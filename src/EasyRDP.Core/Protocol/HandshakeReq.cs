namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 客户端握手请求。
    /// Payload 布局: Version(1) Capabilities(1) UsernameLen(2) Username(*) PasswordLen(2) Password(*)
    /// </summary>
    public class HandshakeReq
    {
        /// <summary>Protocol version of the client.</summary>
        public byte Version;
        /// <summary>Bitmask of supported codec capabilities.</summary>
        public CodecCapabilities Capabilities;
        /// <summary>Username for authentication.</summary>
        public string Username;
        /// <summary>Password for authentication.</summary>
        public string Password;

        public HandshakeReq()
        {
            Version = Constants.ProtocolVersion;
            Username = "";
            Password = "";
        }

        /// <summary>序列化为 payload 字节。</summary>
        public byte[] Pack()
        {
            var bp = new BinaryPacker();
            bp.WriteByte(Version);
            bp.WriteByte((byte)Capabilities);
            bp.WriteString(Username);
            bp.WriteString(Password);
            return bp.GetBytes();
        }

        /// <summary>从 payload 字节反序列化。</summary>
        public static HandshakeReq Unpack(byte[] data)
        {
            var bp = BinaryPacker.From(data);
            return new HandshakeReq
            {
                Version = bp.ReadByte(),
                Capabilities = (CodecCapabilities)bp.ReadByte(),
                Username = bp.ReadString(),
                Password = bp.ReadString()
            };
        }
    }
}
