using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace EasyRDP.Core.Transport
{
    /// <summary>
    /// UDP 服务端传输实现。
    /// 无连接监听，按远程端点区分客户端会话。
    /// 通过 <see cref="UdpTransportOptions"/> 配置所有通讯参数。
    /// 包含僵尸会话超时清理机制。
    /// </summary>
    public class UdpTransportServer : ITransportServer
    {
        private readonly UdpTransportOptions _options;

        private readonly Dictionary<string, ClientSession> _sessions = new Dictionary<string, ClientSession>();
        private readonly Dictionary<uint, string> _sessionEndpoints = new Dictionary<uint, string>();
        private readonly Dictionary<string, IPEndPoint> _sessionTargets = new Dictionary<string, IPEndPoint>();
        private uint _nextSessionId = 1;
        private readonly object _lock = new object();

        private UdpClient _udpListener;
        private Thread _receiveThread;
        private volatile bool _running;
        private volatile bool _stopped;

        /// <inheritdoc />
        public event EventHandler<ConnectionEventArgs> ClientConnected;

        /// <inheritdoc />
        public event EventHandler<ConnectionEventArgs> ClientDisconnected;

        /// <inheritdoc />
        public event EventHandler<MessageReceivedEventArgs> MessageReceived;

        /// <inheritdoc />
        public LogCallback OnLog { get; set; }

        private class ClientSession
        {
            public ConnectionEventArgs Args;
            public DateTime LastActivity;
        }

        /// <summary>
        /// 使用默认配置创建 UDP 服务端传输实例。
        /// </summary>
        public UdpTransportServer()
            : this(UdpTransportOptions.Default)
        {
        }

        /// <summary>
        /// 使用自定义配置创建 UDP 服务端传输实例。
        /// 传入 null 等同使用默认配置。
        /// </summary>
        public UdpTransportServer(UdpTransportOptions options)
        {
            _options = options ?? UdpTransportOptions.Default;
        }

        /// <inheritdoc />
        public void Start(int port)
        {
            _udpListener = new UdpClient(new IPEndPoint(IPAddress.Any, port));
            _udpListener.Client.ReceiveTimeout = _options.ReceiveTimeoutMs;
            _udpListener.Client.ReceiveBufferSize = _options.ReceiveBufferSize;
            _running = true;
            _stopped = false;

            _receiveThread = new Thread(ReceiveLoop);
            _receiveThread.IsBackground = true;
            _receiveThread.Name = "EasyRDP-UDP-Listen";
            _receiveThread.Start();

            Log(LogLevel.Info, string.Format("UDP server started on port {0}", port));
        }

        /// <inheritdoc />
        public void Stop()
        {
            if (_stopped)
                return;
            _stopped = true;
            _running = false;

            if (_udpListener != null)
            {
                try { _udpListener.Close(); } catch { }
            }

            if (_receiveThread != null && _receiveThread.IsAlive)
                _receiveThread.Join(1000);

            List<ClientSession> sessionsToClose;
            lock (_lock)
            {
                sessionsToClose = new List<ClientSession>(_sessions.Values);
                _sessions.Clear();
                _sessionEndpoints.Clear();
                _sessionTargets.Clear();
            }

            foreach (var session in sessionsToClose)
            {
                try
                {
                    var handler = ClientDisconnected;
                    if (handler != null)
                        handler(this, session.Args);
                }
                catch { }
            }

            Log(LogLevel.Info, "UDP server stopped");
        }

        /// <inheritdoc />
        public void SendTo(uint sessionId, byte[] data)
        {
            IPEndPoint target;
            lock (_lock)
            {
                string endpointKey;
                if (!_sessionEndpoints.TryGetValue(sessionId, out endpointKey))
                    return;

                if (!_sessionTargets.TryGetValue(endpointKey, out target))
                    return;
            }

            try
            {
                _udpListener.Send(data, data.Length, target);
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, string.Format("UDP send failed to {0}: {1}", sessionId, ex.Message));
            }
        }

        /// <inheritdoc />
        public void Disconnect(uint sessionId)
        {
            ClientSession session = null;
            string endpointKey;
            lock (_lock)
            {
                if (!_sessionEndpoints.TryGetValue(sessionId, out endpointKey))
                    return;

                _sessionEndpoints.Remove(sessionId);
                _sessionTargets.Remove(endpointKey);
                if (_sessions.TryGetValue(endpointKey, out session))
                {
                    _sessions.Remove(endpointKey);
                }
            }

            if (session != null)
            {
                try
                {
                    var handler = ClientDisconnected;
                    if (handler != null)
                        handler(this, session.Args);
                }
                catch { }
            }
        }

        private void ReceiveLoop()
        {
            DateTime lastCleanup = DateTime.UtcNow;
            TimeSpan sessionTimeout = TimeSpan.FromSeconds(_options.SessionTimeoutSeconds);

            while (_running)
            {
                try
                {
                    IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data = _udpListener.Receive(ref remote);

                    string endpointKey = remote.ToString();
                    uint sessionId = GetOrCreateSession(endpointKey, remote);

                    // 跳过注册探测（单字节 0x00），不触发 MessageReceived
                    if (data.Length == 1 && data[0] == 0x00)
                    {
                        // 继续检查僵尸会话清理时机
                        DateTime now = DateTime.UtcNow;
                        if (now - lastCleanup > TimeSpan.FromSeconds(10))
                        {
                            CleanupStaleSessions(now, sessionTimeout);
                            lastCleanup = now;
                        }
                        continue;
                    }

                    var msg = Protocol.MessageCodec.Decode(data);
                    if (msg != null)
                    {
                        var handler = MessageReceived;
                        if (handler != null)
                        {
                            var args = new MessageReceivedEventArgs
                            {
                                Message = msg,
                                SessionId = sessionId
                            };
                            handler(this, args);
                        }
                    }

                    // 定期清理僵尸会话
                    DateTime now2 = DateTime.UtcNow;
                    if (now2 - lastCleanup > TimeSpan.FromSeconds(10))
                    {
                        CleanupStaleSessions(now2, sessionTimeout);
                        lastCleanup = now2;
                    }
                }
                catch (SocketException ex)
                {
                    if (ex.SocketErrorCode == SocketError.TimedOut)
                    {
                        DateTime now = DateTime.UtcNow;
                        if (now - lastCleanup > TimeSpan.FromSeconds(10))
                        {
                            CleanupStaleSessions(now, sessionTimeout);
                            lastCleanup = now;
                        }

                        if (_running)
                            continue;
                        break;
                    }

                    if (_running)
                        Log(LogLevel.Error, string.Format("UDP receive socket error: {0}", ex.SocketErrorCode));
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (_running)
                        Log(LogLevel.Error, string.Format("UDP receive error: {0}", ex.Message));
                }
            }
        }

        private uint GetOrCreateSession(string endpointKey, IPEndPoint remote)
        {
            lock (_lock)
            {
                ClientSession existing;
                if (_sessions.TryGetValue(endpointKey, out existing))
                {
                    existing.LastActivity = DateTime.UtcNow;
                    _sessionTargets[endpointKey] = remote;
                    return existing.Args.SessionId;
                }

                uint sessionId = _nextSessionId;
                _nextSessionId = _nextSessionId + 1;

                var session = new ClientSession
                {
                    Args = new ConnectionEventArgs
                    {
                        SessionId = sessionId,
                        RemoteEndPoint = endpointKey
                    },
                    LastActivity = DateTime.UtcNow
                };

                _sessions[endpointKey] = session;
                _sessionEndpoints[sessionId] = endpointKey;
                _sessionTargets[endpointKey] = remote;

                Log(LogLevel.Info, string.Format("UDP client {0} registered: {1}", sessionId, endpointKey));

                var handler = ClientConnected;
                if (handler != null)
                    handler(this, session.Args);

                return sessionId;
            }
        }

        private void CleanupStaleSessions(DateTime now, TimeSpan timeout)
        {
            List<ClientSession> expired;
            lock (_lock)
            {
                expired = new List<ClientSession>();
                var keysToRemove = new List<string>();

                foreach (var kvp in _sessions)
                {
                    if (now - kvp.Value.LastActivity > timeout)
                    {
                        expired.Add(kvp.Value);
                        keysToRemove.Add(kvp.Key);
                    }
                }

                foreach (var key in keysToRemove)
                {
                    _sessions.Remove(key);
                    _sessionTargets.Remove(key);
                }

                foreach (var session in expired)
                {
                    _sessionEndpoints.Remove(session.Args.SessionId);
                }
            }

            foreach (var session in expired)
            {
                Log(LogLevel.Info, string.Format("UDP client {0} timed out: {1}",
                    session.Args.SessionId, session.Args.RemoteEndPoint));

                try
                {
                    var handler = ClientDisconnected;
                    if (handler != null)
                        handler(this, session.Args);
                }
                catch { }
            }
        }

        private void Log(LogLevel level, string message)
        {
            var handler = OnLog;
            if (handler != null)
                handler(level, message);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Stop();
        }
    }
}
