using System;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security;
using NLog;

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

            var init = H264Native.GetVTableDelegate<H264Native.InitializeEncoderDelegate>(
                _encoder, H264Native.VTABLE_SLOT_INITIALIZE);
            var param = new H264Native.SEncParamBase();
            param.Init(width, height, targetBitrate);
            int ret = init(_encoder, ref param);
            if (ret != 0)
            {
                Logger.Error("OpenH264 Initialize failed: return code {0}, resolution={1}x{2} bitrate={3}",
                    ret, width, height, targetBitrate);
                throw new InvalidOperationException("OpenH264 encoder Initialize failed: " + ret);
            }

            Logger.Info("OpenH264 encoder initialized: {0}x{1} @ {2} bps", width, height, targetBitrate);

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

        private static unsafe void ConvertBgraToI420(IntPtr pBgra, IntPtr pY, IntPtr pU, IntPtr pV, int w, int h)
        {
            byte* src = (byte*)pBgra;
            byte* dstY = (byte*)pY;
            byte* dstU = (byte*)pU;
            byte* dstV = (byte*)pV;
            int uvIndex = 0;
            for (int j = 0; j < h; j++)
            {
                for (int i = 0; i < w; i++)
                {
                    int off = (j * w + i) * 4;
                    int r = src[off + 2], g = src[off + 1], b = src[off];
                    dstY[j * w + i] = ClampByte((((66 * r + 129 * g + 25 * b + 128) >> 8) + 16));
                    if ((j & 1) == 0 && (i & 1) == 0)
                    {
                        dstU[uvIndex] = ClampByte((((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128));
                        dstV[uvIndex] = ClampByte((((112 * r - 94 * g - 18 * b + 128) >> 8) + 128));
                        uvIndex++;
                    }
                }
            }
        }

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
