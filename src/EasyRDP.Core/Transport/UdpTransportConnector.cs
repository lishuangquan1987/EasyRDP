namespace EasyRDP.Core.Transport
{
    using System;
    using System.Net;
    using System.Net.Sockets;
    using NLog;

    /// <summary>UDP 客户端建连器。实现 ITransportConnector。</summary>
    public class UdpTransportConnector : ITransportConnector
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public LogCallback OnLog { get; set; }

        /// <summary>
        /// 按 "host:port" 建立 UDP「连接」。UDP 无握手，Connect 仅绑定对端地址；
        /// 失败（解析失败/端口不可达等）返回 null。
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
                IPAddress addr;
                if (!IPAddress.TryParse(host, out addr))
                {
                    // 域名解析
                    var addrs = Dns.GetHostAddresses(host);
                    if (addrs == null || addrs.Length == 0)
                        return null;
                    addr = addrs[0];
                }

                var remote = new IPEndPoint(addr, port);
                var client = new UdpClient();
                client.Connect(remote);

                Logger.Info("UDP connected to {0}:{1}", host, port);
                Log("Connected to " + endpoint);
                return new UdpTransport(client, remote, true);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Connect to {0} failed", endpoint);
                Log("Connect failed: " + ex.Message);
                return null;
            }
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
