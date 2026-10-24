namespace EasyRDP.Core.Security
{
    using System;
    using System.Security.Cryptography;
    using System.Text;

    /// <summary>
    /// 握手认证密码学工具：PBKDF2 派生 + HMAC-SHA256 挑战响应 + 常量时间比较。
    /// net40/netstandard2.0/net8.0 均可用（Rfc2898DeriveBytes 与 HMACSHA256 属 BCL 基础类型）。
    /// </summary>
    public static class AuthCrypto
    {
        /// <summary>PBKDF2 默认迭代次数。XP 单核约 30-60ms，连接一次的开销可接受。</summary>
        public const int DefaultIterations = 10000;
        /// <summary>派生密钥长度（字节）。</summary>
        public const int KeyLength = 32;

        /// <summary>生成密码学安全随机字节。</summary>
        public static byte[] RandomBytes(int count)
        {
            byte[] buffer = new byte[count];
            using (var rng = new RNGCryptoServiceProvider())
            {
                rng.GetBytes(buffer);
            }
            return buffer;
        }

        /// <summary>用 PBKDF2(SHA1) 从口令派生 32 字节密钥。</summary>
        public static byte[] DeriveKey(string password, byte[] salt, int iterations)
        {
            if (salt == null || salt.Length == 0)
                throw new ArgumentException("salt must not be empty", "salt");
            if (iterations < 1)
                iterations = DefaultIterations;
            using (var kdf = new Rfc2898DeriveBytes(password ?? "", salt, iterations))
            {
                return kdf.GetBytes(KeyLength);
            }
        }

        /// <summary>
        /// 计算挑战响应：HMAC-SHA256(key, serverNonce || clientNonce || UTF8(username))。
        /// 客户端与服务端必须用完全相同的拼接顺序与编码。
        /// </summary>
        public static byte[] ComputeProof(byte[] key, byte[] serverNonce, byte[] clientNonce, string username)
        {
            if (key == null || key.Length == 0)
                throw new ArgumentException("key must not be empty", "key");
            byte[] sn = serverNonce ?? new byte[0];
            byte[] cn = clientNonce ?? new byte[0];
            byte[] user = Encoding.UTF8.GetBytes(username ?? "");

            byte[] buffer = new byte[sn.Length + cn.Length + user.Length];
            Buffer.BlockCopy(sn, 0, buffer, 0, sn.Length);
            Buffer.BlockCopy(cn, 0, buffer, sn.Length, cn.Length);
            Buffer.BlockCopy(user, 0, buffer, sn.Length + cn.Length, user.Length);

            using (var hmac = new HMACSHA256(key))
            {
                return hmac.ComputeHash(buffer);
            }
        }

        /// <summary>常量时间字节比较（避免按字节提前返回泄漏前缀匹配长度）。</summary>
        public static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
                return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
                diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
