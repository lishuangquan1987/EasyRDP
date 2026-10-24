namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 认证证明（客户端→服务端，v5 第二段）。
    /// Payload 布局: Version(1) Proof(32) = 33 字节。
    /// Proof = HMAC-SHA256(PBKDF2(password, salt, iterations), serverNonce || clientNonce || username)。
    /// 密码本身从不出现在网络上，服务端按同样步骤计算并做常量时间比较。
    /// </summary>
    public class HandshakeProofMessage
    {
        /// <summary>重发协议版本，便于服务端在校验前再次拒绝不匹配的客户端。</summary>
        public byte Version;
        /// <summary>HMAC-SHA256 结果（32 字节）。</summary>
        public byte[] Proof;

        public HandshakeProofMessage()
        {
            Version = Constants.ProtocolVersion;
            Proof = new byte[32];
        }

        /// <summary>序列化为 payload 字节。</summary>
        public byte[] Pack()
        {
            var bp = new BinaryPacker();
            bp.WriteByte(Version);
            bp.WriteRaw(Proof);
            return bp.GetBytes();
        }

        /// <summary>从 payload 字节反序列化。</summary>
        public static HandshakeProofMessage Unpack(byte[] data)
        {
            var bp = BinaryPacker.From(data);
            return new HandshakeProofMessage
            {
                Version = bp.ReadByte(),
                Proof = bp.ReadRaw(32)
            };
        }
    }
}
