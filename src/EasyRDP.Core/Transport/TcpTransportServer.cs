using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace EasyRDP.Core.Transport
{
    /// <summary>
    /// TCP 服务端传输实现。
    /// 使用 TcpListener 监听，管理多个客户端会话。
    /// 通过 <see cref="TcpTransportOptions"/> 配置所有通讯参数。
    /// </summary>
    public class TcpTransportServer : ITransportServer
    {
        private readonly TcpTransportOptions _options;
        private TcpListener _tcpListener;
        private Thread _acceptThread;
        private volatile bool _running;
        private volatile bool _stopped;

        private readonly Dictionary<uint, ClientSession> _sessions = new Dictionary<uint, ClientSession>();
        private uint _nextSessionId = 1;
        private readonly object _lock = new object();

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
            public TcpTransportClient Transport;
            public ConnectionEventArgs Args;
            public bool Disconnected;
        }

        /// <summary>
        /// 使用默认配置创建 TCP 服务端传输实例。
        /// </summary>
        public TcpTransportServer()
            : this(TcpTransportOptions.Default)
        {
        }

        /// <summary>
        /// 使用自定义配置创建 TCP 服务端传输实例。
        /// 传入 null 等同使用默认配置。
        /// </summary>
        public TcpTransportServer(TcpTransportOptions options)
        {
            _options = options ?? TcpTransportOptions.Default;
        }

        /// <inheritdoc />
        public void Start(int port)
        {
            _tcpListener = new TcpListener(IPAddress.Any, port);
            _tcpListener.Start(_options.Backlog);
            _running = true;
            _stopped = false;

            _acceptThread = new Thread(AcceptLoop);
            _acceptThread.IsBackground = true;
            _acceptThread.Name = "EasyRDP-TCP-Accept";
            _acceptThread.Start();

            Log(LogLevel.Info, string.Format("TCP server started on port {0} (backlog={1})", port, _options.Backlog));
        }

        /// <inheritdoc />
        public void Stop()
        {
            if (_stopped)
                return;
            _stopped = true;
            _running = false;

            if (_tcpListener != null)
            {
                try { _tcpListener.Stop(); } catch { }
            }

            if (_acceptThread != null && _acceptThread.IsAlive)
                _acceptThread.Join(1000);

            List<ClientSession> sessionsToClose;
            lock (_lock)
            {
                sessionsToClose = new List<ClientSession>(_sessions.Values);
                _sessions.Clear();
            }

            foreach (var session in sessionsToClose)
            {
                try
                {
                    if (!session.Disconnected)
                    {
                        session.Disconnected = true;
                        var handler = ClientDisconnected;
                        if (handler != null)
                            handler(this, session.Args);
                    }
                }
                catch { }

                try { session.Transport.Dispose(); } catch { }
            }

            Log(LogLevel.Info, "TCP server stopped");
        }

        /// <inheritdoc />
        public void SendTo(uint sessionId, byte[] data)
        {
            TcpTransportClient transport = null;
            lock (_lock)
            {
                ClientSession session;
                if (_sessions.TryGetValue(sessionId, out session))
                    transport = session.Transport;
            }

            if (transport != null)
                transport.Send(data);
        }

        /// <inheritdoc />
        public void Disconnect(uint sessionId)
        {
            ClientSession session = null;
            lock (_lock)
            {
                if (_sessions.TryGetValue(sessionId, out session))
                {
                    session.Disconnected = true;
                    _sessions.Remove(sessionId);
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

                try { session.Transport.Dispose(); } catch { }
            }
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    if (_tcpListener == null)
                        break;

                    TcpClient client = _tcpListener.AcceptTcpClient();
                    OnTcpClientAccepted(client);
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { break; }
                catch (Exception ex)
                {
                    if (_running)
                        Log(LogLevel.Error, string.Format("Accept error: {0}", ex.Message));
                }
            }
        }

        private void OnTcpClientAccepted(TcpClient client)
        {
            // MaxClients 检查
            if (_options.MaxClients > 0)
            {
                lock (_lock)
                {
                    if (_sessions.Count >= _options.MaxClients)
                    {
                        Log(LogLevel.Warning, string.Format(
                            "Client rejected: server full ({0}/{1})", _sessions.Count, _options.MaxClients));
                        try { client.Close(); } catch { }
                        return;
                    }
                }
            }

            uint sessionId;
            ClientSession session;
            lock (_lock)
            {
                sessionId = _nextSessionId;
                _nextSessionId = _nextSessionId + 1;

                session = new ClientSession
                {
                    Args = new ConnectionEventArgs
                    {
                        SessionId = sessionId,
                        RemoteEndPoint = client.Client.RemoteEndPoint != null
                            ? client.Client.RemoteEndPoint.ToString()
                            : "unknown"
                    },
                    Disconnected = false
                };

                _sessions[sessionId] = session;
            }

            // 继承服务端 Options 创建内部传输实例
            session.Transport = new TcpTransportClient(_options);
            session.Transport.OnLog = OnLog;
            session.Transport.MessageReceived += (sender, args) =>
            {
                args.SessionId = sessionId;
                var handler = MessageReceived;
                if (handler != null)
                    handler(this, args);
            };
            session.Transport.Disconnected += (sender, args) =>
            {
                bool fireEvent = false;
                lock (_lock)
                {
                    if (!session.Disconnected)
                    {
                        session.Disconnected = true;
                        _sessions.Remove(sessionId);
                        fireEvent = true;
                    }
                }

                if (fireEvent)
                {
                    var handler = ClientDisconnected;
                    if (handler != null)
                        handler(this, session.Args);
                }
            };

            session.Transport.StartWithClient(client);

            Log(LogLevel.Info, string.Format("Client {0} connected: {1}", sessionId, session.Args.RemoteEndPoint));

            var connectedHandler = ClientConnected;
            if (connectedHandler != null)
                connectedHandler(this, session.Args);
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
