namespace EasyRDP.Core
{
    using System;
    using System.Runtime.InteropServices;
    using NLog;

    /// <summary>
    /// 剪贴板所有者标记 — 防回环机制。
    /// 通过自定义剪贴板格式 "EasyRDP.ClipboardOwner" 标记剪贴板数据由哪一端设置。
    /// 轮询检测到剪贴板变化时，如果 owner 标记 == 自己的端，说明是自己设的，跳过不回传。
    /// 比"路径签名比对"更可靠：用户修改文件后重新复制，路径签名不变但内容已变，
    /// 签名法会误判为"没变化"，而 owner flag 仍正确识别为"对方设的"。
    /// 使用 Win32 P/Invoke，兼容 net40 和 netstandard2.0。
    /// 必须在 STA 线程调用（与 IClipboardService 一致）。
    /// </summary>
    public static class ClipboardOwnerHelper
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        /// <summary>自定义剪贴板格式名。</summary>
        private const string OwnerFormatName = "EasyRDP.ClipboardOwner";

        /// <summary>端：服务端（被控端）。</summary>
        public const byte SideHost = 1;

        /// <summary>端：客户端（控制端）。</summary>
        public const byte SideClient = 2;

        /// <summary>端：无（本地用户设置，非远程同步）。</summary>
        public const byte SideNone = 0;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint RegisterClipboardFormat(string lpszFormatName);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetClipboardData(uint uFormat);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsClipboardFormatAvailable(uint uFormat);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalUnlock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalFree(IntPtr hMem);

        private const uint GHND = 0x0042;
        private const int MaxRetry = 10;
        private const int RetryDelayMs = 50;

        private static uint _formatId;
        private static bool _formatRegistered;

        /// <summary>
        /// 注册自定义剪贴板格式（首次调用时注册，后续直接返回缓存值）。
        /// RegisterClipboardFormat 在所有 Windows 版本上都可用，且格式名不区分大小写。
        /// </summary>
        private static uint GetFormatId()
        {
            if (_formatRegistered)
                return _formatId;

            _formatId = RegisterClipboardFormat(OwnerFormatName);
            _formatRegistered = true;
            if (_formatId == 0)
            {
                Logger.Warn("RegisterClipboardFormat failed for '{0}'", OwnerFormatName);
            }
            return _formatId;
        }

        /// <summary>
        /// 在剪贴板上设置 owner 标记。必须在 SetFiles/SetFileDropList 之后调用
        /// （SetClipboardData 不需要 EmptyClipboard，追加格式即可）。
        /// 必须在 STA 线程调用。
        /// </summary>
        /// <param name="side">设置端：SideHost 或 SideClient。</param>
        public static void SetOwnerFlag(byte side)
        {
            uint fmtId = GetFormatId();
            if (fmtId == 0) return;

            IntPtr hGlobal = GlobalAlloc(GHND, (UIntPtr)1);
            if (hGlobal == IntPtr.Zero)
            {
                Logger.Warn("SetOwnerFlag GlobalAlloc failed");
                return;
            }

            IntPtr pGlobal = GlobalLock(hGlobal);
            if (pGlobal == IntPtr.Zero)
            {
                GlobalFree(hGlobal);
                Logger.Warn("SetOwnerFlag GlobalLock failed");
                return;
            }

            try
            {
                Marshal.WriteByte(pGlobal, side);
            }
            finally
            {
                GlobalUnlock(hGlobal);
            }

            for (int i = 0; i < MaxRetry; i++)
            {
                if (OpenClipboard(IntPtr.Zero))
                {
                    try
                    {
                        IntPtr result = SetClipboardData(fmtId, hGlobal);
                        if (result != IntPtr.Zero)
                        {
                            // 成功后剪贴板拥有 hGlobal，不要 free
                            return;
                        }
                        int err = Marshal.GetLastWin32Error();
                        Logger.Warn("SetOwnerFlag SetClipboardData failed: Win32 error {0}", err);
                        GlobalFree(hGlobal);
                        return;
                    }
                    finally
                    {
                        CloseClipboard();
                    }
                }
                System.Threading.Thread.Sleep(RetryDelayMs);
            }

            Logger.Warn("SetOwnerFlag OpenClipboard failed after {0} retries", MaxRetry);
            GlobalFree(hGlobal);
        }

        /// <summary>
        /// 读取剪贴板上的 owner 标记。
        /// 必须在 STA 线程调用。
        /// </summary>
        /// <returns>SideHost/SideClient，或 SideNone（未设置或读取失败）。</returns>
        public static byte GetOwnerFlag()
        {
            uint fmtId = GetFormatId();
            if (fmtId == 0) return SideNone;

            if (!IsClipboardFormatAvailable(fmtId))
                return SideNone;

            for (int i = 0; i < MaxRetry; i++)
            {
                if (OpenClipboard(IntPtr.Zero))
                {
                    try
                    {
                        IntPtr hData = GetClipboardData(fmtId);
                        if (hData == IntPtr.Zero)
                            return SideNone;

                        IntPtr pData = GlobalLock(hData);
                        if (pData == IntPtr.Zero)
                            return SideNone;

                        try
                        {
                            return Marshal.ReadByte(pData);
                        }
                        finally
                        {
                            GlobalUnlock(hData);
                        }
                    }
                    finally
                    {
                        CloseClipboard();
                    }
                }
                System.Threading.Thread.Sleep(RetryDelayMs);
            }

            return SideNone;
        }
    }
}
