using System;
using System.Runtime.InteropServices;

namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// OpenH264 原生 API — x64 (64-bit) P/Invoke 实现。
    /// DLL 位于 appBase/openh264/x64/openh264.dll
    /// </summary>
    internal static class H264NativeArchX64
    {
        private const string DllPath = "openh264\\x64\\openh264.dll";

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int WelsCreateSVCEncoder(out IntPtr ppEncoder);

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void WelsDestroySVCEncoder(IntPtr pEncoder);

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int WelsCreateDecoder(out IntPtr ppDecoder);

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void WelsDestroyDecoder(IntPtr pDecoder);
    }
}
