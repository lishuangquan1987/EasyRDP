#nullable disable
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace EasyRDP.Shared
{
    /// <summary>
    /// 使用 Windows DPAPI（CryptProtectData）对本地保存的密码加密。
    /// 仅当前 Windows 用户可解密；无第三方依赖，net40/net8 均可用。
    /// </summary>
    public static class SecretProtector
    {
        private const uint CRYPTPROTECT_UI_FORBIDDEN = 0x1;
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("EasyRDP.local.v1");
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        [StructLayout(LayoutKind.Sequential)]
        private struct DATA_BLOB
        {
            public int cbData;
            public IntPtr pbData;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(
            ref DATA_BLOB pDataIn, string szDataDescr, ref DATA_BLOB pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DATA_BLOB pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(
            ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, ref DATA_BLOB pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DATA_BLOB pDataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        /// <summary>加密明文为 Base64 密文；失败返回 null。</summary>
        public static string Protect(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return "";
            try
            {
                byte[] plain = Encoding.UTF8.GetBytes(plainText);
                var input = new DATA_BLOB { cbData = plain.Length, pbData = Marshal.AllocHGlobal(plain.Length) };
                var entropy = new DATA_BLOB { cbData = Entropy.Length, pbData = Marshal.AllocHGlobal(Entropy.Length) };
                try
                {
                    Marshal.Copy(plain, 0, input.pbData, plain.Length);
                    Marshal.Copy(Entropy, 0, entropy.pbData, Entropy.Length);

                    DATA_BLOB output;
                    if (!CryptProtectData(ref input, "EasyRDP", ref entropy, IntPtr.Zero, IntPtr.Zero,
                        CRYPTPROTECT_UI_FORBIDDEN, out output))
                    {
                        return null;
                    }
                    try
                    {
                        byte[] encrypted = new byte[output.cbData];
                        Marshal.Copy(output.pbData, encrypted, 0, output.cbData);
                        return Convert.ToBase64String(encrypted);
                    }
                    finally
                    {
                        if (output.pbData != IntPtr.Zero)
                            LocalFree(output.pbData);
                    }
                }
                finally
                {
                    if (input.pbData != IntPtr.Zero)
                        Marshal.FreeHGlobal(input.pbData);
                    if (entropy.pbData != IntPtr.Zero)
                        Marshal.FreeHGlobal(entropy.pbData);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "SecretProtector.Protect failed");
                return null;
            }
        }

        /// <summary>解密 Base64 密文；失败返回 null（旧版明文或非本用户数据）。</summary>
        public static string Unprotect(string base64)
        {
            if (string.IsNullOrEmpty(base64)) return "";
            try
            {
                byte[] encrypted = Convert.FromBase64String(base64);
                var input = new DATA_BLOB { cbData = encrypted.Length, pbData = Marshal.AllocHGlobal(encrypted.Length) };
                var entropy = new DATA_BLOB { cbData = Entropy.Length, pbData = Marshal.AllocHGlobal(Entropy.Length) };
                try
                {
                    Marshal.Copy(encrypted, 0, input.pbData, encrypted.Length);
                    Marshal.Copy(Entropy, 0, entropy.pbData, Entropy.Length);

                    DATA_BLOB output;
                    if (!CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero,
                        CRYPTPROTECT_UI_FORBIDDEN, out output))
                    {
                        return null;
                    }
                    try
                    {
                        byte[] decrypted = new byte[output.cbData];
                        Marshal.Copy(output.pbData, decrypted, 0, output.cbData);
                        return Encoding.UTF8.GetString(decrypted);
                    }
                    finally
                    {
                        if (output.pbData != IntPtr.Zero)
                            LocalFree(output.pbData);
                    }
                }
                finally
                {
                    if (input.pbData != IntPtr.Zero)
                        Marshal.FreeHGlobal(input.pbData);
                    if (entropy.pbData != IntPtr.Zero)
                        Marshal.FreeHGlobal(entropy.pbData);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "SecretProtector.Unprotect failed");
                return null;
            }
        }
    }
}
