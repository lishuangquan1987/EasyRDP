namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 服务端握手响应。
    /// Payload 布局: Result(1) Codec(1) ScreenWidth(4 LE) ScreenHeight(4 LE) = 10 字节定长
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

        /// <summary>序列化为 payload 字节。</summary>
        public byte[] Pack()
        {
            var bp = new BinaryPacker();
            bp.WriteByte((byte)Result);
            bp.WriteByte((byte)Codec);
            bp.WriteInt32(ScreenWidth);
            bp.WriteInt32(ScreenHeight);
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
                ScreenHeight = bp.ReadInt32()
            };
        }
    }
}
