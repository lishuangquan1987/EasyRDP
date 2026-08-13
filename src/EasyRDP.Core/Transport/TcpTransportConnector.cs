namespace EasyRDP.Core.Transport
{
    using System;
    using System.Net.Sockets;
    using NLog;

    /// <summary>TCP 客户端建连器。实现 ITransportConnector。</summary>
    public class TcpTransportConnector : ITransportConnector
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public LogCallback OnLog { get; set; }

        /// <summary>
        /// 按 "host:port" endpoint 建立连接。成功返回「已连接未 Start 的」TcpTransport，
        /// 失败（解析失败/超时/拒绝）返回 null。
        /// </summary>
        public ITransport Connect(string endpoint, int timeoutMs)
        {
            string host;
            int port;
            if (!TryParseEndpoint(endpoint, out host, out port))
            {
                Logger.Error("Connect failed: invalid endpoint '{0}'", endpoint);
                Log("Connect failed: invalid endpoint '" + endpoint + "'");
                return null;
            }

            try
            {
                var client = new TcpClient();
                client.NoDelay = true;
                var result = client.BeginConnect(host, port, null, null);
                if (!result.AsyncWaitHandle.WaitOne(timeoutMs))
                {
                    Logger.Warn("Connection timeout to {0} after {1}ms", endpoint, timeoutMs);
                    Log("Connection timeout to " + endpoint);
                    try { client.Close(); } catch { }
                    return null;
                }
                client.EndConnect(result);

                string remote = endpoint;
                try { remote = client.Client.RemoteEndPoint.ToString(); } catch { }

                Logger.Info("Connected to {0}", endpoint);
                Log("Connected to " + endpoint);
                return new TcpTransport(client, remote);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Connect to {0} failed", endpoint);
                Log("Connect failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>解析 "host:port" 端点（IPv4/域名，不支持带括号 IPv6）。</summary>
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
