namespace EasyRDP.Core.Transport
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Sockets;
    using System.Threading;
    using NLog;

    /// <summary>TCP 传输监听器。实现 ITransportAcceptor。</summary>
    public class TcpTransportAcceptor : ITransportAcceptor
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// 存活连接数硬上限（含 pending 与已握手建立）：防止恶意客户端批量建连耗尽 FD/线程/内存。
        /// 已握手会话数由 TransportHost 的 maxSessions 单独限制；此处是 accept 层的总连接数兜底。
        /// </summary>
        private const int MaxPendingConnections = 16;

        private TcpListener _listener;
        private readonly List<TcpTransport> _transports = new List<TcpTransport>();
        private readonly object _lock = new object();
        private volatile bool _running;

        public event EventHandler<TransportAcceptedEventArgs> ClientConnected;
        public LogCallback OnLog { get; set; }

        /// <summary>监听 "port"（0.0.0.0）或 "host:port" 端点。</summary>
        public void Start(string endpoint)
        {
            int port;
            if (!TryParseListenEndpoint(endpoint, out port))
            {
                Logger.Error("Start failed: invalid endpoint '{0}'", endpoint);
                Log("Start failed: invalid endpoint '" + endpoint + "'");
                return;
            }

            lock (_lock)
            {
                if (_running)
                    return;
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Start();
                _running = true;
            }

            var acceptThread = new Thread(AcceptLoop);
            acceptThread.IsBackground = true;
            acceptThread.Start();
        }

        public void Stop()
        {
            _running = false;
            try { if (_listener != null) _listener.Stop(); } catch { }

            TcpTransport[] transports;
            lock (_lock)
            {
                transports = _transports.ToArray();
                _transports.Clear();
            }
            foreach (var t in transports)
            {
                try { t.Disconnect(); } catch { }
            }
        }

        public void Dispose()
        {
            Stop();
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    TcpClient client = _listener.AcceptTcpClient();
                    client.NoDelay = true;

                    // 总连接数限流：存活连接（含 pending 与已建立）超限直接断开。
                    // TransportHost 的 maxSessions 在握手成功后另行限制活跃会话数。
                    int pendingCount;
                    lock (_lock) { pendingCount = _transports.Count; }
                    if (pendingCount >= MaxPendingConnections)
                    {
                        Logger.Warn("Connection rejected: pending connections {0} >= max {1}",
                            pendingCount, MaxPendingConnections);
                        try { client.Close(); } catch { }
                        continue;
                    }

                    string remote = "";
                    try { remote = client.Client.RemoteEndPoint.ToString(); } catch { }

                    var transport = new TcpTransport(client, remote);
                    transport.Disconnected += (s, e) => RemoveTransport(transport);
                    lock (_lock) { _transports.Add(transport); }

                    Logger.Info("Client connected: {0}", remote);
                    Log("Client connected: " + remote);

                    // fire ClientConnected（同步）：TransportHost 在处理器内完成订阅 +
                    // SessionId 分配 + transport.Start()
                    var handler = ClientConnected;
                    if (handler != null)
                        handler(this, new TransportAcceptedEventArgs(transport, remote));
                }
                catch (SocketException ex)
                {
                    if (!_running) break;
                    Logger.Warn("Accept socket error: {0}", ex.Message);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Accept error");
                    Log("Accept error: " + ex.Message);
                }
            }
        }

        private void RemoveTransport(TcpTransport transport)
        {
            lock (_lock) { _transports.Remove(transport); }
        }

        /// <summary>解析监听端点："port" 或 "host:port"（取端口部分）。</summary>
        private static bool TryParseListenEndpoint(string endpoint, out int port)
        {
            port = 0;
            if (string.IsNullOrEmpty(endpoint))
                return false;
            string portStr = endpoint;
            int idx = endpoint.LastIndexOf(':');
            if (idx >= 0)
                portStr = endpoint.Substring(idx + 1);
            if (!int.TryParse(portStr, out port))
                return false;
            if (port < 1 || port > 65535)
                return false;
            return true;
        }

        private void Log(string message)
        {
            var cb = OnLog;
            if (cb != null)
                cb(message);
        }
    }
}
