namespace EasyRDP.Core.Protocol
{
    public interface IVideoEncoder
    {
        CodecId Codec { get; }
        bool IsAvailable { get; }
        void Initialize(int width, int height, int targetBitrate = 2000000);
        VideoFrameMessage Encode(byte[] pixels, bool forceKeyframe);
        void Reset();
        void Dispose();
    }
}