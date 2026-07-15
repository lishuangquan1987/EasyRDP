using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;

namespace EasyRDP.Core.Transport
{
    /// <summary>
    /// 服务端传输层——按 TransportMode 创建通道组合，管理多客户端会话。
    /// </summary>
    public class ServerTransport : IServerTransport
    {
        private TransportMode _mode;
        private TcpListener _tcpListener;
        private ITransportChannel _udpChannel;
        private Thread _acceptThread;
        private volatile bool _running;

        private readonly Dictionary<uint, ClientSession> _sessions = new Dictionary<uint, ClientSession>();
        private uint _nextSessionId = 1;
        private readonly object _lock = new object();

        public event EventHandler<ConnectionEventArgs> ClientConnected;
        public event EventHandler<ConnectionEventArgs> ClientDisconnected;
        public event EventHandler<MessageReceivedEventArgs> MessageReceived;
        public LogCallback OnLog { get; set; }

        private class ClientSession
        {
            public TcpChannel Tcp;
            public ConnectionEventArgs Args;
            public bool Disconnected;
        }

        public ServerTransport()
        {
        }

        public void Start(int port, TransportMode mode)
        {
            _mode = mode;

            // TCP listener (always on)
            _tcpListener = new TcpListener(System.Net.IPAddress.Any, port);
            _tcpListener.Start();
            _running = true;

            _acceptThread = new Thread(AcceptLoop);
            _acceptThread.IsBackground = true;
            _acceptThread.Name = "EasyRDP-TCP-Accept";
            _acceptThread.Start();

            // Optional UDP
            if (mode == TransportMode.TcpAndUdp)
            {
                _udpChannel = new UdpChannel();
                _udpChannel.OnLog = OnLog;
                _udpChannel.MessageReceived += OnUdpMessage;
                _udpChannel.Bind(port + 1); // UDP on TCP port + 1
            }

            Log(LogLevel.Info, string.Format("Server started on port {0}, mode={1}", port, mode));
        }

        public void Stop()
        {
            _running = false;

            try { _tcpListener.Stop(); } catch { }

            if (_udpChannel != null)
                _udpChannel.Close();

            if (_acceptThread != null && _acceptThread.IsAlive)
                _acceptThread.Join(1000);

            lock (_lock)
            {
                foreach (var kvp in _sessions)
                    kvp.Value.Tcp.Close();
                _sessions.Clear();
            }

            Log(LogLevel.Info, "Server stopped");
        }

        public void SendTo(uint sessionId, byte[] data)
        {
            TcpChannel tcp = null;
            lock (_lock)
            {
                ClientSession session;
                if (_sessions.TryGetValue(sessionId, out session))
                    tcp = session.Tcp;
            }

            if (tcp != null)
                tcp.Send(data);
        }

        public void Broadcast(byte[] data)
        {
            if (_udpChannel != null)
            {
                // TcpAndUdp mode: use UDP broadcast
                _udpChannel.Send(data);
            }
            else
            {
                // TCP-only mode: iterate all clients
                lock (_lock)
                {
                    foreach (var kvp in _sessions)
                        kvp.Value.Tcp.Send(data);
                }
            }
        }

        public void Disconnect(uint sessionId)
        {
            lock (_lock)
            {
                ClientSession session;
                if (_sessions.TryGetValue(sessionId, out session))
                {
                    session.Tcp.Close();
                    _sessions.Remove(sessionId);
                }
            }
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
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

            session.Tcp = new TcpChannel();
            session.Tcp.OnLog = OnLog;
            session.Tcp.MessageReceived += (sender, args) =>
            {
                args.SessionId = sessionId;
                var handler = MessageReceived;
                if (handler != null)
                    handler(this, args);
            };
            session.Tcp.Disconnected += (sender, args) =>
            {
                lock (_lock)
                {
                    if (!session.Disconnected)
                    {
                        session.Disconnected = true;
                        _sessions.Remove(sessionId);
                    }
                }

                var handler = ClientDisconnected;
                if (handler != null)
                    handler(this, session.Args);
            };
            session.Tcp.StartWithClient(client);

            Log(LogLevel.Info, string.Format("Client {0} connected: {1}", sessionId, session.Args.RemoteEndPoint));

            var connectedHandler = ClientConnected;
            if (connectedHandler != null)
                connectedHandler(this, session.Args);
        }

        private void OnUdpMessage(object sender, MessageReceivedEventArgs e)
        {
            e.SessionId = 0;
            var handler = MessageReceived;
            if (handler != null)
                handler(this, e);
        }

        private void Log(LogLevel level, string message)
        {
            var handler = OnLog;
            if (handler != null)
                handler(level, message);
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
