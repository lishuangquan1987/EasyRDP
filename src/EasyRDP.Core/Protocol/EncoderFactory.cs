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
    }
}