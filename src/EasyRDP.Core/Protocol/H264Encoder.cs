namespace EasyRDP.Core.Protocol
{
#if NET8_0_OR_GREATER
    using System;
    using System.Runtime.InteropServices;

    public class H264Encoder : IVideoEncoder
    {
        private IntPtr _encoder;
        private int _width;
        private int _height;
        private byte[] _yBuffer;
        private byte[] _uBuffer;
        private byte[] _vBuffer;
        private bool _isInitialized;
        private int _frameIndex;

        public CodecId Codec
        {
            get { return CodecId.H264Software; }
        }

        public bool IsAvailable
        {
            get { return OpenH264Native.IsAvailable(); }
        }

        public void Initialize(int width, int height, int targetBitrate = 2000000)
        {
            if (!IsAvailable)
                throw new NotSupportedException("OpenH264 not available");

            if (_isInitialized)
                Dispose();

            _width = width;
            _height = height;
            _frameIndex = 0;

            int ySize = width * height;
            int uvSize = (width / 2) * (height / 2);
            _yBuffer = new byte[ySize];
            _uBuffer = new byte[uvSize];
            _vBuffer = new byte[uvSize];

            int result = OpenH264Native.WelsCreateSvcEncoder(ref _encoder);
            if (result != OpenH264Native.ERROR_CODE_NONE || _encoder == IntPtr.Zero)
                throw new InvalidOperationException("Failed to create encoder");

            OpenH264Native.SEncParamBase param = new OpenH264Native.SEncParamBase();
            param.iUsageType = 1;
            param.iPicWidth = width;
            param.iPicHeight = height;
            param.iTargetBitrate = targetBitrate;
            param.iMaxBitrate = targetBitrate * 2;
            param.iFrameRateNum = 30;
            param.iFrameRateDenom = 1;
            param.iLayerNum = 1;
            param.uiIntraPeriod = 30;
            param.iProfileIdc = 66;
            param.iLevelIdc = 21;
            param.bEnableSVC = 0;
            param.bEnableFrameSkip = 1;
            param.iRefFrameNum = 3;
            param.iMultipleThreadIdc = Environment.ProcessorCount;
            param.bEnableDeblockingFilter = 1;
            param.iDeblockingFilterAlphaC0Offset = 0;
            param.iDeblockingFilterBetaOffset = 0;

            result = OpenH264Native.EncoderInitialize(_encoder, ref param);
            if (result != OpenH264Native.ERROR_CODE_NONE)
            {
                OpenH264Native.DestroyEncoder(_encoder);
                _encoder = IntPtr.Zero;
                throw new InvalidOperationException("Failed to initialize encoder");
            }

            _isInitialized = true;
        }

        public VideoFrameMessage Encode(byte[] pixels, bool forceKeyframe)
        {
            if (!_isInitialized)
                throw new InvalidOperationException("Encoder not initialized");

            YuvConverter.Bgra32ToI420(pixels, _width, _height, _yBuffer, _uBuffer, _vBuffer);

            GCHandle yHandle = GCHandle.Alloc(_yBuffer, GCHandleType.Pinned);
            GCHandle uHandle = GCHandle.Alloc(_uBuffer, GCHandleType.Pinned);
            GCHandle vHandle = GCHandle.Alloc(_vBuffer, GCHandleType.Pinned);

            try
            {
                OpenH264Native.SSourcePicture srcPic = new OpenH264Native.SSourcePicture(
                    _width, _height, OpenH264Native.COLOR_FORMAT_I420,
                    yHandle.AddrOfPinnedObject(),
                    uHandle.AddrOfPinnedObject(),
                    vHandle.AddrOfPinnedObject(),
                    _width, _width / 2);

                OpenH264Native.SFrameBSInfo bsInfo = new OpenH264Native.SFrameBSInfo();
                bsInfo.sLayerInfo = new OpenH264Native.SLayerBSInfo[OpenH264Native.WelsMaxLayerNum];

                int result = OpenH264Native.EncoderEncodeFrameNoDelay(_encoder, ref srcPic, ref bsInfo);
                if (result != OpenH264Native.ERROR_CODE_NONE)
                    return null;

                OpenH264Native.SLayerBSInfo layerInfo = bsInfo.sLayerInfo[0];
                if (layerInfo.iBsLen <= 0 || layerInfo.pBsBuf == IntPtr.Zero)
                    return null;

                byte[] encodedData = new byte[layerInfo.iBsLen];
                Marshal.Copy(layerInfo.pBsBuf, encodedData, 0, layerInfo.iBsLen);

                bool isKey = bsInfo.iFrameType == 0 || forceKeyframe;

                return new VideoFrameMessage
                {
                    FrameType = isKey ? FrameType.Full : FrameType.Delta,
                    Codec = CodecId.H264Software,
                    Width = (ushort)_width,
                    Height = (ushort)_height,
                    FrameIndex = (uint)_frameIndex++,
                    Pixels = encodedData
                };
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
            if (_encoder != IntPtr.Zero)
            {
                OpenH264Native.DestroyEncoder(_encoder);
                _encoder = IntPtr.Zero;
            }
            _isInitialized = false;
            _yBuffer = null;
            _uBuffer = null;
            _vBuffer = null;
        }
    }
#endif
}