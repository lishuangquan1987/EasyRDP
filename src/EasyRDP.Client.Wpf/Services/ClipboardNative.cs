using System;
using System.Runtime.InteropServices;
using EasyRDP.Core.Clipboard;

namespace EasyRDP.Client.Wpf.Services
{
    /// <summary>
    /// 剪贴板延迟渲染所需的原生调用（客户端）。
    /// 服务端复制文件后，本端只在剪贴板放一个空的 CF_HDROP 占位；
    /// 系统真正粘贴（WM_RENDERFORMAT）时才下载文件并填充真实数据。
    /// </summary>
    internal static class ClipboardNative
    {
        private const uint CF_HDROP = 15;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll")]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [DllImport("user32.dll")]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll")]
        private static extern IntPtr GetClipboardOwner();

        [DllImport("kernel32.dll")]
        private static extern void SetLastError(uint dwErrCode);

        /// <summary>当前剪贴板所有者窗口句柄（用于判断延迟渲染占位是否仍属于本端）。</summary>
        public static IntPtr GetOwner()
        {
            try { return GetClipboardOwner(); }
            catch { return IntPtr.Zero; }
        }

        /// <summary>放置 CF_HDROP 延迟渲染占位（空数据，等系统索要时再渲染）。</summary>
        public static bool SetDelayedPlaceholder(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            // 剪贴板常被其它进程短暂占用：重试几次
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (OpenClipboard(hwnd))
                {
                    try
                    {
                        EmptyClipboard();
                        // 延迟渲染：hMem=NULL 时成功返回值同样是 NULL，
                        // 必须用 LastError 判定成败（否则永远被当成失败）。
                        SetLastError(0);
                        SetClipboardData(CF_HDROP, IntPtr.Zero);
                        if (Marshal.GetLastWin32Error() == 0)
                            return true;
                    }
                    finally
                    {
                        CloseClipboard();
                    }
                }
                System.Threading.Thread.Sleep(60);
            }
            return false;
        }

        /// <summary>
        /// 在 WM_RENDERFORMAT 处理中填充真实数据：此时系统已开启剪贴板会话，
        /// 不能再 OpenClipboard/CloseClipboard。
        /// </summary>
        public static bool SetFilesInRenderSession(IntPtr hDrop)
        {
            if (hDrop == IntPtr.Zero) return false;
            return SetClipboardData(CF_HDROP, hDrop) != IntPtr.Zero;
        }

        /// <summary>在非渲染会话中直接设置文件剪贴板（Ctrl+V 预取完成时使用）。</summary>
        public static bool SetFiles(IntPtr hwnd, string[] paths)
        {
            if (hwnd == IntPtr.Zero) return false;
            IntPtr hDrop = DropFilesBuilder.Build(paths);
            if (hDrop == IntPtr.Zero) return false;
            if (!OpenClipboard(hwnd))
            {
                Marshal.FreeHGlobal(hDrop);
                return false;
            }
            try
            {
                EmptyClipboard();
                if (SetClipboardData(CF_HDROP, hDrop) == IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(hDrop);
                    return false;
                }
                return true;
            }
            finally
            {
                CloseClipboard();
            }
        }

        /// <summary>若剪贴板仍由本窗口持有，清空它（断开连接时移除失效的延迟渲染占位）。</summary>
        public static void ClearIfOwner(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            if (GetClipboardOwner() != hwnd) return;
            if (!OpenClipboard(hwnd)) return;
            try { EmptyClipboard(); }
            finally { CloseClipboard(); }
        }
    }
}
