namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 视频帧消息（服务端→客户端）。
    /// Payload 布局: Width(4) Height(4) IsKeyframe(1) SequenceNumber(8) DataLen(4) Data(*)
    /// 定长头 21 字节 + 变长 H.264 数据
    /// </summary>
    public class VideoFrameMessage
    {
        /// <summary>Width of the video frame in pixels.</summary>
        public int Width;
        /// <summary>Height of the video frame in pixels.</summary>
        public int Height;
        /// <summary>Whether this frame is a keyframe (IDR).</summary>
        public bool IsKeyframe;
        /// <summary>Monotonically increasing sequence number for frame ordering.</summary>
        public long SequenceNumber;
        /// <summary>Encoded H.264 video data.</summary>
        public byte[] Data;

        /// <summary>序列化为 payload 字节。</summary>
        public byte[] Pack()
        {
            var bp = new BinaryPacker();
            bp.WriteInt32(Width);
            bp.WriteInt32(Height);
            bp.WriteByte((byte)(IsKeyframe ? 1 : 0));
            bp.WriteInt64(SequenceNumber);
            bp.WriteBytes(Data);
            return bp.GetBytes();
        }

        /// <summary>从 payload 字节反序列化。</summary>
        public static VideoFrameMessage Unpack(byte[] data)
        {
            var bp = BinaryPacker.From(data);
            return new VideoFrameMessage
            {
                Width = bp.ReadInt32(),
                Height = bp.ReadInt32(),
                IsKeyframe = bp.ReadByte() != 0,
                SequenceNumber = bp.ReadInt64(),
                Data = bp.ReadBytes()
            };
        }
    }
}
