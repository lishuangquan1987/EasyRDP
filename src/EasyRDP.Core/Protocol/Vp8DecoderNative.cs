using System;
using System.Runtime.InteropServices;
using NLog;

namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// VP8 软件解码器（libvpx P/Invoke）。镜像 Vp8EncoderNative。
    /// 解码输出 I420（vpx_image_t 三平面）→ ColorConverter.I420ToBgra 转 BGRA32。
    /// 需要 libs/vpx/vpx.dll，缺失时 IsAvailable=false。
    /// </summary>
    public class Vp8DecoderNative : IVideoDecoder
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private IntPtr _iface;   // vpx_codec_vp8_dx()
        private IntPtr _ctx;     // vpx_codec_ctx_t 缓冲
        private IntPtr _cfg;     // vpx_codec_dec_cfg_t 缓冲
        private int _width;
        private int _height;
        private bool _initialized;
        private bool _disposed;
        private bool _firstFrameLogged;

        /// <summary>编解码器标识。</summary>
        public CodecId Codec { get { return CodecId.Vp8Software; } }

        /// <summary>vpx.dll 是否可用。</summary>
        public bool IsAvailable
        {
            get
            {
                if (_disposed) return false;
                if (_iface != IntPtr.Zero) return true;
                try
                {
                    _iface = VpxNative.vpx_codec_vp8_dx();
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

        /// <summary>初始化解码器。</summary>
        public void Initialize(int width, int height)
        {
            if (_disposed) throw new ObjectDisposedException("Vp8DecoderNative");
            if (!IsAvailable)
                throw new InvalidOperationException("vpx.dll not available — VP8 decoder cannot be created");

            _width = width;
            _height = height;

            _ctx = Marshal.AllocHGlobal(VpxNative.CtxSize);
            _cfg = Marshal.AllocHGlobal(VpxNative.DecCfgSize);
            for (int off = 0; off < VpxNative.CtxSize; off += 8)
                Marshal.WriteInt64(_ctx, off, 0);
            for (int off = 0; off < VpxNative.DecCfgSize; off += 8)
                Marshal.WriteInt64(_cfg, off, 0);

            // dec_cfg：threads@0, w@4, h@8（单线程解码；弱机避免多线程开销）
            Marshal.WriteInt32(_cfg, 0, 1);
            Marshal.WriteInt32(_cfg, 4, width);
            Marshal.WriteInt32(_cfg, 8, height);

            int ret = VpxNative.vpx_codec_dec_init_ver(_ctx, _iface, _cfg, IntPtr.Zero,
                VpxNative.VPX_DECODER_ABI_VERSION);
            if (ret != VpxNative.VPX_CODEC_OK)
            {
                Logger.Error("vpx_codec_dec_init failed: {0} {1}", ret,
                    VpxNative.ErrorString(_ctx, ret));
                throw new InvalidOperationException("vpx_codec_dec_init failed: " + ret);
            }
            _initialized = true;
            _firstFrameLogged = false;
            Logger.Info("VP8 decoder initialized: {0}x{1}", width, height);
        }

        /// <summary>解码一帧 VP8 数据，返回 BGRA32 像素。</summary>
        public DecodeResult Decode(byte[] data)
        {
            long expectedSize = (long)_width * _height * 4;
            if (expectedSize <= 0 || expectedSize > int.MaxValue)
                return new DecodeResult { Status = DecodeStatus.Failed };
            byte[] outputBuffer = new byte[expectedSize];
            return Decode(data, outputBuffer);
        }

        /// <summary>解码一帧 VP8 数据到指定的 BGRA32 输出缓冲区。</summary>
        public DecodeResult Decode(byte[] data, byte[] outputBuffer)
        {
            if (!_initialized || _disposed)
                return new DecodeResult { Status = DecodeStatus.Failed };
            if (data == null || data.Length == 0)
                return new DecodeResult { Status = DecodeStatus.NeedMoreInput };

            var hData = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                int ret = VpxNative.vpx_codec_decode(_ctx, hData.AddrOfPinnedObject(),
                    (uint)data.Length, IntPtr.Zero, IntPtr.Zero);
                if (ret != VpxNative.VPX_CODEC_OK)
                {
                    Logger.Warn("vpx_codec_decode failed: {0} {1} len={2}",
                        ret, VpxNative.ErrorString(_ctx, ret), data.Length);
                    return new DecodeResult { Status = DecodeStatus.Failed };
                }

                // 取输出帧（可能缓冲中无帧 = NeedMoreInput）
                IntPtr iter = IntPtr.Zero;
                IntPtr pFrame = VpxNative.vpx_codec_get_frame(_ctx, ref iter);
                if (pFrame == IntPtr.Zero)
                    return new DecodeResult { Status = DecodeStatus.NeedMoreInput };

                // 读 vpx_image_t：fmt/w/h/planes/stride
                int w = Marshal.ReadInt32(pFrame, VpxNative.ImgW);
                int h = Marshal.ReadInt32(pFrame, VpxNative.ImgH);
                if (w <= 0 || h <= 0)
                    return new DecodeResult { Status = DecodeStatus.Failed };

                long expectedBgraSize = (long)w * h * 4;
                if (expectedBgraSize > outputBuffer.Length)
                {
                    Logger.Warn("Output buffer too small: got={0} expected={1}", outputBuffer.Length, expectedBgraSize);
                    return new DecodeResult { Status = DecodeStatus.Failed };
                }

                IntPtr yPlane = Marshal.ReadIntPtr(pFrame, VpxNative.ImgPlanes);
                IntPtr uPlane = Marshal.ReadIntPtr(pFrame, VpxNative.ImgPlanes + IntPtr.Size);
                IntPtr vPlane = Marshal.ReadIntPtr(pFrame, VpxNative.ImgPlanes + IntPtr.Size * 2);
                int yStride = Marshal.ReadInt32(pFrame, VpxNative.ImgStride);
                int uvStride = Marshal.ReadInt32(pFrame, VpxNative.ImgStride + 4);
                if (yPlane == IntPtr.Zero || uPlane == IntPtr.Zero || vPlane == IntPtr.Zero
                    || yStride <= 0 || uvStride <= 0)
                    return new DecodeResult { Status = DecodeStatus.Failed };

                var hOut = GCHandle.Alloc(outputBuffer, GCHandleType.Pinned);
                try
                {
                    ColorConverter.I420ToBgra(yPlane, uPlane, vPlane, yStride, uvStride,
                        w, h, hOut.AddrOfPinnedObject(), w * 4);
                }
                finally
                {
                    hOut.Free();
                }

                if (!_firstFrameLogged)
                {
                    _firstFrameLogged = true;
                    Logger.Info("VP8 first frame decoded: {0}x{1} outLen={2}", w, h, outputBuffer.Length);
                }

                return new DecodeResult { Status = DecodeStatus.Ok, Pixels = outputBuffer };
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "VP8 Decode threw exception");
                return new DecodeResult { Status = DecodeStatus.Failed };
            }
            finally
            {
                hData.Free();
            }
        }

        /// <summary>重置解码器（释放并重新创建）。幂等：Initialize 中途失败时已分配的缓冲也释放。</summary>
        public void Reset()
        {
            if (_ctx != IntPtr.Zero)
            {
                if (_initialized)
                {
                    try { VpxNative.vpx_codec_destroy(_ctx); }
                    catch (Exception ex) { Logger.Warn(ex, "VP8 decoder destroy failed (non-fatal)"); }
                }
                Marshal.FreeHGlobal(_ctx);
                _ctx = IntPtr.Zero;
            }
            if (_cfg != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_cfg);
                _cfg = IntPtr.Zero;
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
    }
}
