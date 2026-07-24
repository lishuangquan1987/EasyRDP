using System;
using System.Runtime.InteropServices;

namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// OpenH264 原生解码器。
    /// 通过 P/Invoke + vtable 调用 OpenH264 DLL。
    /// </summary>
    public class H264DecoderNative : IVideoDecoder
    {
        private IntPtr _decoder;
        private H264Native.DecodeFrameDelegate _decodeFrame;
        private int _width;
        private int _height;
        private bool _initialized;
        private bool _disposed;

        public CodecId Codec { get { return CodecId.H264Software; } }

        public bool IsAvailable
        {
            get
            {
                if (_disposed) return false;
                if (_decoder != IntPtr.Zero) return true;
                return TryCreateDecoder();
            }
        }

        private bool TryCreateDecoder()
        {
            try
            {
                int ret = H264Native.WelsCreateDecoder(out _decoder);
                if (ret != 0 || _decoder == IntPtr.Zero)
                {
                    _decoder = IntPtr.Zero;
                    return false;
                }
                return true;
            }
            catch
            {
                _decoder = IntPtr.Zero;
                return false;
            }
        }

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

            var init = H264Native.GetVTableDelegate<H264Native.InitializeDecoderDelegate>(_decoder, 0);
            var param = new H264Native.SDecodingParam();
            param.Init();
            int ret = init(_decoder, ref param);
            if (ret != 0)
                throw new InvalidOperationException("OpenH264 decoder Initialize failed: " + ret);

            _decodeFrame = H264Native.GetVTableDelegate<H264Native.DecodeFrameDelegate>(_decoder, 2);
            _initialized = true;
        }

        public DecodeResult Decode(byte[] data)
        {
            int expectedSize = _width * _height * 4;
            if (expectedSize <= 0)
                return new DecodeResult { Status = DecodeStatus.Failed };

            byte[] outputBuffer = new byte[expectedSize];
            return Decode(data, outputBuffer);
        }

        public DecodeResult Decode(byte[] data, byte[] outputBuffer)
        {
            if (!_initialized || _disposed || _decoder == IntPtr.Zero)
                return new DecodeResult { Status = DecodeStatus.Failed };
            if (data == null || data.Length == 0)
                return new DecodeResult { Status = DecodeStatus.NeedMoreInput };

            var dataHandle = GCHandle.Alloc(data, GCHandleType.Pinned);
            var outHandle = GCHandle.Alloc(outputBuffer, GCHandleType.Pinned);
            try
            {
                IntPtr pData = dataHandle.AddrOfPinnedObject();
                IntPtr pDst = outHandle.AddrOfPinnedObject();
                var bufInfo = new H264Native.SBufferInfo();
                IntPtr ppDst = pDst;

                int ret = _decodeFrame(_decoder, pData, data.Length, ref ppDst, ref bufInfo);

                if (ret != 0)
                    return new DecodeResult { Status = DecodeStatus.Failed };

                if (bufInfo.iBufferStatus == 1)
                {
                    return new DecodeResult
                    {
                        Status = DecodeStatus.Ok,
                        Pixels = outputBuffer
                    };
                }

                // Decoding succeeded but no frame ready yet (buffering)
                return new DecodeResult { Status = DecodeStatus.NeedMoreInput };
            }
            catch (Exception)
            {
                return new DecodeResult { Status = DecodeStatus.Failed };
            }
            finally
            {
                dataHandle.Free();
                outHandle.Free();
            }
        }

        public void Reset()
        {
            _initialized = false;
            if (_decoder != IntPtr.Zero)
            {
                H264Native.WelsDestroyDecoder(_decoder);
                _decoder = IntPtr.Zero;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_decoder != IntPtr.Zero)
            {
                H264Native.WelsDestroyDecoder(_decoder);
                _decoder = IntPtr.Zero;
            }
        }
    }
}
