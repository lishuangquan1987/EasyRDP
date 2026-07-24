using System;
using System.Runtime.InteropServices;

namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// OpenH264 原生编码器（net40 路径，XP 兼容）。
    /// 通过 P/Invoke + vtable 调用 OpenH264 DLL。
    /// </summary>
    public class H264EncoderNative : IVideoEncoder
    {
        private IntPtr _encoder;
        private H264Native.EncodeFrameDelegate _encodeFrame;
        private int _width;
        private int _height;
        private int _targetBitrate;
        private bool _initialized;
        private bool _disposed;

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
                    _encoder = IntPtr.Zero;
                    return false;
                }
                return true;
            }
            catch
            {
                _encoder = IntPtr.Zero;
                return false;
            }
        }

        public void Initialize(int width, int height, int targetBitrate)
        {
            if (_disposed) throw new ObjectDisposedException("H264EncoderNative");
            if (_encoder == IntPtr.Zero)
            {
                if (!TryCreateEncoder())
                    throw new InvalidOperationException("Failed to create OpenH264 encoder");
            }

            _width = width;
            _height = height;
            _targetBitrate = targetBitrate;

            var init = H264Native.GetVTableDelegate<H264Native.InitializeEncoderDelegate>(_encoder, 0);
            var param = new H264Native.SEncParamBase();
            param.Init(width, height, targetBitrate);
            int ret = init(_encoder, ref param);
            if (ret != 0)
                throw new InvalidOperationException("OpenH264 encoder Initialize failed: " + ret);

            _encodeFrame = H264Native.GetVTableDelegate<H264Native.EncodeFrameDelegate>(_encoder, 4);
            _initialized = true;
        }

        public EncodedFrame Encode(byte[] pixels, bool forceKeyframe)
        {
            if (!_initialized || _disposed || _encoder == IntPtr.Zero)
                return new EncodedFrame();

            // Pin pixel buffer and create source picture
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                var pic = new H264Native.SSourcePicture();
                pic.Init(handle.AddrOfPinnedObject(), _width, _height, forceKeyframe);

                var bsInfo = new H264Native.SFrameBSInfo();
                int ret = _encodeFrame(_encoder, ref pic, ref bsInfo);
                if (ret != 0)
                    return new EncodedFrame();

                if (bsInfo.iFrameSizeInBytes <= 0 || bsInfo.pBsBuf == IntPtr.Zero)
                    return new EncodedFrame();

                byte[] data = new byte[bsInfo.iFrameSizeInBytes];
                Marshal.Copy(bsInfo.pBsBuf, data, 0, data.Length);

                return new EncodedFrame
                {
                    Data = data,
                    IsKeyframe = bsInfo.IsKeyFrame,
                    Width = _width,
                    Height = _height
                };
            }
            finally
            {
                handle.Free();
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
