using System;

namespace EasyRDP.Core.Protocol
{
    public static class EncoderFactory
    {
        public static IFrameEncoder CreateFrame(CodecId codec)
        {
            switch (codec)
            {
                case CodecId.Bitmap:
                    return new BitmapEncoder();
                default:
                    throw new NotSupportedException("Codec not supported: " + codec);
            }
        }

        public static IVideoEncoder CreateVideo(CodecId codec)
        {
            switch (codec)
            {
#if NET8_0_OR_GREATER
                case CodecId.H264Software:
                    H264Encoder h264 = new H264Encoder();
                    return h264.IsAvailable ? h264 : null;
#endif
                default:
                    return null;
            }
        }

        public static CodecId GetAvailableCodec(CodecId preferred)
        {
            switch (preferred)
            {
#if NET8_0_OR_GREATER
                case CodecId.H264Software:
                    H264Encoder h264 = new H264Encoder();
                    if (h264.IsAvailable)
                        return CodecId.H264Software;
                    break;
#endif
            }
            return CodecId.Bitmap;
        }
    }
}