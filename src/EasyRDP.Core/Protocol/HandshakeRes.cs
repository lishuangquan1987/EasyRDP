namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 服务端握手响应。
    /// Payload 布局: Result(1) Codec(1) ScreenWidth(4 LE) ScreenHeight(4 LE)
    ///               ContentWidth(4 LE) ContentHeight(4 LE) = 18 字节定长
    /// ScreenWidth/Height 为编码/显示分辨率（可能与物理屏幕不同，如 D11 降档起始）；
    /// ContentWidth/Height 为内容坐标空间（物理屏幕），客户端鼠标映射基准。
    /// </summary>
    public class HandshakeRes
    {
        /// <summary>Handshake result indicating success or failure.</summary>
        public HandshakeResult Result;
        /// <summary>The negotiated codec identifier.</summary>
        public CodecId Codec;
        /// <summary>Screen width in pixels.</summary>
        public int ScreenWidth;
        /// <summary>Screen height in pixels.</summary>
        public int ScreenHeight;
        /// <summary>内容坐标空间宽度（物理屏幕宽度），鼠标映射基准。</summary>
        public int ContentWidth;
        /// <summary>内容坐标空间高度（物理屏幕高度），鼠标映射基准。</summary>
        public int ContentHeight;

        /// <summary>序列化为 payload 字节。</summary>
        public byte[] Pack()
        {
            var bp = new BinaryPacker();
            bp.WriteByte((byte)Result);
            bp.WriteByte((byte)Codec);
            bp.WriteInt32(ScreenWidth);
            bp.WriteInt32(ScreenHeight);
            bp.WriteInt32(ContentWidth);
            bp.WriteInt32(ContentHeight);
            return bp.GetBytes();
        }

        /// <summary>从 payload 字节反序列化。</summary>
        public static HandshakeRes Unpack(byte[] data)
        {
            var bp = BinaryPacker.From(data);
            return new HandshakeRes
            {
                Result = (HandshakeResult)bp.ReadByte(),
                Codec = (CodecId)bp.ReadByte(),
                ScreenWidth = bp.ReadInt32(),
                ScreenHeight = bp.ReadInt32(),
                ContentWidth = bp.ReadInt32(),
                ContentHeight = bp.ReadInt32()
            };
        }
    }
}
