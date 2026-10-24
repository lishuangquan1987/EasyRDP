namespace EasyRDP.Core.Clipboard
{
    using System;
    using System.Runtime.InteropServices;
    using System.Text;

    /// <summary>
    /// 构造 CF_HDROP 剪贴板数据（DROPFILES 头 + 文件路径列表）。
    /// 延迟渲染时先用 SetClipboardData(CF_HDROP, IntPtr.Zero) 放占位，
    /// 待系统发 WM_RENDERFORMAT 再用本类构造真实数据。
    /// </summary>
    public static class DropFilesBuilder
    {
        /// <summary>CF_HDROP 剪贴板格式 ID。</summary>
        public const int CF_HDROP = 15;

        /// <summary>DROPFILES 头长度（字节）：pFiles(4) + pt(8) + fNC(4) + fWide(4)。</summary>
        private const int HeaderSize = 20;

        /// <summary>
        /// 构造 CF_HDROP 数据块并返回 HGLOBAL（调用方负责 SetClipboardData 后不再释放）。
        /// 失败返回 IntPtr.Zero。
        /// </summary>
        /// <param name="paths">文件路径数组。</param>
        public static IntPtr Build(string[] paths)
        {
            if (paths == null || paths.Length == 0)
                return IntPtr.Zero;

            // 路径之间以 '\0' 分隔，末尾再加一个 '\0'（双 null 结尾）
            var sb = new StringBuilder();
            foreach (string path in paths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                sb.Append(path);
                sb.Append('\0');
            }
            sb.Append('\0');

            byte[] payload = Encoding.Unicode.GetBytes(sb.ToString());
            int total = HeaderSize + payload.Length;
            IntPtr handle = Marshal.AllocHGlobal(total);
            try
            {
                // DROPFILES 头：pFiles=文件列表偏移，pt 不关心，fNC=0，fWide=1
                Marshal.WriteInt32(handle, 0, HeaderSize);
                Marshal.WriteInt32(handle, 4, 0);   // pt.x
                Marshal.WriteInt32(handle, 8, 0);   // pt.y
                Marshal.WriteInt32(handle, 12, 0);  // fNC
                Marshal.WriteInt32(handle, 16, 1);  // fWide=TRUE
                Marshal.Copy(payload, 0, new IntPtr(handle.ToInt64() + HeaderSize), payload.Length);
                return handle;
            }
            catch
            {
                Marshal.FreeHGlobal(handle);
                return IntPtr.Zero;
            }
        }
    }
}
