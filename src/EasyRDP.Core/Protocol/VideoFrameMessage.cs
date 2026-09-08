namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 视频帧消息（服务端→客户端）。
    /// Payload 布局(v2): Width(4) Height(4) IsKeyframe(1) SequenceNumber(8)
    ///               ContentWidth(4) ContentHeight(4) Codec(1) DataLen(4) Data(*)
    /// 定长头 30 字节 + 变长编码数据。
    /// Width/Height 为编码/显示分辨率（D11 自适应降采样时会小于物理屏幕）；
    /// ContentWidth/ContentHeight 为内容坐标空间（物理屏幕，鼠标坐标映射基准），
    /// 二者在降采样后不再相等，客户端必须用 Content* 映射输入、用 Width/Height 显示。
    /// Codec 为本帧实际编码器（D14 混合编码：会话内 ZRLE 与 H264 按内容动态切换，
    /// 客户端按帧头选择解码器，不再绑定握手协商的单一 codec）。
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
        /// <summary>本帧实际编码器（D14 混合编码；缺省 Zrle 兼容旧路径）。</summary>
        public CodecId Codec = CodecId.Zrle;
        /// <summary>Encoded video data.</summary>
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
            bp.WriteByte((byte)Codec);
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
                // D14 v2 新增：Codec(1) 字节位于 ContentHeight 与 DataLen 之间。
                // 旧服务端 payload 无此字段，两端同步部署时不存在混用；
                // payload 异常短（旧格式/损坏数据）时保守按 Zrle 处理。
                Codec = ReadCodecSafe(bp, data != null ? data.Length : 0),
                Data = bp.ReadBytes()
            };
        }

        /// <summary>
        /// 安全读取 Codec 字段：新格式定长头 30 字节（到 ContentHeight 为 25 字节，
        /// 其后还有 Codec(1)+DataLen(4)），payload 不足 30 字节视为旧格式/损坏数据，
        /// 保守回退 Zrle，避免把 DataLen 首字节误读为 codec 造成解码错乱。
        /// </summary>
        private static CodecId ReadCodecSafe(BinaryPacker bp, int payloadLen)
        {
            if (payloadLen < 30)
                return CodecId.Zrle;
            return (CodecId)bp.ReadByte();
        }
    }
}
