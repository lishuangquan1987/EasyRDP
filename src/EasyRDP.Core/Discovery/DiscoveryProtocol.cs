namespace EasyRDP.Core.Discovery
{
    using System;
    using System.Net;
    using System.Text;

    /// <summary>局域网发现协议常量。</summary>
    public static class DiscoveryConstants
    {
        /// <summary>UDP 发现端口（与 TCP 业务端口分离，避免混用）。</summary>
        public const int UdpPort = 2001;

        /// <summary>协议版本。</summary>
        public const byte Version = 1;

        /// <summary>请求 Magic。</summary>
        public static readonly byte[] RequestMagic = new byte[] { 0x45, 0x52, 0x44, 0x3F }; // "ERD?"

        /// <summary>应答 Magic。</summary>
        public static readonly byte[] ResponseMagic = new byte[] { 0x45, 0x52, 0x44, 0x21 }; // "ERD!"

        /// <summary>请求类型。</summary>
        public const byte TypeRequest = 1;

        /// <summary>应答类型。</summary>
        public const byte TypeResponse = 2;

        /// <summary>Nonce 长度（字节）。</summary>
        public const int NonceLength = 8;

        /// <summary>显示名最大字节数。</summary>
        public const int MaxNameBytes = 64;

        /// <summary>认证模式：口令。</summary>
        public const byte AuthPassword = 0;

        /// <summary>认证模式：免密码审批。</summary>
        public const byte AuthApprove = 1;

        /// <summary>请求报文长度：Magic(4)+Ver(1)+Type(1)+Nonce(8)。</summary>
        public const int RequestLength = 14;
    }

    /// <summary>发现到的服务端（客户端列表项）。</summary>
    public class DiscoveredServer
    {
        /// <summary>服务端 IP（应答源地址）。</summary>
        public IPAddress Address { get; set; }

        /// <summary>服务端 TCP 业务端口。</summary>
        public int TcpPort { get; set; }

        /// <summary>认证模式：0=口令，1=免密码审批。</summary>
        public byte AuthMode { get; set; }

        /// <summary>是否已达会话上限。</summary>
        public bool Busy { get; set; }

        /// <summary>服务端显示名（计算机名）。WPF 绑定只支持属性，不能是字段。</summary>
        public string Name { get; set; }

        /// <summary>构造：C# 5（net40 目标）不支持自动属性初始值设定项，在此初始化。</summary>
        public DiscoveredServer()
        {
            Name = "";
        }

        /// <summary>去重键：ip:tcpPort。</summary>
        public string Key
        {
            get { return Address + ":" + TcpPort; }
        }

        /// <summary>端点文本（ip:port）。</summary>
        public string EndpointText
        {
            get { return Address + ":" + TcpPort; }
        }

        /// <summary>认证模式文本。</summary>
        public string AuthModeText
        {
            get { return AuthMode == DiscoveryConstants.AuthApprove ? "Approval" : "Password"; }
        }

        /// <summary>状态文本（忙/可用）。</summary>
        public string StateText
        {
            get { return Busy ? "Busy" : "Available"; }
        }
    }

    /// <summary>
    /// 局域网发现报文编解码。请求/应答都是固定小包（≤ 80 字节），
    /// 不含用户名、密码等任何敏感信息。
    /// </summary>
    public static class DiscoveryProtocol
    {
        /// <summary>构建探测请求；nonce 用于把应答与本次扫描配对。</summary>
        public static byte[] BuildRequest(out byte[] nonce)
        {
            nonce = new byte[DiscoveryConstants.NonceLength];
            using (var rng = new System.Security.Cryptography.RNGCryptoServiceProvider())
                rng.GetBytes(nonce);

            byte[] packet = new byte[DiscoveryConstants.RequestLength];
            Buffer.BlockCopy(DiscoveryConstants.RequestMagic, 0, packet, 0, 4);
            packet[4] = DiscoveryConstants.Version;
            packet[5] = DiscoveryConstants.TypeRequest;
            Buffer.BlockCopy(nonce, 0, packet, 6, DiscoveryConstants.NonceLength);
            return packet;
        }

        /// <summary>校验探测请求，取出 nonce。</summary>
        public static bool TryParseRequest(byte[] data, out byte[] nonce)
        {
            nonce = null;
            if (data == null || data.Length < DiscoveryConstants.RequestLength)
                return false;
            for (int i = 0; i < 4; i++)
            {
                if (data[i] != DiscoveryConstants.RequestMagic[i]) return false;
            }
            if (data[4] != DiscoveryConstants.Version) return false;
            if (data[5] != DiscoveryConstants.TypeRequest) return false;
            nonce = new byte[DiscoveryConstants.NonceLength];
            Buffer.BlockCopy(data, 6, nonce, 0, DiscoveryConstants.NonceLength);
            return true;
        }

        /// <summary>构建应答报文。</summary>
        public static byte[] BuildResponse(byte[] nonce, int tcpPort, byte authMode, bool busy, string name)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(name ?? "");
            if (nameBytes.Length > DiscoveryConstants.MaxNameBytes)
            {
                byte[] trimmed = new byte[DiscoveryConstants.MaxNameBytes];
                Buffer.BlockCopy(nameBytes, 0, trimmed, 0, DiscoveryConstants.MaxNameBytes);
                nameBytes = trimmed;
            }

            // Magic(4)+Ver(1)+Type(1)+Nonce(8)+Port(2)+Auth(1)+Flags(1)+NameLen(1)+Name
            byte[] packet = new byte[19 + nameBytes.Length];
            Buffer.BlockCopy(DiscoveryConstants.ResponseMagic, 0, packet, 0, 4);
            packet[4] = DiscoveryConstants.Version;
            packet[5] = DiscoveryConstants.TypeResponse;
            if (nonce != null && nonce.Length >= DiscoveryConstants.NonceLength)
                Buffer.BlockCopy(nonce, 0, packet, 6, DiscoveryConstants.NonceLength);
            packet[14] = (byte)(tcpPort & 0xFF);
            packet[15] = (byte)((tcpPort >> 8) & 0xFF);
            packet[16] = authMode;
            packet[17] = (byte)(busy ? 1 : 0);
            packet[18] = (byte)nameBytes.Length;
            if (nameBytes.Length > 0)
                Buffer.BlockCopy(nameBytes, 0, packet, 19, nameBytes.Length);
            return packet;
        }

        /// <summary>
        /// 解析应答：校验 Magic/版本/类型/nonce，返回发现到的服务端。
        /// nonce 不匹配的包（过期或伪造）直接丢弃。
        /// </summary>
        public static bool TryParseResponse(byte[] data, byte[] expectedNonce, IPAddress source,
            out DiscoveredServer server)
        {
            server = null;
            if (data == null || data.Length < 19)
                return false;
            for (int i = 0; i < 4; i++)
            {
                if (data[i] != DiscoveryConstants.ResponseMagic[i]) return false;
            }
            if (data[4] != DiscoveryConstants.Version) return false;
            if (data[5] != DiscoveryConstants.TypeResponse) return false;

            if (expectedNonce != null)
            {
                if (expectedNonce.Length != DiscoveryConstants.NonceLength) return false;
                for (int i = 0; i < DiscoveryConstants.NonceLength; i++)
                {
                    if (data[6 + i] != expectedNonce[i]) return false;
                }
            }

            int nameLen = data[18];
            if (nameLen < 0 || nameLen > DiscoveryConstants.MaxNameBytes
                || data.Length < 19 + nameLen)
                return false;

            server = new DiscoveredServer
            {
                Address = source,
                TcpPort = data[14] | (data[15] << 8),
                AuthMode = data[16],
                Busy = data[17] != 0,
                Name = nameLen > 0
                    ? Encoding.UTF8.GetString(data, 19, nameLen)
                    : (source != null ? source.ToString() : "")
            };
            return server.TcpPort > 0 && server.TcpPort < 65536;
        }

        /// <summary>判断地址是否属于私网/回环（发现只对本机与私网应答）。</summary>
        public static bool IsPrivateOrLoopback(IPAddress address)
        {
            if (address == null) return false;
            if (IPAddress.IsLoopback(address)) return true;

            byte[] b = address.GetAddressBytes();
            if (b.Length != 4) return false; // 发现只支持 IPv4 广播

            if (b[0] == 10) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            if (b[0] == 169 && b[1] == 254) return true; // 链路本地
            return false;
        }
    }
}
