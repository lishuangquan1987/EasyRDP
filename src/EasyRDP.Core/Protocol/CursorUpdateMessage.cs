namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 光标更新消息。
    /// Payload 布局: Visible(1) X(4) Y(4) Width(4) Height(4) HotX(4) HotY(4) RgbaLen(4) RgbaPixels(*)
    /// 定长头 29 字节 + 变长像素数据
    /// </summary>
    public class CursorUpdateMessage
    {
        /// <summary>Whether the cursor is visible.</summary>
        public bool Visible;
        /// <summary>X-coordinate of the cursor hotspot.</summary>
        public int X;
        /// <summary>Y-coordinate of the cursor hotspot.</summary>
        public int Y;
        /// <summary>Width of the cursor bitmap in pixels.</summary>
        public int Width;
        /// <summary>Height of the cursor bitmap in pixels.</summary>
        public int Height;
        /// <summary>Hotspot X-offset within the cursor bitmap.</summary>
        public int HotX;
        /// <summary>Hotspot Y-offset within the cursor bitmap.</summary>
        public int HotY;
        /// <summary>RGBA pixel data of the cursor bitmap.</summary>
        public byte[] RgbaPixels;

        /// <summary>序列化为 payload 字节。</summary>
        public byte[] Pack()
        {
            var bp = new BinaryPacker();
            bp.WriteByte((byte)(Visible ? 1 : 0));
            bp.WriteInt32(X);
            bp.WriteInt32(Y);
            bp.WriteInt32(Width);
            bp.WriteInt32(Height);
            bp.WriteInt32(HotX);
            bp.WriteInt32(HotY);
            bp.WriteBytes(RgbaPixels);
            return bp.GetBytes();
        }

        /// <summary>从 payload 字节反序列化。</summary>
        public static CursorUpdateMessage Unpack(byte[] data)
        {
            var bp = BinaryPacker.From(data);
            return new CursorUpdateMessage
            {
                Visible = bp.ReadByte() != 0,
                X = bp.ReadInt32(),
                Y = bp.ReadInt32(),
                Width = bp.ReadInt32(),
                Height = bp.ReadInt32(),
                HotX = bp.ReadInt32(),
                HotY = bp.ReadInt32(),
                RgbaPixels = bp.ReadBytes()
            };
        }
    }
}
