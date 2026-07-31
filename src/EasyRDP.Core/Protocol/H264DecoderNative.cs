using System;
using System.Runtime.InteropServices;
using NLog;

namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// OpenH264 原生解码器。
    /// 通过 P/Invoke + vtable 调用 OpenH264 DLL。
    /// 解码输出为 I420 (YUV 4:2:0)，需做 I420→BGRA 颜色空间转换后才能交给渲染层。
    /// </summary>
    public class H264DecoderNative : IVideoDecoder
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private IntPtr _decoder;
        private H264Native.DecodeFrameDelegate _decodeFrame;
        private int _width;
        private int _height;
        private bool _initialized;
        private bool _disposed;
        private bool _firstFrameLogged;

        /// <summary>当前使用的编解码器标识。</summary>
        public CodecId Codec { get { return CodecId.H264Software; } }

        /// <summary>解码器是否可用（已创建且未释放）。</summary>
        public bool IsAvailable
        {
            get
            {
                if (_disposed) return false;
                if (_decoder != IntPtr.Zero) return true;
                return TryCreateDecoder();
            }
        }

        /// <summary>尝试创建 OpenH264 解码器实例。</summary>
        private bool TryCreateDecoder()
        {
            try
            {
                int ret = H264Native.WelsCreateDecoder(out _decoder);
                if (ret != 0 || _decoder == IntPtr.Zero)
                {
                    Logger.Error("WelsCreateDecoder failed with return code {0}", ret);
                    _decoder = IntPtr.Zero;
                    return false;
                }
                Logger.Info("OpenH264 decoder created successfully");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "WelsCreateDecoder threw exception");
                _decoder = IntPtr.Zero;
                return false;
            }
        }

        /// <summary>初始化解码器，设置目标分辨率。</summary>
        public void Initialize(int width, int height)
        {
            if (_disposed) throw new ObjectDisposedException("H264DecoderNative");
            if (_decoder == IntPtr.Zero)
            {
                if (!TryCreateDecoder())
                    throw new InvalidOperationException("Failed to create OpenH264 decoder");
            }

            _width = width;
            _height = height;

            var init = H264Native.GetVTableDelegate<H264Native.InitializeDecoderDelegate>(
                _decoder, H264Native.VTABLE_SLOT_DEC_INITIALIZE);
            // SDecodingParam 的 C# struct 定义可能与 C 布局不匹配（字段顺序/类型不确定）。
            // 用 AllocHGlobal + 清零的方式初始化，确保传入全 0 的原生内存（与 FFmpeg 做法一致）。
            // OpenH264 容忍全 0 的 SDecodingParam（iOutputColorFormat=0=videoFormatIV1，
            // eEcActiveIdc=0=ERROR_CON_SLICE_COPY，sVideoProperty.eVideoBsType=0=VIDEO_BITSTREAM_DEFAULT）。
            const int paramSize = 64; // 足够容纳 SDecodingParam（实际约 24-40 字节）
            IntPtr pParam = Marshal.AllocHGlobal(paramSize);
            try
            {
                for (int off = 0; off < paramSize; off += 8)
                    Marshal.WriteInt64(pParam, off, 0);
                int ret = init(_decoder, pParam);
                if (ret != 0)
                {
                    Logger.Error("OpenH264 decoder Initialize failed: return code {0}, resolution={1}x{2}",
                        ret, width, height);
                    throw new InvalidOperationException("OpenH264 decoder Initialize failed: " + ret);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pParam);
            }

            Logger.Info("OpenH264 decoder initialized: {0}x{1}", width, height);

            _decodeFrame = H264Native.GetVTableDelegate<H264Native.DecodeFrameDelegate>(
                _decoder, H264Native.VTABLE_SLOT_DEC_DECODE_FRAME_NO_DELAY);
            _initialized = true;
        }

        /// <summary>解码一帧 H264 数据，返回 BGRA32 像素。</summary>
        public DecodeResult Decode(byte[] data)
        {
            int expectedSize = _width * _height * 4;
            if (expectedSize <= 0)
                return new DecodeResult { Status = DecodeStatus.Failed };

            byte[] outputBuffer = new byte[expectedSize];
            return Decode(data, outputBuffer);
        }

        /// <summary>解码一帧 H264 数据到指定的 BGRA32 输出缓冲区。
        /// OpenH264 DecodeFrameNoDelay 输出 I420 (YUV 4:2:0)，本方法负责 I420→BGRA 转换。</summary>
        public DecodeResult Decode(byte[] data, byte[] outputBuffer)
        {
            if (!_initialized || _disposed || _decoder == IntPtr.Zero)
                return new DecodeResult { Status = DecodeStatus.Failed };
            if (data == null || data.Length == 0)
                return new DecodeResult { Status = DecodeStatus.NeedMoreInput };

            var dataHandle = GCHandle.Alloc(data, GCHandleType.Pinned);
            // ppDst 是 unsigned char** — OpenH264 会写入 3 个 YUV 平面指针。
            // 分配 IntPtr[3] 数组并固定，传其地址给 OpenH264。
            IntPtr[] ppDst = new IntPtr[3];
            var ppDstHandle = GCHandle.Alloc(ppDst, GCHandleType.Pinned);
            try
            {
                IntPtr pData = dataHandle.AddrOfPinnedObject();
                IntPtr pppDst = ppDstHandle.AddrOfPinnedObject();
                var bufInfo = new H264Native.SBufferInfo();

                int ret = _decodeFrame(_decoder, pData, data.Length, pppDst, ref bufInfo);

                Logger.Debug("DecodeFrameNoDelay: ret={0} dataLen={1} bufStatus={2} width={3} height={4} fmt={5} stride0={6} stride1={7}",
                    ret, data.Length, bufInfo.iBufferStatus, bufInfo.UsrData.iWidth, bufInfo.UsrData.iHeight,
                    bufInfo.UsrData.iFormat, bufInfo.UsrData.iStride0, bufInfo.UsrData.iStride1);

                if (ret != 0)
                {
                    Logger.Warn("DecodeFrame returned error code {0}", ret);
                    return new DecodeResult { Status = DecodeStatus.Failed };
                }

                if (bufInfo.iBufferStatus != 1)
                {
                    // 解码成功但暂无输出帧（缓冲中）
                    return new DecodeResult { Status = DecodeStatus.NeedMoreInput };
                }

                // OpenH264 输出 I420 — 从 bufInfo.pDst0/1/2 读取 Y/U/V 平面指针，做 I420→BGRA 转换
                int w = bufInfo.UsrData.iWidth;
                int h = bufInfo.UsrData.iHeight;
                int yStride = bufInfo.UsrData.iStride0;
                int uvStride = bufInfo.UsrData.iStride1;

                if (w <= 0 || h <= 0 || yStride <= 0 || uvStride <= 0)
                {
                    Logger.Warn("DecodeFrame returned invalid dimensions: w={0} h={1} yStride={2} uvStride={3}",
                        w, h, yStride, uvStride);
                    return new DecodeResult { Status = DecodeStatus.Failed };
                }

                if (bufInfo.pDst0 == IntPtr.Zero || bufInfo.pDst1 == IntPtr.Zero || bufInfo.pDst2 == IntPtr.Zero)
                {
                    Logger.Warn("DecodeFrame returned null plane pointer: Y=0x{0:X} U=0x{1:X} V=0x{2:X}",
                        bufInfo.pDst0.ToInt64(), bufInfo.pDst1.ToInt64(), bufInfo.pDst2.ToInt64());
                    return new DecodeResult { Status = DecodeStatus.Failed };
                }

                // 期望的 BGRA 缓冲区大小
                int expectedBgraSize = w * h * 4;
                if (outputBuffer.Length < expectedBgraSize)
                {
                    Logger.Warn("Output buffer too small: got={0} expected={1} (w={2} h={3})",
                        outputBuffer.Length, expectedBgraSize, w, h);
                    return new DecodeResult { Status = DecodeStatus.Failed };
                }

                // I420→BGRA 转换
                // 服务端编码用 BT.601 limited range（Y=16-235, UV=16-240，带 +16 偏移），
                // 客户端必须用对应的 limited range 解码公式，否则白色 (Y=235) 解码为浅灰 (235)，
                // 黑色 (Y=16) 解码为深灰 (16)，对比度从 255:0 降到 235:16，画面"白屏看不清楚"。
                var outHandle = GCHandle.Alloc(outputBuffer, GCHandleType.Pinned);
                try
                {
                    ConvertI420ToBgra(
                        bufInfo.pDst0, bufInfo.pDst1, bufInfo.pDst2,
                        yStride, uvStride,
                        w, h,
                        outHandle.AddrOfPinnedObject(), w * 4);

                    // 诊断日志：仅第一次成功解码时打印前 4 个像素的 Y/U/V/BGRA 值
                    if (!_firstFrameLogged)
                    {
                        _firstFrameLogged = true;
                        unsafe
                        {
                            byte* yP = (byte*)bufInfo.pDst0;
                            byte* uP = (byte*)bufInfo.pDst1;
                            byte* vP = (byte*)bufInfo.pDst2;
                            byte* bgraP = (byte*)outHandle.AddrOfPinnedObject();
                            Logger.Info("FirstFrame YUV->BGRA: Y[0..3]={0},{1},{2},{3} U[0]={4} V[0]={5} -> BGRA[0]=B{6} G{7} R{8} A{9}",
                                yP[0], yP[1], yP[2], yP[3], uP[0], vP[0],
                                bgraP[0], bgraP[1], bgraP[2], bgraP[3]);
                        }
                    }
                }
                finally
                {
                    outHandle.Free();
                }

                return new DecodeResult
                {
                    Status = DecodeStatus.Ok,
                    Pixels = outputBuffer
                };
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "DecodeFrame threw exception");
                return new DecodeResult { Status = DecodeStatus.Failed };
            }
            finally
            {
                dataHandle.Free();
                ppDstHandle.Free();
            }
        }

        /// <summary>重置解码器（释放并重新创建）。</summary>
        public void Reset()
        {
            _initialized = false;
            if (_decoder != IntPtr.Zero)
            {
                H264Native.WelsDestroyDecoder(_decoder);
                _decoder = IntPtr.Zero;
            }
        }

        /// <summary>释放解码器资源。</summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>终结器：异常路径未显式 Dispose 时仍能回收原生句柄。</summary>
        ~H264DecoderNative()
        {
            Dispose(false);
        }

        private void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            if (_decoder != IntPtr.Zero)
            {
                H264Native.WelsDestroyDecoder(_decoder);
                _decoder = IntPtr.Zero;
            }
        }

        /// <summary>
        /// I420 (YUV 4:2:0) → BGRA32 颜色空间转换。
        /// I420 布局：Y plane = yStride × height，U/V plane = uvStride × (height/2)。
        /// 每 2×2 像素块共享 1 个 U 和 1 个 V 值。
        /// 使用 BT.601 limited range 转换公式（与服务端 ConvertBgraToI420 的 +16 偏移匹配）：
        ///   R = clamp((298*(Y-16) + 409*(V-128)) >> 8)
        ///   G = clamp((298*(Y-16) - 100*(U-128) - 208*(V-128)) >> 8)
        ///   B = clamp((298*(Y-16) + 517*(U-128)) >> 8)
        /// 若误用 full range 公式（无 -16 偏移），白色 Y=235 → R=235（浅灰），
        /// 黑色 Y=16 → R=16（深灰），对比度从 255:0 降到 235:16 → "白屏看不清"。
        /// </summary>
        private static unsafe void ConvertI420ToBgra(
            IntPtr yPlaneAddr, IntPtr uPlaneAddr, IntPtr vPlaneAddr,
            int yStride, int uvStride,
            int width, int height,
            IntPtr bgraAddr, int bgraStride)
        {
            byte* yPlane = (byte*)yPlaneAddr;
            byte* uPlane = (byte*)uPlaneAddr;
            byte* vPlane = (byte*)vPlaneAddr;
            byte* bgra = (byte*)bgraAddr;

            // 按 2×2 块处理（I420 是 4:2:0，每 4 个 Y 共享 1 个 U 和 1 个 V）
            int hBlocks = height >> 1;
            int wBlocks = width >> 1;

            for (int by = 0; by < hBlocks; by++)
            {
                int yRow0 = (by * 2) * yStride;
                int yRow1 = ((by * 2) + 1) * yStride;
                int uvRow = by * uvStride;
                int bgraRow0 = (by * 2) * bgraStride;
                int bgraRow1 = ((by * 2) + 1) * bgraStride;

                for (int bx = 0; bx < wBlocks; bx++)
                {
                    int x0 = bx * 2;
                    int x1 = x0 + 1;
                    int uvIdx = uvRow + bx;

                    int u = uPlane[uvIdx] - 128;
                    int v = vPlane[uvIdx] - 128;

                    // BT.601 limited range 整数系数（乘 256）
                    int rv = 409 * v;          // R 增量：1.596*(V-128)
                    int gu = -100 * u;         // G 增量（U 部分）：-0.391*(U-128)
                    int gv = -208 * v;         // G 增量（V 部分）：-0.813*(V-128)
                    int bu = 517 * u;          // B 增量：2.018*(U-128)

                    // 4 个像素：左上、右上、左下、右下
                    int y0 = yPlane[yRow0 + x0];
                    WriteBgraPixel(bgra + bgraRow0 + x0 * 4, y0, rv, gu + gv, bu);

                    int y1 = yPlane[yRow0 + x1];
                    WriteBgraPixel(bgra + bgraRow0 + x1 * 4, y1, rv, gu + gv, bu);

                    int y2 = yPlane[yRow1 + x0];
                    WriteBgraPixel(bgra + bgraRow1 + x0 * 4, y2, rv, gu + gv, bu);

                    int y3 = yPlane[yRow1 + x1];
                    WriteBgraPixel(bgra + bgraRow1 + x1 * 4, y3, rv, gu + gv, bu);
                }
            }

            // 处理奇数行/列（如果 width 或 height 是奇数）
            if ((width & 1) != 0)
            {
                int x = width - 1;
                for (int y = 0; y < height; y++)
                {
                    int uvY = y >> 1;
                    int u = uPlane[uvY * uvStride + (x >> 1)] - 128;
                    int v = vPlane[uvY * uvStride + (x >> 1)] - 128;
                    int yVal = yPlane[y * yStride + x];
                    WriteBgraPixel(bgra + y * bgraStride + x * 4, yVal, 409 * v, -100 * u - 208 * v, 517 * u);
                }
            }
            if ((height & 1) != 0)
            {
                int yRow = (height - 1) * yStride;
                int bgraRow = (height - 1) * bgraStride;
                for (int x = 0; x < width; x++)
                {
                    int u = uPlane[((height - 1) >> 1) * uvStride + (x >> 1)] - 128;
                    int v = vPlane[((height - 1) >> 1) * uvStride + (x >> 1)] - 128;
                    int yVal = yPlane[yRow + x];
                    WriteBgraPixel(bgra + bgraRow + x * 4, yVal, 409 * v, -100 * u - 208 * v, 517 * u);
                }
            }
        }

        /// <summary>写入单个 BGRA 像素（带 clamp）。使用 BT.601 limited range 整数公式。
        /// Y 先减 16（limited range 偏移），再乘 298（1.164×256）做范围扩展。
        /// 必须先 &gt;&gt; 8 再 clamp，否则 yScaled 溢出 &gt; 255 → 全白。</summary>
        private static unsafe void WriteBgraPixel(byte* p, int y, int rv, int gv, int bu)
        {
            // limited range: Y-16，乘 298（1.164*256）做范围扩展到 0-255
            int yScaled = (y - 16) * 298;
            int r = (yScaled + rv) >> 8;
            int g = (yScaled + gv) >> 8;
            int b = (yScaled + bu) >> 8;
            // Clamp 0-255
            if (r < 0) r = 0; else if (r > 255) r = 255;
            if (g < 0) g = 0; else if (g > 255) g = 255;
            if (b < 0) b = 0; else if (b > 255) b = 255;
            p[0] = (byte)b;   // B
            p[1] = (byte)g;   // G
            p[2] = (byte)r;   // R
            p[3] = 255;       // A
        }
    }
}
