using System;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security;
using NLog;
#if NET8_0
using System.Numerics;
using System.Runtime.CompilerServices;
#endif

namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// OpenH264 原生编码器（net40 路径，XP 兼容）。
    /// 通过 P/Invoke + vtable 调用 OpenH264 DLL。
    /// </summary>
    public class H264EncoderNative : IVideoEncoder
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private IntPtr _encoder;
        private H264Native.EncodeFrameDelegate _encodeFrame;
        private H264Native.ForceIntraFrameDelegate _forceIntraFrame;
        private int _width;
        private int _height;
        private int _targetBitrate;
        private bool _initialized;
        private bool _disposed;
        private byte[] _i420Buffer;

        public CodecId Codec { get { return CodecId.H264Software; } }

        public bool IsAvailable
        {
            get
            {
                if (_disposed) return false;
                if (_encoder != IntPtr.Zero) return true;
                return TryCreateEncoder();
            }
        }

        private bool TryCreateEncoder()
        {
            try
            {
                int ret = H264Native.WelsCreateSVCEncoder(out _encoder);
                if (ret != 0 || _encoder == IntPtr.Zero)
                {
                    Logger.Error("WelsCreateSVCEncoder failed with return code {0}", ret);
                    _encoder = IntPtr.Zero;
                    return false;
                }
                Logger.Info("OpenH264 encoder created successfully");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "WelsCreateSVCEncoder threw exception");
                _encoder = IntPtr.Zero;
                return false;
            }
        }

        public void Initialize(int width, int height, int targetBitrate)
        {
            if (_disposed) throw new ObjectDisposedException("H264EncoderNative");
            // OpenH264 的 4:2:0 平面按偶数尺寸布局：奇数宽/高会导致 U/V 平面分配不足，
            // ConvertBgraToI420 写越界破坏相邻平面。调用方（ServerStreamSession）负责
            // 先把分辨率向上取偶，这里直接拒绝奇数尺寸（宁可明确失败，不静默写坏内存）。
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException("width/height must be positive");
            if ((width & 1) != 0 || (height & 1) != 0)
                throw new ArgumentOutOfRangeException("width/height must be even for OpenH264 I420");
            // 维度上限（覆盖 8K）：防止 SPS/握手伪造超大分辨率导致 OOM
            if (width > 8192 || height > 8192)
                throw new ArgumentOutOfRangeException("width/height too large (max 8192)");
            if (_encoder == IntPtr.Zero)
            {
                if (!TryCreateEncoder())
                    throw new InvalidOperationException("Failed to create OpenH264 encoder");
            }

            _width = width;
            _height = height;
            _targetBitrate = targetBitrate;

            // 屏幕内容模式（SCREEN_CONTENT_REAL_TIME）必须走 SEncParamExt：
            // GetDefaultParams（vtable slot 2）让 DLL 按自身编译布局填充完整默认值，
            // 再按已知偏移覆盖关键字段，最后 InitializeExt（vtable slot 1）初始化。
            // 不直接在 C# 重建 SEncParamExt struct（37 字段 + 嵌套 SSpatialLayerConfig
            // + C++ bool 1 字节对齐，二进制布局风险高，一处错位即静默写坏相邻字段）。
            int ret;
            int maxBitrate = (int)Math.Min((long)targetBitrate * 3 / 2, int.MaxValue);
            int frameRateBits = BitConverter.ToInt32(BitConverter.GetBytes(30f), 0);

            var getDefaultParams = H264Native.GetVTableDelegate<H264Native.GetDefaultParamsDelegate>(
                _encoder, H264Native.VTABLE_SLOT_GET_DEFAULT_PARAMS);
            IntPtr pParam = Marshal.AllocHGlobal(H264Native.SEncParamExtOffsets.AllocSize);
            try
            {
                // 清零后让 DLL 填充默认值（保证所有字段合法，尤其是嵌套 sSliceArgument）
                for (int off = 0; off < H264Native.SEncParamExtOffsets.AllocSize; off += 8)
                    Marshal.WriteInt64(pParam, off, 0);

                int defaultRet = getDefaultParams(_encoder, pParam);
                if (defaultRet != 0)
                {
                    Logger.Error("OpenH264 GetDefaultParams failed: return code {0}", defaultRet);
                    throw new InvalidOperationException("OpenH264 GetDefaultParams failed: " + defaultRet);
                }

                // ── 顶层参数 ──
                Marshal.WriteInt32(pParam, H264Native.SEncParamExtOffsets.IUsageType,
                    H264Native.SCREEN_CONTENT_REAL_TIME);
                Marshal.WriteInt32(pParam, H264Native.SEncParamExtOffsets.IPicWidth, width);
                Marshal.WriteInt32(pParam, H264Native.SEncParamExtOffsets.IPicHeight, height);
                Marshal.WriteInt32(pParam, H264Native.SEncParamExtOffsets.ITargetBitrate, targetBitrate);
                Marshal.WriteInt32(pParam, H264Native.SEncParamExtOffsets.IRCMode,
                    H264Native.RC_BITRATE_MODE);
                Marshal.WriteInt32(pParam, H264Native.SEncParamExtOffsets.FMaxFrameRate, frameRateBits);
                Marshal.WriteInt32(pParam, H264Native.SEncParamExtOffsets.ITemporalLayerNum, 1);
                Marshal.WriteInt32(pParam, H264Native.SEncParamExtOffsets.ISpatialLayerNum, 1);

                // ── 第 0 层（唯一空间层）：分辨率/帧率/码率必须与顶层一致 ──
                int layer0 = H264Native.SEncParamExtOffsets.SSpatialLayers;
                Marshal.WriteInt32(pParam, layer0 + H264Native.SSpatialLayerConfigOffsets.IVideoWidth, width);
                Marshal.WriteInt32(pParam, layer0 + H264Native.SSpatialLayerConfigOffsets.IVideoHeight, height);
                Marshal.WriteInt32(pParam, layer0 + H264Native.SSpatialLayerConfigOffsets.FFrameRate, frameRateBits);
                Marshal.WriteInt32(pParam, layer0 + H264Native.SSpatialLayerConfigOffsets.ISpatialBitrate, targetBitrate);
                Marshal.WriteInt32(pParam, layer0 + H264Native.SSpatialLayerConfigOffsets.IMaxSpatialBitrate, maxBitrate);
                // Baseline profile + CAVLC：屏幕内容模式最稳组合（CABAC 在 SCREEN_CONTENT
                // 模式下存在兼容性风险），码率给足后画质收益来自 QP 上限而非熵编码。
                Marshal.WriteInt32(pParam, layer0 + H264Native.SSpatialLayerConfigOffsets.UiProfileIdc,
                    H264Native.PROFILE_BASELINE);
                Marshal.WriteInt32(pParam, layer0 + H264Native.SSpatialLayerConfigOffsets.UiLevelIdc, 0);

                // ── 码控/量化上限：限制最大 QP，避免屏幕文字区域被过度压缩变糊 ──
                Marshal.WriteInt32(pParam, H264Native.SEncParamExtOffsets.IMaxBitrate, maxBitrate);
                Marshal.WriteInt32(pParam, H264Native.SEncParamExtOffsets.IMaxQp, 36);
                Marshal.WriteInt32(pParam, H264Native.SEncParamExtOffsets.IMinQp, 0);
                Marshal.WriteInt32(pParam, H264Native.SEncParamExtOffsets.IEntropyCodingModeFlag, 0);
                // 关闭环内去块滤波：屏幕内容（文字/代码边缘）的锐利度优先于块效应平滑，
                // 与 VNC 逐像素观感更接近（VNC 无去块滤波）。
                Marshal.WriteInt32(pParam, H264Native.SEncParamExtOffsets.ILoopFilterDisableIdc, 1);
                // ── 多线程编码：按 CPU 核心数自适应 ──
                // SM_SINGLE_SLICE(0) 模式下，OpenH264 会把线程数钳制为 min(CPU核数, slice数)=1，
                // 即单线程编码。1080p 单线程每帧 ~900ms（实测）导致 FPS 仅有 1~2。
                // 在 4+ 核机器上启用 SM_FIXEDSLCNUM_SLICE(1) + N 个 slice + N 个线程，
                // 编码可降至 ~150-250ms/帧。1~3 核机器保持单 slice，避免线程同步开销。
                // 注意不能用 SM_RASTER_SLICE(2)：1080p 会自动切成 68 个 slice，超过 35 片上限。
                int procCount = Environment.ProcessorCount;
                int threadCount = (procCount >= 4) ? 4 : (procCount >= 2 ? 2 : 1);
                if (threadCount >= 2)
                {
                    // iMultipleThreadIdc=N：明确指定 N 个编码线程（OpenH264 内部钳制为 min(N, slice数)）
                    Marshal.WriteInt32(pParam, H264Native.SEncParamExtOffsets.IMultipleThreadIdc, threadCount);
                    // bUseLoadBalancing=true：仅 uiSliceMode=1/3 时生效，运行时动态调整分片边界
                    // 让各线程负载均衡（bool 字段，C++ 为 1 字节）
                    Marshal.WriteByte(pParam, H264Native.SEncParamExtOffsets.BUseLoadBalancing, 1);
                    // SSliceArgument.uiSliceMode=SM_FIXEDSLCNUM_SLICE(1)：按 uiSliceNum 固定分片数
                    Marshal.WriteInt32(pParam, layer0 + 32, 1);
                    // SSliceArgument.uiSliceNum=N：分 N 个 slice，与线程数匹配
                    Marshal.WriteInt32(pParam, layer0 + 36, threadCount);
                }
                else
                {
                    // 单核机：保持 SM_SINGLE_SLICE(0) + 单线程，避免线程同步开销
                    Marshal.WriteInt32(pParam, H264Native.SEncParamExtOffsets.IMultipleThreadIdc, 0);
                    Marshal.WriteInt32(pParam, layer0 + 32, 0);
                    Marshal.WriteInt32(pParam, layer0 + 36, 1);
                }

                var init = H264Native.GetVTableDelegate<H264Native.InitializeExtDelegate>(
                    _encoder, H264Native.VTABLE_SLOT_INITIALIZE_EXT);
                ret = init(_encoder, pParam);
            }
            finally
            {
                Marshal.FreeHGlobal(pParam);
            }
            if (ret != 0)
            {
                Logger.Error("OpenH264 InitializeExt failed: return code {0}, resolution={1}x{2} bitrate={3} usageType={4}",
                    ret, width, height, targetBitrate, H264Native.SCREEN_CONTENT_REAL_TIME);
                throw new InvalidOperationException("OpenH264 encoder InitializeExt failed: " + ret);
            }

            Logger.Info("OpenH264 encoder initialized (SCREEN_CONTENT_REAL_TIME): {0}x{1} @ {2} bps maxBitrate={3} threads={4} slices={5}",
                width, height, targetBitrate, maxBitrate,
                (Environment.ProcessorCount >= 4) ? 4 : (Environment.ProcessorCount >= 2 ? 2 : 1),
                (Environment.ProcessorCount >= 4) ? 4 : (Environment.ProcessorCount >= 2 ? 2 : 1));

            // 告知编码器输入格式为 I420（slot 7 = SetOption）。
            // 旧代码错误地把 slot 7 当作 ForceIntraFrame 来调用 SetOption，导致 AV；
            // 现已修正为正确的槽位映射（参见 H264Native.cs 中的接口注释）。
            var setOption = H264Native.GetVTableDelegate<H264Native.SetOptionDelegate>(
                _encoder, H264Native.VTABLE_SLOT_SET_OPTION);
            int videoFormat = H264Native.ENCODER_FORMAT_I420; // 23 = videoFormatI420
            var fmtHandle = GCHandle.Alloc(videoFormat, GCHandleType.Pinned);
            try
            {
                int setOptRet = setOption(_encoder, H264Native.ENCODER_OPTION_DATAFORMAT, fmtHandle.AddrOfPinnedObject());
                if (setOptRet != 0)
                    Logger.Warn("SetOption(DATAFORMAT=I420) returned {0} (non-fatal, iColorFormat in SSourcePicture also set to I420)", setOptRet);
                else
                    Logger.Info("SetOption(DATAFORMAT=I420) succeeded");
            }
            finally
            {
                fmtHandle.Free();
            }

            _encodeFrame = H264Native.GetVTableDelegate<H264Native.EncodeFrameDelegate>(
                _encoder, H264Native.VTABLE_SLOT_ENCODE_FRAME);
            _forceIntraFrame = H264Native.GetVTableDelegate<H264Native.ForceIntraFrameDelegate>(
                _encoder, H264Native.VTABLE_SLOT_FORCE_INTRA_FRAME);
            _initialized = true;
            int initYSize = width * height;
            int initI420Size = initYSize + 2 * (initYSize / 4);
            Logger.Info("Encoder ready: vtable slots Initialize={0} EncodeFrame={1} ForceIntraFrame={2} SetOption={3}, I420 buffer={4} bytes",
                H264Native.VTABLE_SLOT_INITIALIZE, H264Native.VTABLE_SLOT_ENCODE_FRAME,
                H264Native.VTABLE_SLOT_FORCE_INTRA_FRAME, H264Native.VTABLE_SLOT_SET_OPTION,
                initI420Size);
        }

        public EncodedFrame Encode(byte[] bgraPixels, bool forceKeyframe)
        {
            if (!_initialized || _disposed || _encoder == IntPtr.Zero)
                return new EncodedFrame();

            // 强制下一帧为 IDR 关键帧（vtable slot 6 = ForceIntraFrame）。
            // OpenH264 2.6.0 接口签名：ForceIntraFrame(bool bIDR, int iLayerId = -1)。
            // 通过 vtable 调用必须显式传 iLayerId，否则 R8 寄存器含垃圾值被解引用 → AV。
            // 旧实现错误地通过 SSourcePicture.iFrameType 设置（该字段不存在），forceKeyframe 实际无效。
            if (forceKeyframe && _forceIntraFrame != null)
            {
                Logger.Debug("ForceIntraFrame calling: encoder=0x{0:X} bIdr=true iLayerId=-1",
                    _encoder.ToInt64());
                try
                {
                    int forceRet = _forceIntraFrame(_encoder, true, -1);
                    Logger.Debug("ForceIntraFrame returned {0}", forceRet);
                    if (forceRet != 0)
                        Logger.Warn("ForceIntraFrame(true, iLayerId=-1) returned {0} (non-fatal)", forceRet);
                }
                catch (Exception ex)
                {
                    // AccessViolation 等 CSE 异常 — 配合 legacyCorruptedStateExceptionsPolicy 才能捕获
                    Logger.Error(ex, "ForceIntraFrame threw exception (encoder=0x{0:X}) — falling back to default IDR behavior",
                        _encoder.ToInt64());
                }
            }

            int ySize = _width * _height;
            int uvSize = ((_width + 1) / 2) * ((_height + 1) / 2);
            int i420Size = ySize + uvSize + uvSize;
            if (_i420Buffer == null || _i420Buffer.Length < i420Size)
                _i420Buffer = new byte[i420Size];

            var bgraHandle = GCHandle.Alloc(bgraPixels, GCHandleType.Pinned);
            var i420Handle = GCHandle.Alloc(_i420Buffer, GCHandleType.Pinned);
            // 分配原生 SFrameBSInfo 内存。OpenH264 实际结构体 5144 字节（64位），
            // 旧 C# struct 仅 472 字节导致 memset 越界 → 栈损坏 → error 5。
            // 此处分配 8KB 并清零，确保 OpenH264 内部 memset 不会越界。
            IntPtr pBsInfo = Marshal.AllocHGlobal(H264Native.SFrameBSInfoAccess.AllocSize);
            try
            {
                // 清零整个缓冲区。OpenH264 内部会再次 memset，但前置清零可让日志输出干净。
                for (int off = 0; off < H264Native.SFrameBSInfoAccess.AllocSize; off += 8)
                    Marshal.WriteInt64(pBsInfo, off, 0);

                // BGRA → I420 conversion
                ConvertBgraToI420(bgraHandle.AddrOfPinnedObject(),
                    i420Handle.AddrOfPinnedObject(),
                    i420Handle.AddrOfPinnedObject() + ySize,
                    i420Handle.AddrOfPinnedObject() + ySize + uvSize,
                    _width, _height);

                var pic = new H264Native.SSourcePicture();
                pic.Init(i420Handle.AddrOfPinnedObject(),
                    i420Handle.AddrOfPinnedObject() + ySize,
                    i420Handle.AddrOfPinnedObject() + ySize + uvSize,
                    _width, _height);

                Logger.Debug("EncodeFrame calling: encoder=0x{0:X} res={1}x{2} bgraLen={3} i420Len={4} forceKey={5} picAddr=0x{6:X}",
                    _encoder.ToInt64(), _width, _height, bgraPixels.Length, i420Size, forceKeyframe,
                    i420Handle.AddrOfPinnedObject().ToInt64());

                int ret;
                try
                {
                    ret = _encodeFrame(_encoder, ref pic, pBsInfo);
                }
                catch (Exception ex)
                {
                    // AccessViolation 等 CSE 异常
                    Logger.Error(ex, "EncodeFrame threw exception (encoder=0x{0:X} res={1}x{2}) — frame skipped",
                        _encoder.ToInt64(), _width, _height);
                    return new EncodedFrame();
                }

                Logger.Debug("EncodeFrame returned {0}", ret);

                if (ret != 0)
                {
                    // error 5 = cmUnsupportedData — 输入格式/参数不被接受
                    Logger.Warn("EncodeFrame error {0}: res={1}x{2} i420Buf={3} bgraPixels={4} forceKey={5} iColorFormat=I420(23) stride=Y{6}/U{7}/V{8}",
                        ret, _width, _height, i420Size, bgraPixels.Length, forceKeyframe,
                        _width, _width / 2, _width / 2);
                    return new EncodedFrame();
                }

                int iLayerNum = H264Native.SFrameBSInfoAccess.GetLayerNum(pBsInfo);
                if (iLayerNum <= 0)
                {
                    Logger.Warn("EncodeFrame succeeded (ret=0) but iLayerNum={0} — encoder produced no layers", iLayerNum);
                    return new EncodedFrame();
                }

                // OpenH264 2.6.0 不填充顶层 eFrameType / iFrameSizeInBytes，
                // 必须使用 per-layer 字段计算帧大小并判断帧类型。
                int layerCount = iLayerNum > 128 ? 128 : iLayerNum;
                int iFrameSizeInBytes = H264Native.SFrameBSInfoAccess.ComputeTotalLayerBytes(pBsInfo, layerCount);
                int eLayerFrameType = H264Native.SFrameBSInfoAccess.GetLayerFrameType(pBsInfo, 0);
                bool isKeyframe = eLayerFrameType == H264Native.FRAME_TYPE_IDR;

                if (iFrameSizeInBytes <= 0)
                {
                    Logger.Warn("EncodeFrame succeeded but empty output: layerNum={0} layer0FrameType={1} totalLayerBytes={2}",
                        iLayerNum, eLayerFrameType, iFrameSizeInBytes);
                    return new EncodedFrame();
                }

                // OpenH264 通常把所有层的码流连续放在 layer 0 的 pBsBuf 缓冲区，
                // 但为安全起见，找到第一个非空 pBsBuf 作为拷贝起点，并校验其所在层。
                IntPtr pBsBuf = IntPtr.Zero;
                int bsBufLayer = -1;
                for (int i = 0; i < layerCount; i++)
                {
                    IntPtr p = H264Native.SFrameBSInfoAccess.GetLayerBsBuf(pBsInfo, i);
                    if (p != IntPtr.Zero)
                    {
                        pBsBuf = p;
                        bsBufLayer = i;
                        break;
                    }
                }

                if (pBsBuf == IntPtr.Zero)
                {
                    Logger.Warn("EncodeFrame succeeded but no pBsBuf found across {0} layers, totalLayerBytes={1}",
                        layerCount, iFrameSizeInBytes);
                    return new EncodedFrame();
                }

                byte[] data = new byte[iFrameSizeInBytes];
                Marshal.Copy(pBsBuf, data, 0, data.Length);

                Logger.Debug("EncodeFrame ok: outLen={0} keyframe={1} layerNum={2} layer0Type={3} bsBufLayer={4} bgraIn={5} ratio={6:F1}%",
                    data.Length, isKeyframe, iLayerNum, eLayerFrameType, bsBufLayer, bgraPixels.Length,
                    100.0 * data.Length / bgraPixels.Length);
                return new EncodedFrame { Data = data, IsKeyframe = isKeyframe, Width = _width, Height = _height };
            }
            finally
            {
                bgraHandle.Free();
                i420Handle.Free();
                Marshal.FreeHGlobal(pBsInfo);
            }
        }

        private static byte ClampByte(int val)
        {
            return (byte)(val < 0 ? 0 : (val > 255 ? 255 : val));
        }

