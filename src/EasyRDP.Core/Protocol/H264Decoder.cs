namespace EasyRDP.Core.Protocol
{
#if NET8_0_OR_GREATER
    using System;
    using System.Runtime.InteropServices;

    public class H264Decoder
    {
        private IntPtr _decoder;
        private byte[] _yBuffer;
        private byte[] _uBuffer;
        private byte[] _vBuffer;
        private byte[] _bgraBuffer;
        private bool _isInitialized;
        private int _width;
        private int _height;

        public CodecId Codec
        {
            get { return CodecId.H264Software; }
        }

        public bool IsAvailable
        {
            get { return OpenH264Native.IsAvailable(); }
        }

        public void Initialize(int width, int height)
        {
            if (!IsAvailable)
                throw new NotSupportedException("OpenH264 not available");

            if (_isInitialized)
                Dispose();

            _width = width;
            _height = height;

            int ySize = width * height;
            int uvSize = (width / 2) * (height / 2);
            _yBuffer = new byte[ySize];
            _uBuffer = new byte[uvSize];
            _vBuffer = new byte[uvSize];
            _bgraBuffer = new byte[ySize * 4];

            int result = OpenH264Native.WelsCreateDecoder(ref _decoder);
            if (result != OpenH264Native.ERROR_CODE_NONE || _decoder == IntPtr.Zero)
                throw new InvalidOperationException("Failed to create decoder");

            result = OpenH264Native.WelsInitializeDecoder(_decoder);
            if (result != OpenH264Native.ERROR_CODE_NONE)
            {
                OpenH264Native.WelsDestroyDecoder(_decoder);
                _decoder = IntPtr.Zero;
                throw new InvalidOperationException("Failed to initialize decoder");
            }

            _isInitialized = true;
        }

        public byte[] Decode(byte[] data)
        {
            if (!_isInitialized)
                throw new InvalidOperationException("Decoder not initialized");

            if (data == null || data.Length == 0)
                return null;

            GCHandle yHandle = GCHandle.Alloc(_yBuffer, GCHandleType.Pinned);
            GCHandle uHandle = GCHandle.Alloc(_uBuffer, GCHandleType.Pinned);
            GCHandle vHandle = GCHandle.Alloc(_vBuffer, GCHandleType.Pinned);

            try
            {
                OpenH264Native.SSourcePicture dstPic = new OpenH264Native.SSourcePicture(
                    _width, _height, OpenH264Native.COLOR_FORMAT_I420,
                    yHandle.AddrOfPinnedObject(),
                    uHandle.AddrOfPinnedObject(),
                    vHandle.AddrOfPinnedObject(),
                    _width, _width / 2);

                int frameStatus = 0;
                int result = OpenH264Native.WelsDecodeFrame2(_decoder, data, data.Length, ref dstPic, ref frameStatus);
                if (result != OpenH264Native.ERROR_CODE_NONE)
                    return null;

                YuvConverter.I420ToBgra32(_yBuffer, _uBuffer, _vBuffer, _width, _height, _bgraBuffer);

                return (byte[])_bgraBuffer.Clone();
            }
            finally
            {
                yHandle.Free();
                uHandle.Free();
                vHandle.Free();
            }
        }

        public void Reset()
        {
            if (_isInitialized)
            {
                Dispose();
                Initialize(_width, _height);
            }
        }

        public void Dispose()
        {
            if (_decoder != IntPtr.Zero)
            {
                OpenH264Native.WelsDestroyDecoder(_decoder);
                _decoder = IntPtr.Zero;
            }
            _isInitialized = false;
            _yBuffer = null;
            _uBuffer = null;
            _vBuffer = null;
            _bgraBuffer = null;
        }
    }
#endif
}