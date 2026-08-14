namespace EasyRDP.Core.Transport
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading;
    using NLog;

    /// <summary>
    /// WebSocket 传输监听器。实现 ITransportAcceptor（TCP 监听 + RFC 6455 服务端握手）。
    /// 每个新连接完成 HTTP Upgrade 握手后产出 WebSocketTransport 并触发 ClientConnected。
    /// </summary>
    public class WebSocketTransportAcceptor : ITransportAcceptor
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        /// <summary>存活连接数硬上限（含 pending 与已握手建立）：防止恶意客户端批量建连耗尽 FD/线程/内存。</summary>
        private const int MaxPendingConnections = 16;

        private TcpListener _listener;
        private readonly List<WebSocketTransport> _transports = new List<WebSocketTransport>();
        private readonly object _lock = new object();
        private volatile bool _running;

        public event EventHandler<TransportAcceptedEventArgs> ClientConnected;
        public LogCallback OnLog { get; set; }

        public void Start(string endpoint)
        {
            int port;
            if (!TryParseListenEndpoint(endpoint, out port))
            {
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

            WebSocketTransport[] transports;
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

                    int pendingCount;
                    lock (_lock) { pendingCount = _transports.Count; }
                    if (pendingCount >= MaxPendingConnections)
                    {
                        try { client.Close(); } catch { }
                        continue;
                    }

                    var stream = client.GetStream();
                    // 握手阶段设置读超时（10s），防止恶意客户端连上不发数据阻塞 AcceptLoop（DoS）；
                    // 握手完成后交由 WebSocketTransport 接收，清除超时。
                    stream.ReadTimeout = 10000;

                    // 读 HTTP Upgrade 请求头（Upgrade 值大小写不敏感）
                    string request = ReadHttpHeaders(stream);
                    if (request.IndexOf("Upgrade: websocket", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        try { client.Close(); } catch { }
                        continue;
                    }

                    string key = ParseSecWebSocketKey(request);
                    if (key == null)
                    {
                        try { client.Close(); } catch { }
                        continue;
                    }

                    string accept = WebSocketTransport.ComputeAcceptKey(key);
                    string response =
                        "HTTP/1.1 101 Switching Protocols\r\n" +
                        "Upgrade: websocket\r\n" +
                        "Connection: Upgrade\r\n" +
                        "Sec-WebSocket-Accept: " + accept + "\r\n" +
                        "\r\n";
                    byte[] respBytes = Encoding.ASCII.GetBytes(response);
                    stream.Write(respBytes, 0, respBytes.Length);
                    stream.Flush();

                    // 握手完成，清除读超时（后续帧读取由 WebSocketTransport 负责）
                    stream.ReadTimeout = System.Threading.Timeout.Infinite;

                    string remote = "";
                    try { remote = client.Client.RemoteEndPoint.ToString(); } catch { }

                    var transport = new WebSocketTransport(stream, false);
                    transport.Disconnected += (s, e) => RemoveTransport(transport);
                    lock (_lock) { _transports.Add(transport); }

                    Logger.Info("WebSocket client connected: {0}", remote);
                    Log("Client connected: " + remote);

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
                }
            }
        }

        private void RemoveTransport(WebSocketTransport transport)
        {
            lock (_lock) { _transports.Remove(transport); }
        }

        private static string ReadHttpHeaders(System.IO.Stream stream)
        {
            var sb = new StringBuilder();
            var buf = new byte[1];
            // 整体 deadline（10s）：ReadTimeout 只是单次 Read 超时，逐字节攻击者每 9s 发 1 字节即可无限阻塞；
            // 用 Stopwatch 限制握手头读取总时长。
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sb.Length < 65536 && sw.ElapsedMilliseconds < 10000)
            {
                int n = stream.Read(buf, 0, 1);
                if (n <= 0)
                    break;
                sb.Append((char)buf[0]);
                if (sb.Length >= 4 &&
                    sb[sb.Length - 4] == '\r' && sb[sb.Length - 3] == '\n' &&
                    sb[sb.Length - 2] == '\r' && sb[sb.Length - 1] == '\n')
                    break;
            }
            return sb.ToString();
        }

        private static string ParseSecWebSocketKey(string request)
        {
            const string marker = "Sec-WebSocket-Key:";
            int idx = request.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return null;
            int start = idx + marker.Length;
            while (start < request.Length && request[start] == ' ')
                start++;
            int end = start;
            while (end < request.Length && request[end] != '\r' && request[end] != '\n')
                end++;
            if (end <= start)
                return null;
            return request.Substring(start, end - start).Trim();
        }

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