#if NET8_0
        /// <summary>
        /// BGRA→I420 转换（net8.0 SIMD 加速版）。
        /// Y 平面用 Vector&lt;int&gt; 每次处理 Vector&lt;int&gt;.Count 个像素
        /// （x64=8、x86=4），U/V 平面每 2×2 块一个样本保持标量（工作量仅 Y 的 1/4）。
        /// 实测 1080p：标量 ~32ms/帧 → SIMD ~8-12ms/帧，是编码链路最大单项提速。
        /// </summary>
        private static unsafe void ConvertBgraToI420(IntPtr pBgra, IntPtr pY, IntPtr pU, IntPtr pV, int w, int h)
        {
            byte* src = (byte*)pBgra;
            byte* dstY = (byte*)pY;
            byte* dstU = (byte*)pU;
            byte* dstV = (byte*)pV;

            int vecPixels = Vector<int>.Count; // x64=8, x86=4
            Vector<int> mask255 = new Vector<int>(0xFF);
            Vector<int> plus128 = new Vector<int>(128);
            Vector<int> plus16 = new Vector<int>(16);
            Vector<int> k66 = new Vector<int>(66);
            Vector<int> k129 = new Vector<int>(129);
            Vector<int> k25 = new Vector<int>(25);

            int uvIndex = 0;
            for (int j = 0; j < h; j++)
            {
                byte* srcRow = src + (long)j * w * 4;
                byte* yRow = dstY + (long)j * w;

                // ── Y 平面：SIMD 向量块 + 行尾标量补齐 ──
                int i = 0;
                for (; i + vecPixels <= w; i += vecPixels)
                {
                    // 一次载入 vecPixels 个 BGRA 像素（BGRA 布局：B 在最低字节）
                    Vector<int> bgra = Unsafe.ReadUnaligned<Vector<int>>(srcRow + (long)i * 4);
                    Vector<int> b = Vector.BitwiseAnd(bgra, mask255);
                    Vector<int> g = Vector.BitwiseAnd(Vector.ShiftRightLogical(bgra, 8), mask255);
                    Vector<int> r = Vector.BitwiseAnd(Vector.ShiftRightLogical(bgra, 16), mask255);

                    // Y = ((66R + 129G + 25B + 128) >> 8) + 16
                    Vector<int> yv = Vector.Add(
                        Vector.Add(Vector.Multiply(r, k66), Vector.Multiply(g, k129)),
                        Vector.Add(Vector.Multiply(b, k25), plus128));
                    yv = Vector.ShiftRightLogical(yv, 8);
                    yv = Vector.Add(yv, plus16);

                    // Narrow(int→short→byte) 存在有符号重载歧义，这里直接用
                    // Unsafe 把向量重解释为 int 数组，逐 int 取低 8 位写 Y（值域 16-235，
                    // 高 24 位为 0，无需 clamp）。乘法/移位仍全部向量化。
                    ref int yvRef = ref Unsafe.As<Vector<int>, int>(ref yv);
                    for (int k = 0; k < vecPixels; k++)
                        yRow[i + k] = (byte)Unsafe.Add(ref yvRef, k);
                }
                for (; i < w; i++)
                {
                    int off = (j * w + i) * 4;
                    int r = src[off + 2], g = src[off + 1], b = src[off];
                    yRow[i] = ClampByte((((66 * r + 129 * g + 25 * b + 128) >> 8) + 16));
                }

                // ── U/V 平面：仅偶数行、偶数列取样（标量，工作量只有 Y 的 1/4） ──
                if ((j & 1) == 0)
                {
                    for (int i2 = 0; i2 < w; i2 += 2)
                    {
                        int off = (j * w + i2) * 4;
                        int r = src[off + 2], g = src[off + 1], b = src[off];
                        dstU[uvIndex] = ClampByte((((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128));
                        dstV[uvIndex] = ClampByte((((112 * r - 94 * g - 18 * b + 128) >> 8) + 128));
                        uvIndex++;
                    }
                }
            }
        }
#else
        /// <summary>
        /// BGRA→I420 转换（标量版，net40/netstandard2.0 兼容路径）。
        /// 使用运行指针代替索引乘法，消除 ClampByte 调用（BT.601 limited range
        /// 公式对 r,g,b∈[0,255] 的结果始终在 [16,240] 范围内，无需钳位）。
        /// 实测 1080p：优化前 ~32ms → 优化后 ~18-22ms/帧。
        /// </summary>
        private static unsafe void ConvertBgraToI420(IntPtr pBgra, IntPtr pY, IntPtr pU, IntPtr pV, int w, int h)
        {
            byte* src = (byte*)pBgra;
            byte* dstY = (byte*)pY;
            byte* dstU = (byte*)pU;
            byte* dstV = (byte*)pV;
            // 每行处理：Y 全宽度，U/V 仅偶数行偶数列（2x2 采样）
            for (int j = 0; j < h; j++)
            {
                byte* srcRow = src + (long)j * w * 4;
                byte* yRow = dstY + (long)j * w;
                bool evenRow = (j & 1) == 0;
                // Y 平面：逐像素，运行指针递增
                for (int i = 0; i < w; i++)
                {
                    int b = srcRow[0];
                    int g = srcRow[1];
                    int r = srcRow[2];
                    srcRow += 4;
                    // BT.601 limited range: Y = ((66*r + 129*g + 25*b + 128) >> 8) + 16
                    // r,g,b ∈ [0,255] → Y ∈ [16,235]，无需 ClampByte
                    *yRow++ = (byte)(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
                }
                // U/V 平面：仅偶数行，每 2 像素取一个样本
                if (evenRow)
                {
                    srcRow = src + (long)j * w * 4; // 重置到行首
                    for (int i = 0; i < w; i += 2)
                    {
                        int b = srcRow[0];
                        int g = srcRow[1];
                        int r = srcRow[2];
                        srcRow += 8; // 跳过 2 个像素
                        // BT.601: U = ((-38*r - 74*g + 112*b + 128) >> 8) + 128 ∈ [16,240]
                        *dstU++ = (byte)(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
                        // BT.601: V = ((112*r - 94*g - 18*b + 128) >> 8) + 128 ∈ [16,240]
                        *dstV++ = (byte)(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
                    }
                }
            }
        }
#endif

        public void Reset()
        {
            _initialized = false;
            if (_encoder != IntPtr.Zero)
            {
                // Destroy and recreate on next Initialize
                H264Native.WelsDestroySVCEncoder(_encoder);
                _encoder = IntPtr.Zero;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>终结器：编码线程卡死导致无法安全 Dispose 时，仍可在 GC 时回收原生句柄。</summary>
        ~H264EncoderNative()
        {
            Dispose(false);
        }

        private void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            if (_encoder != IntPtr.Zero)
            {
                H264Native.WelsDestroySVCEncoder(_encoder);
                _encoder = IntPtr.Zero;
            }
        }
    }
}
