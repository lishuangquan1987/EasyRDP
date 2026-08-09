using System;
using System.Runtime.InteropServices;

namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// libvpx (VP8) 原生接口声明层：P/Invoke + 结构体偏移表。
    /// 布局基于 libvpx v1.16.0 官方头文件（vpx_encoder.h / vpx_image.h / vpx_codec.h / vpx_decoder.h）
    /// 手工计算（C 自然对齐）。偏移表按 IntPtr.Size 区分 x86/x64。
    /// 编码器 ABI 版本 VPX_ENCODER_ABI_VERSION=39，解码器 VPX_DECODER_ABI_VERSION=12。
    /// </summary>
    internal static class VpxNative
    {
        // ── 枚举/常量（来自 vpx_codec.h / vpx_encoder.h / vpx_image.h）──
        internal const int VPX_CODEC_OK = 0;
        internal const int VPX_MEM_ERROR = 2;
        internal const int VPX_ABI_MISMATCH = 3;

        /// <summary>vpx_img_fmt_t：VPX_IMG_FMT_I420 = 0x102。</summary>
        internal const uint VPX_IMG_FMT_I420 = 0x102;

        // vpx_rc_mode
        internal const int VPX_VBR = 0;
        internal const int VPX_CBR = 1;
        internal const int VPX_CQ = 2;
        internal const int VPX_Q = 3;

        // vpx_enc_pass
        internal const int VPX_RC_ONE_PASS = 0;

        // vpx_kf_mode
        internal const int VPX_KF_AUTO = 1;

        // vpx_enc_frame_flags_t（C long，小值）
        internal const long VPX_EFLAG_FORCE_KF = 1;
        // vpx_enc_deadline_t
        internal const long VPX_DL_REALTIME = 1;

        // ABI 版本
        internal const int VPX_ENCODER_ABI_VERSION = 39;
        internal const int VPX_DECODER_ABI_VERSION = 12;

        // vpx_codec_cx_pkt_kind
        internal const int CX_FRAME_PKT = 0;

        // vpx_codec_frame_flags_t
        internal const uint VPX_FRAME_IS_KEY = 0x1;

        /// <summary>vpx_codec_ctx_t 结构体大小（内部管理，只提供缓冲）：x64=56，x86=28。</summary>
        internal static int CtxSize { get { return IntPtr.Size == 8 ? 56 : 28; } }

        // ── vpx_codec_enc_cfg_t 关键字段偏移（v1.16.0：无 g_ss 子结构，全部字段对齐≤4，
        //    x64 与 x86 布局完全相同，sizeof=468。默认值由 vpx_codec_enc_config_default
        //    全量覆盖填充后按偏移覆写——该函数不要求 cfg 零初始化，见 vpx_encoder.c）──
        internal const int CfgGThreads = 4;
        internal const int CfgGW = 12;
        internal const int CfgGH = 16;
        internal const int CfgGTimebaseNum = 28;
        internal const int CfgGTimebaseDen = 32;
        internal const int CfgGPass = 40;
        internal const int CfgGLagInFrames = 44;
        internal const int CfgRcEndUsage = 72;          // enum vpx_rc_mode
        internal const int CfgRcTargetBitrate = 76;     // kbps
        internal const int CfgRcMinQuantizer = 80;
        internal const int CfgRcMaxQuantizer = 84;
        internal const int CfgRcBufSz = 96;
        internal const int CfgRcBufInitialSz = 100;
        internal const int CfgRcBufOptimalSz = 104;
        internal const int CfgKfMode = 124;             // enum vpx_kf_mode
        internal const int CfgKfMaxDist = 132;
        /// <summary>vpx_codec_enc_cfg_t 总大小（v1.16.0）：x64=x86=468。</summary>
        internal const int CfgSize = 468;

        // ── vpx_image_t 关键字段偏移 ──
        internal const int ImgFmt = 0;
        internal const int ImgW = 12;
        internal const int ImgH = 16;
        internal const int ImgPlanes = 48;   // unsigned char*[4]（x64/x86 相同，前 12 个 4B 字段=48）
        internal static int ImgStride { get { return IntPtr.Size == 8 ? 80 : 64; } } // int[4]
        /// <summary>vpx_image_t 总大小：x64=136，x86=104。</summary>
        internal static int ImgSize { get { return IntPtr.Size == 8 ? 136 : 104; } }

        // ── vpx_codec_cx_pkt_t（kind + union.data.frame）──
        // 布局依 MinGW 编译（本仓库构建方式）：
        //   x64（LP64，long=8）：kind@0, data@8, buf@8, sz@16, pts@24, duration@32, flags@40
        //   x86（GCC i386，int64 对齐 4）：kind@0, data@4, buf@4, sz@8, pts@12, duration@16, flags@24
        // 注意：若 libvpx 由 MSVC 编译（LLP64，long=4），x64 flags 为 36 而非 40 ——
        // 本仓库构建脚本使用 MSYS2 MinGW，按上表偏移。
        internal const int PktKind = 0;
        internal static int PktData { get { return IntPtr.Size == 8 ? 8 : 4; } }
        internal static int PktFrameBuf { get { return PktData; } }                          // void* buf
        internal static int PktFrameSz { get { return PktData + (IntPtr.Size == 8 ? 8 : 4); } } // size_t sz
        internal static int PktFrameFlags { get { return IntPtr.Size == 8 ? 40 : 24; } }     // uint32 flags

        // ── vpx_codec_dec_cfg_t：{threads@0, w@4, h@8}，sizeof=12 ──
        internal const int DecCfgSize = 12;

        // ── P/Invoke ──
        // DLL 名：运行时按架构加载 libs/vpx/ 下的 vpx-x86/vpx-x64（由 H264NativeArch 类似机制提供）。
        // 直接声明 vpx.dll；加载失败时 DllNotFoundException 由调用方捕获判定 IsAvailable=false。
        private const string VpxDll = "vpx.dll";

        [DllImport(VpxDll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr vpx_codec_vp8_cx();

        [DllImport(VpxDll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int vpx_codec_enc_config_default(IntPtr iface, IntPtr cfg, uint usage);

        [DllImport(VpxDll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int vpx_codec_enc_init_ver(IntPtr ctx, IntPtr iface, IntPtr cfg, IntPtr flags, int ver);

        [DllImport(VpxDll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int vpx_codec_encode(IntPtr ctx, IntPtr img, long pts, IntPtr duration, IntPtr flags, IntPtr deadline);

        [DllImport(VpxDll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr vpx_codec_get_cx_data(IntPtr ctx, ref IntPtr iter);

        [DllImport(VpxDll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int vpx_codec_destroy(IntPtr ctx);

        [DllImport(VpxDll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr vpx_img_wrap(IntPtr img, uint fmt, uint d_w, uint d_h, uint stride_align, IntPtr img_data);

        [DllImport(VpxDll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr vpx_codec_vp8_dx();

        [DllImport(VpxDll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int vpx_codec_dec_init_ver(IntPtr ctx, IntPtr iface, IntPtr cfg, IntPtr flags, int ver);

        [DllImport(VpxDll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int vpx_codec_decode(IntPtr ctx, IntPtr data, uint dataSz, IntPtr userPriv, IntPtr deadline);

        [DllImport(VpxDll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr vpx_codec_get_frame(IntPtr ctx, ref IntPtr iter);

        [DllImport(VpxDll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr vpx_codec_error(IntPtr ctx);

        [DllImport(VpxDll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr vpx_codec_err_to_string(int err);

        /// <summary>获取 libvpx 错误描述（诊断用）。失败返回空串。</summary>
        internal static string ErrorString(IntPtr ctx, int ret)
        {
            try
            {
                if (ret != VPX_CODEC_OK)
                {
                    IntPtr p = vpx_codec_err_to_string(ret);
                    if (p != IntPtr.Zero)
                        return Marshal.PtrToStringAnsi(p) ?? "";
                }
                if (ctx != IntPtr.Zero)
                {
                    IntPtr p = vpx_codec_error(ctx);
                    if (p != IntPtr.Zero)
                        return Marshal.PtrToStringAnsi(p) ?? "";
                }
            }
            catch
            {
                // 错误路径自身异常不影响主流程
            }
            return "";
        }
    }
}
