namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 视频帧消息（服务端→客户端）。
    /// Payload 布局: Width(4) Height(4) IsKeyframe(1) SequenceNumber(8)
    ///               ContentWidth(4) ContentHeight(4) DataLen(4) Data(*)
    /// 定长头 29 字节 + 变长 H.264 数据。
    /// Width/Height 为编码/显示分辨率（D11 自适应降采样时会小于物理屏幕）；
    /// ContentWidth/ContentHeight 为内容坐标空间（物理屏幕，鼠标坐标映射基准），
    /// 二者在降采样后不再相等，客户端必须用 Content* 映射输入、用 Width/Height 显示。
    /// </summary>
    public class VideoFrameMessage
    {
        /// <summary>Width of the video frame in pixels (encode/display size).</summary>
        public int Width;
        /// <summary>Height of the video frame in pixels (encode/display size).</summary>
        public int Height;
        /// <summary>Whether this frame is a keyframe (IDR).</summary>
        public bool IsKeyframe;
        /// <summary>Monotonically increasing sequence number for frame ordering.</summary>
        public long SequenceNumber;
        /// <summary>内容坐标空间宽度（物理屏幕宽度），鼠标映射基准。</summary>
        public int ContentWidth;
        /// <summary>内容坐标空间高度（物理屏幕高度），鼠标映射基准。</summary>
        public int ContentHeight;
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
            bp.WriteInt32(ContentWidth);
            bp.WriteInt32(ContentHeight);
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
                ContentWidth = bp.ReadInt32(),
                ContentHeight = bp.ReadInt32(),
                Data = bp.ReadBytes()
            };
        }
    }
}
