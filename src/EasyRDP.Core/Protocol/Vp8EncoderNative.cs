using System;
using System.Runtime.InteropServices;
using NLog;

namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// VP8 软件编码器（libvpx P/Invoke）。实现 IVideoEncoder，与 H264EncoderNative 可互换。
    /// 输入 BGRA32 → ColorConverter.BgraToI420 → vpx_img_wrap 包装 I420 → vpx_codec_encode。
    /// 运行时低延时配置：CBR + 无 lag + 实时 deadline（VPX_DL_REALTIME）。
    /// 需要 libs/vpx/vpx.dll（XP 兼容 x86 构建），缺失时 IsAvailable=false。
    /// </summary>
    public class Vp8EncoderNative : IVideoEncoder
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private IntPtr _iface;   // vpx_codec_vp8_cx() 接口指针（进程级单例）
        private IntPtr _ctx;     // vpx_codec_ctx_t 缓冲
        private IntPtr _cfg;     // vpx_codec_enc_cfg_t 缓冲
        private IntPtr _img;     // vpx_image_t 缓冲（输入帧包装）
        private IntPtr _i420;    // I420 像素缓冲（pinned）
        private GCHandle _i420Handle;
        private int _width;
        private int _height;
        private long _pts;
        private bool _initialized;
        private bool _disposed;

        /// <summary>编解码器标识。</summary>
        public CodecId Codec { get { return CodecId.Vp8Software; } }

        /// <summary>vpx.dll 是否可用（可加载且 vpx_codec_vp8_cx 非空）。</summary>
        public bool IsAvailable
        {
            get
            {
                if (_disposed) return false;
                if (_iface != IntPtr.Zero) return true;
                try
                {
                    _iface = VpxNative.vpx_codec_vp8_cx();
                }
                catch (DllNotFoundException)
                {
                    return false;
                }
                catch (EntryPointNotFoundException)
                {
                    return false;
                }
                return _iface != IntPtr.Zero;
            }
        }

        /// <summary>
        /// 初始化编码器。libvpx cfg 由 vpx_codec_enc_config_default 填充默认值后
        /// 按偏移覆写（与 OpenH264 模式一致），避免手建含嵌套数组的大结构体。
        /// </summary>
        public void Initialize(int width, int height, int targetBitrate)
        {
            if (_disposed) throw new ObjectDisposedException("Vp8EncoderNative");
            if (!IsAvailable)
                throw new InvalidOperationException("vpx.dll not available — VP8 encoder cannot be created");
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException("width/height must be positive");
            if ((width & 1) != 0 || (height & 1) != 0)
                throw new ArgumentOutOfRangeException("width/height must be even for VP8 I420");

            _width = width;
            _height = height;

            _ctx = Marshal.AllocHGlobal(VpxNative.CtxSize);
            _cfg = Marshal.AllocHGlobal(VpxNative.CfgSize);
            _img = Marshal.AllocHGlobal(VpxNative.ImgSize);
            ZeroMemory(_ctx, VpxNative.CtxSize);
            ZeroMemory(_cfg, VpxNative.CfgSize);
            ZeroMemory(_img, VpxNative.ImgSize);

            // 1) 让 DLL 填充默认 cfg
            int defRet = VpxNative.vpx_codec_enc_config_default(_iface, _cfg, 0);
            if (defRet != VpxNative.VPX_CODEC_OK)
            {
                Logger.Error("vpx_codec_enc_config_default failed: {0} {1}", defRet,
                    VpxNative.ErrorString(IntPtr.Zero, defRet));
                throw new InvalidOperationException("vpx_codec_enc_config_default failed: " + defRet);
            }

            // 2) 覆写关键字段（偏移见 VpxNative；rc_target_bitrate 单位为 kbps）
            Marshal.WriteInt32(_cfg, VpxNative.CfgGW, width);
            Marshal.WriteInt32(_cfg, VpxNative.CfgGH, height);
            Marshal.WriteInt32(_cfg, VpxNative.CfgGTimebaseNum, 1);
            Marshal.WriteInt32(_cfg, VpxNative.CfgGTimebaseDen, 30);
            Marshal.WriteInt32(_cfg, VpxNative.CfgGPass, VpxNative.VPX_RC_ONE_PASS);
            Marshal.WriteInt32(_cfg, VpxNative.CfgGLagInFrames, 0); // 实时：零缓冲
            Marshal.WriteInt32(_cfg, VpxNative.CfgRcEndUsage, VpxNative.VPX_CBR);
            int kbps = Math.Max(100, targetBitrate / 1000);
            Marshal.WriteInt32(_cfg, VpxNative.CfgRcTargetBitrate, kbps);
            // QP 范围：屏幕内容用较紧上限保文字清晰
            Marshal.WriteInt32(_cfg, VpxNative.CfgRcMinQuantizer, 10);
            Marshal.WriteInt32(_cfg, VpxNative.CfgRcMaxQuantizer, 51);
            // 码率控制缓冲（CBR 平滑），单位 kbps
            Marshal.WriteInt32(_cfg, VpxNative.CfgRcBufSz, Math.Max(kbps / 2, 1000));
            Marshal.WriteInt32(_cfg, VpxNative.CfgRcBufInitialSz, Math.Max(kbps / 4, 500));
            Marshal.WriteInt32(_cfg, VpxNative.CfgRcBufOptimalSz, Math.Max(kbps / 3, 600));
            // 关键帧：AUTO + 60 帧间隔（≈2s@30fps，参考帧漂移保护）
            Marshal.WriteInt32(_cfg, VpxNative.CfgKfMode, VpxNative.VPX_KF_AUTO);
            Marshal.WriteInt32(_cfg, VpxNative.CfgKfMaxDist, 60);
            // 线程：弱机（XP 双核）用 1-2，四核+ 用 4
            int procCount = Environment.ProcessorCount;
            Marshal.WriteInt32(_cfg, VpxNative.CfgGThreads, procCount >= 4 ? 4 : (procCount >= 2 ? 2 : 1));

            // 3) 初始化编码器（ABI 版本 39）
            int ret = VpxNative.vpx_codec_enc_init_ver(_ctx, _iface, _cfg, IntPtr.Zero,
                VpxNative.VPX_ENCODER_ABI_VERSION);
            if (ret != VpxNative.VPX_CODEC_OK)
            {
                Logger.Error("vpx_codec_enc_init failed: {0} {1} (res={2}x{3} bitrate={4}kbps)",
                    ret, VpxNative.ErrorString(_ctx, ret), width, height, kbps);
                throw new InvalidOperationException("vpx_codec_enc_init failed: " + ret);
            }

            // 4) 分配 I420 缓冲（Y + U + V 连续）
            int ySize = width * height;
            int uvSize = ySize / 4;
            byte[] i420 = new byte[ySize + uvSize * 2];
            _i420Handle = GCHandle.Alloc(i420, GCHandleType.Pinned);
            _i420 = _i420Handle.AddrOfPinnedObject();
            _pts = 0;
            _initialized = true;
            Logger.Info("VP8 encoder initialized: {0}x{1} bitrate={2}kbps CBR", width, height, kbps);
        }

        /// <summary>编码一帧 BGRA32 像素。返回 VP8 压缩数据（EncodedFrame）。失败返回空帧（Data=null）。</summary>
        public EncodedFrame Encode(byte[] pixels, bool forceKeyframe)
        {
            if (!_initialized || _disposed || pixels == null) return new EncodedFrame();

            // BGRA→I420（与 H264 共享 ColorConverter）
            int ySize = _width * _height;
            int uvSize = ySize / 4;
            var hBgra = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                ColorConverter.BgraToI420(hBgra.AddrOfPinnedObject(),
                    _i420, _i420 + ySize, _i420 + ySize + uvSize, _width, _height);
            }
            finally
            {
                hBgra.Free();
            }

            // 包装输入帧（vpx_img_wrap 设置 planes/stride；I420 连续布局与 i420 缓冲一致）
            IntPtr pImg = VpxNative.vpx_img_wrap(_img, VpxNative.VPX_IMG_FMT_I420,
                (uint)_width, (uint)_height, 32, _i420);
            if (pImg == IntPtr.Zero)
            {
                Logger.Warn("vpx_img_wrap failed");
                return new EncodedFrame();
            }

            long flags = forceKeyframe ? VpxNative.VPX_EFLAG_FORCE_KF : 0;
            int ret = VpxNative.vpx_codec_encode(_ctx, pImg, _pts++,
                new IntPtr(1), new IntPtr(flags), new IntPtr(VpxNative.VPX_DL_REALTIME));
            if (ret != VpxNative.VPX_CODEC_OK)
            {
                Logger.Warn("vpx_codec_encode failed: {0} {1} pts={2}",
                    ret, VpxNative.ErrorString(_ctx, ret), _pts - 1);
                return new EncodedFrame();
            }

            // 迭代取编码输出包
            IntPtr iter = IntPtr.Zero;
            byte[] encoded = null;
            bool isKey = false;
            IntPtr pkt;
            while ((pkt = VpxNative.vpx_codec_get_cx_data(_ctx, ref iter)) != IntPtr.Zero)
            {
                int kind = Marshal.ReadInt32(pkt, VpxNative.PktKind);
                if (kind != VpxNative.CX_FRAME_PKT) continue;
                IntPtr buf = Marshal.ReadIntPtr(pkt, VpxNative.PktFrameBuf);
                long sz = IntPtr.Size == 8
                    ? Marshal.ReadInt64(pkt, VpxNative.PktFrameSz)
                    : Marshal.ReadInt32(pkt, VpxNative.PktFrameSz);
                uint fflags = (uint)Marshal.ReadInt32(pkt, VpxNative.PktFrameFlags);
                if (buf != IntPtr.Zero && sz > 0 && sz < int.MaxValue)
                {
                    encoded = new byte[(int)sz];
                    Marshal.Copy(buf, encoded, 0, (int)sz);
                    isKey = (fflags & VpxNative.VPX_FRAME_IS_KEY) != 0;
                }
                break; // 实时单帧：只取第一个 frame 包
            }

            if (encoded == null || encoded.Length == 0)
                return new EncodedFrame();
            return new EncodedFrame { Data = encoded, IsKeyframe = isKey, Width = _width, Height = _height };
        }

        /// <summary>
        /// 运行时调整目标码率。libvpx VP8 无 SetOption 式接口，改码率需重新初始化
        /// （会丢参考帧并强制关键帧，开销大）——故空实现：D11 自适应在 VP8 路径
        /// 退化为降帧率/降分辨率（已有机制），码率在 Initialize 时按档位设定。
        /// </summary>
        public void SetTargetBitrate(int bitrateBps)
        {
            // 空实现：VP8 重建改码率成本高，D11 用降分辨率/降 fps 达成等效降负载
        }

        /// <summary>重置编码器（释放原生实例，重新 Initialize 后可用）。幂等，可安全重复调用。
        /// 注意：不依赖 _initialized 门闩——Initialize 中途失败（enc_init 返回错误）时
        /// 已分配的 ctx/cfg/img 缓冲也必须释放，防止泄漏。</summary>
        public void Reset()
        {
            try
            {
                if (_ctx != IntPtr.Zero)
                {
                    // 仅当编码器成功初始化过才调用 destroy（防止对未初始化 ctx 释放）
                    if (_initialized)
                    {
                        VpxNative.vpx_codec_destroy(_ctx);
                    }
                    Marshal.FreeHGlobal(_ctx);
                    _ctx = IntPtr.Zero;
                }
                if (_cfg != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_cfg);
                    _cfg = IntPtr.Zero;
                }
                if (_img != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_img);
                    _img = IntPtr.Zero;
                }
                if (_i420Handle.IsAllocated)
                {
                    _i420Handle.Free();
                    _i420 = IntPtr.Zero;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "VP8 Reset cleanup failed (non-fatal)");
            }
            _initialized = false;
        }

        /// <summary>释放全部资源。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Reset();
        }

        private static void ZeroMemory(IntPtr p, int size)
        {
            for (int off = 0; off < size; off += 8)
                Marshal.WriteInt64(p, off, 0);
        }
    }
}
