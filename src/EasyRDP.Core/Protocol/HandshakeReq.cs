namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 客户端握手请求（v5 第一段，不含任何密码材料）。
    /// Payload 布局: Version(1) Capabilities(1) Flags(1)
    ///               UsernameLen(2) Username(*) ClientNonce(16)
    /// 密码认证由服务端下发 AuthChallenge 后，客户端再发 HandshakeProof 完成。
    /// </summary>
    public class HandshakeReq
    {
        /// <summary>客户端请求服务端人工审批（免密码模式）；服务端仍按其 AUTH 模式决定。</summary>
        public const byte FlagApproveRequest = 0x01;

        /// <summary>Protocol version of the client.</summary>
        public byte Version;
        /// <summary>Bitmask of supported codec capabilities.</summary>
        public CodecCapabilities Capabilities;
        /// <summary>客户端能力标志（见 Flag* 常量）。</summary>
        public byte Flags;
        /// <summary>Username for authentication (also shown in the approval prompt).</summary>
        public string Username;
        /// <summary>客户端随机数（16 字节），用于挑战-响应防重放。</summary>
        public byte[] ClientNonce;

        public HandshakeReq()
        {
            Version = Constants.ProtocolVersion;
            Username = "";
            ClientNonce = new byte[16];
        }

        /// <summary>序列化为 payload 字节。</summary>
        public byte[] Pack()
        {
            var bp = new BinaryPacker();
            bp.WriteByte(Version);
            bp.WriteByte((byte)Capabilities);
            bp.WriteByte(Flags);
            bp.WriteString(Username);
            bp.WriteRaw(ClientNonce);
            return bp.GetBytes();
        }

        /// <summary>从 payload 字节反序列化。</summary>
        public static HandshakeReq Unpack(byte[] data)
        {
            var bp = BinaryPacker.From(data);
            var req = new HandshakeReq
            {
                Version = bp.ReadByte(),
                Capabilities = (CodecCapabilities)bp.ReadByte(),
                Flags = bp.ReadByte(),
                Username = bp.ReadString()
            };
            req.ClientNonce = bp.ReadRaw(16);
            return req;
        }
    }
}
