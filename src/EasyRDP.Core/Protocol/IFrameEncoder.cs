using System;

namespace EasyRDP.Core.Protocol
{
    public interface IFrameEncoder
    {
        CodecId Codec { get; }

        ScreenFrameMessage Encode(int width, int height, byte[] curPixels, byte[] prevPixels, bool forceKey);

        void Reset();
    }
}