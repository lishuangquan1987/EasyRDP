namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 认证挑战（服务端→客户端，v5 密码模式）。
    /// Payload 布局: ServerNonce(16) Salt(16) Iterations(4 LE) = 36 字节。
    /// 客户端用 PBKDF2(password, Salt, Iterations) 派生密钥，再对
    /// ServerNonce || ClientNonce || Username 做 HMAC-SHA256 作为证明。
    /// </summary>
    public class AuthChallengeMessage
    {
        /// <summary>服务端随机数（16 字节）。</summary>
        public byte[] ServerNonce;
        /// <summary>PBKDF2 盐（16 字节）。</summary>
        public byte[] Salt;
        /// <summary>PBKDF2 迭代次数。</summary>
        public int Iterations;

        public AuthChallengeMessage()
        {
            ServerNonce = new byte[16];
            Salt = new byte[16];
            Iterations = Security.AuthCrypto.DefaultIterations;
        }

        /// <summary>序列化为 payload 字节。</summary>
        public byte[] Pack()
        {
            var bp = new BinaryPacker();
            bp.WriteRaw(ServerNonce);
            bp.WriteRaw(Salt);
            bp.WriteInt32(Iterations);
            return bp.GetBytes();
        }

        /// <summary>从 payload 字节反序列化。</summary>
        public static AuthChallengeMessage Unpack(byte[] data)
        {
            var bp = BinaryPacker.From(data);
            return new AuthChallengeMessage
            {
                ServerNonce = bp.ReadRaw(16),
                Salt = bp.ReadRaw(16),
                Iterations = bp.ReadInt32()
            };
        }
    }
}
