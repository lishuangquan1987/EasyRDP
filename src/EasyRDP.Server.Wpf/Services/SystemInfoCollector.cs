using System;
using System.Runtime.InteropServices;
using EasyRDP.Core.Protocol;
using Microsoft.Win32;

namespace EasyRDP.Server.Wpf.Services
{
    /// <summary>
    /// 服务端系统信息采集器。为连接详情面板收集 CPU/GPU/内存/OS/DPI 等信息，
    /// 组装为 DiagnosticInfoMessage 下发给客户端。
    /// 实现策略：注册表 + Win32 P/Invoke（net40/XP 可用），避免 WMI 依赖（慢且 XP 兼容性差）。
    /// 系统静态信息（CPU 名/GPU 名/OS/内存）首次采集后缓存，不重复查询。
    /// </summary>
    public class SystemInfoCollector
    {
        // 采集方式枚举（与客户端展示映射一致）
        public const byte CaptureMethodBitBlt = 0;
        public const byte CaptureMethodDxgi = 1;

        // 缓存字段：系统静态信息只查一次
        private string _cachedCpuName;
        private string _cachedGpuName;
        private string _cachedOsVersion;
        private long _cachedTotalMemoryMb;
        private bool _systemInfoInitialized;

        /// <summary>
        /// 组装一条完整的 DiagnosticInfoMessage。
        /// captureMethod 由调用方（TransportHost）根据实际采集器类型传入；
        /// 编码器可用性由调用方按 EncoderFactory 探测结果传入。
        /// </summary>
        public DiagnosticInfoMessage Collect(
            byte captureMethod,
            int screenWidth,
            int screenHeight,
            ushort scaleFactorX100,
            bool h264Available,
            bool zrleAvailable,
            bool vp8Available)
        {
            EnsureSystemInfo();
            var msg = new DiagnosticInfoMessage
            {
                CpuName = _cachedCpuName,
                CpuCores = Environment.ProcessorCount,
                GpuName = _cachedGpuName,
                TotalMemoryMb = _cachedTotalMemoryMb,
                OsVersion = _cachedOsVersion,
                CaptureMethod = captureMethod,
                ScaleFactorX100 = scaleFactorX100,
                ScreenWidth = screenWidth,
                ScreenHeight = screenHeight,
                H264Available = h264Available ? (byte)1 : (byte)0,
                ZrleAvailable = zrleAvailable ? (byte)1 : (byte)0,
                Vp8Available = vp8Available ? (byte)1 : (byte)0
            };
            return msg;
        }

        /// <summary>获取当前进程 DPI 缩放因子 ×100（100=100%，150=150%）。</summary>
        public static ushort GetScaleFactorX100()
        {
            try
            {
                IntPtr dc = GetDC(IntPtr.Zero);
                if (dc == IntPtr.Zero) return 100;
                try
                {
                    int dpi = GetDeviceCaps(dc, 88 /* LOGPIXELSX */);
                    return dpi > 0 ? (ushort)Math.Round(dpi * 100.0 / 96.0) : (ushort)100;
                }
                finally
                {
                    ReleaseDC(IntPtr.Zero, dc);
                }
            }
            catch
            {
                return 100;
            }
        }

        /// <summary>判断采集器类型对应的 CaptureMethod 枚举值。</summary>
        public static byte DetectCaptureMethod(object capturer)
        {
            if (capturer == null) return CaptureMethodBitBlt;
            string typeName = capturer.GetType().Name;
            if (typeName.IndexOf("Dxgi", StringComparison.OrdinalIgnoreCase) >= 0)
                return CaptureMethodDxgi;
            return CaptureMethodBitBlt;
        }

        private void EnsureSystemInfo()
        {
            if (_systemInfoInitialized) return;
            lock (this)
            {
                if (_systemInfoInitialized) return;
                try { _cachedCpuName = ReadRegistryString(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString"); }
                catch { _cachedCpuName = ""; }
                try { _cachedGpuName = ReadGpuName(); }
                catch { _cachedGpuName = ""; }
                try { _cachedOsVersion = BuildOsVersion(); }
                catch { _cachedOsVersion = Environment.OSVersion.VersionString; }
                try { _cachedTotalMemoryMb = GetTotalPhysicalMemoryMb(); }
                catch { _cachedTotalMemoryMb = 0; }
                _systemInfoInitialized = true;
            }
        }

        /// <summary>从注册表读取字符串值（HKLM 基键）。</summary>
        private static string ReadRegistryString(string subKey, string valueName)
        {
            object v = Registry.GetValue(@"HKEY_LOCAL_MACHINE\" + subKey, valueName, null);
            return v != null ? v.ToString() : "";
        }

        /// <summary>从显示类驱动注册表枚举主 GPU 名称（DriverDesc）。</summary>
        private static string ReadGpuName()
        {
            const string displayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(displayClass))
            {
                if (key == null) return "";
                for (int i = 0; i <= 9; i++)
                {
                    using (RegistryKey sub = key.OpenSubKey(i.ToString("D4")))
                    {
                        if (sub == null) continue;
                        string desc = sub.GetValue("DriverDesc") as string;
                        if (!string.IsNullOrEmpty(desc))
                            return desc;
                    }
                }
            }
            return "";
        }

        /// <summary>组装操作系统版本描述（如 "Windows 10 Pro (22H2)"）。</summary>
        private static string BuildOsVersion()
        {
            string productName = ReadRegistryString(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName");
            if (string.IsNullOrEmpty(productName))
                productName = Environment.OSVersion.VersionString;
            string displayVersion = ReadRegistryString(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion");
            if (string.IsNullOrEmpty(displayVersion))
            {
                string releaseId = ReadRegistryString(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ReleaseId");
                if (!string.IsNullOrEmpty(releaseId))
                    displayVersion = releaseId;
            }
            string arch = Environment.Is64BitOperatingSystem ? " x64" : " x86";
            string result = productName + arch;
            if (!string.IsNullOrEmpty(displayVersion))
                result += " (" + displayVersion + ")";
            return result;
        }

        /// <summary>物理内存总量（MB），GlobalMemoryStatusEx。</summary>
        private static long GetTotalPhysicalMemoryMb()
        {
            MEMORYSTATUSEX status = new MEMORYSTATUSEX();
            status.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            if (!GlobalMemoryStatusEx(ref status))
                return 0;
            return (long)(status.ullTotalPhys / (1024 * 1024));
        }

        #region P/Invoke

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

        #endregion
    }
}
