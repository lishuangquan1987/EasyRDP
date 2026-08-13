namespace EasyRDP.Core.Transport
{
    using System;
    using System.IO;
    using System.Net.Sockets;
    using System.Text;
    using NLog;

    /// <summary>WebSocket 客户端建连器。实现 ITransportConnector（TCP 连接 + RFC 6455 握手）。</summary>
    public class WebSocketTransportConnector : ITransportConnector
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public LogCallback OnLog { get; set; }

        /// <summary>按 "host:port" 建立 WebSocket 连接（默认路径 "/"）。失败返回 null。</summary>
        public ITransport Connect(string endpoint, int timeoutMs)
        {
            string host;
            int port;
            if (!TryParseEndpoint(endpoint, out host, out port))
            {
                Log("Connect failed: invalid endpoint '" + endpoint + "'");
                return null;
            }

            TcpClient tcp = null;
            try
            {
                tcp = new TcpClient();
                tcp.NoDelay = true;
                var result = tcp.BeginConnect(host, port, null, null);
                if (!result.AsyncWaitHandle.WaitOne(timeoutMs))
                {
                    try { tcp.Close(); } catch { }
                    Log("Connection timeout to " + endpoint);
                    return null;
                }
                tcp.EndConnect(result);

                var stream = tcp.GetStream();
                // 握手阶段设置读超时，防止服务端不响应时永久阻塞
                stream.ReadTimeout = 10000;

                // 客户端握手请求
                string key = GenerateKey();
                string request =
                    "GET / HTTP/1.1\r\n" +
                    "Host: " + host + ":" + port + "\r\n" +
                    "Upgrade: websocket\r\n" +
                    "Connection: Upgrade\r\n" +
                    "Sec-WebSocket-Key: " + key + "\r\n" +
                    "Sec-WebSocket-Version: 13\r\n" +
                    "\r\n";
                byte[] reqBytes = Encoding.ASCII.GetBytes(request);
                stream.Write(reqBytes, 0, reqBytes.Length);
                stream.Flush();

                // 读响应头
                string response = ReadHttpHeaders(stream);
                if (response.IndexOf("101") < 0)
                {
                    try { tcp.Close(); } catch { }
                    Log("WebSocket handshake rejected: " + response.Replace("\r", " ").Replace("\n", " "));
                    return null;
                }

                string expectedAccept = WebSocketTransport.ComputeAcceptKey(key);
                if (response.IndexOf("Sec-WebSocket-Accept: " + expectedAccept) < 0)
                {
                    try { tcp.Close(); } catch { }
                    Log("WebSocket handshake accept-key mismatch");
                    return null;
                }

                Logger.Info("WebSocket connected to {0}:{1}", host, port);
                Log("Connected to " + endpoint);
                // 握手完成，清除读超时：否则空闲 10s 后 ReceiveLoop 的 ReadByte 会抛 IOException 断连
                stream.ReadTimeout = System.Threading.Timeout.Infinite;
                return new WebSocketTransport(stream, true);
            }
            catch (Exception ex)
            {
                if (tcp != null) { try { tcp.Close(); } catch { } }
                Logger.Error(ex, "WebSocket connect to {0} failed", endpoint);
                Log("Connect failed: " + ex.Message);
                return null;
            }
        }

        private static string GenerateKey()
        {
            byte[] bytes = new byte[16];
            // Sec-WebSocket-Key 要求不可预测（RFC 6455 4.1），用加密级随机源
            using (var rng = new System.Security.Cryptography.RNGCryptoServiceProvider())
            {
                rng.GetBytes(bytes);
            }
            return Convert.ToBase64String(bytes);
        }

        private static string ReadHttpHeaders(Stream stream)
        {
            var sb = new StringBuilder();
            var buf = new byte[1];
            // 整体 deadline（10s）：防止服务端慢速响应时无限阻塞
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

        private static bool TryParseEndpoint(string endpoint, out string host, out int port)
        {
            host = null;
            port = 0;
            if (string.IsNullOrEmpty(endpoint))
                return false;
            int idx = endpoint.LastIndexOf(':');
            if (idx <= 0 || idx >= endpoint.Length - 1)
                return false;
            host = endpoint.Substring(0, idx);
            if (!int.TryParse(endpoint.Substring(idx + 1), out port))
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
