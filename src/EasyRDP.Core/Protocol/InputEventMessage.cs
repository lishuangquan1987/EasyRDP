namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 输入事件消息。
    /// Payload 布局: Type(1) KeyCode(4 LE) X(4 LE) Y(4 LE) WheelDelta(4 LE) = 17 字节定长
    /// </summary>
    public class InputEventMessage
    {
        public InputEventType Type;
        public int KeyCode;
        public int X;
        public int Y;
        public int WheelDelta;

        /// <summary>序列化为 payload 字节。</summary>
        public byte[] Pack()
        {
            var bp = new BinaryPacker();
            bp.WriteByte((byte)Type);
            bp.WriteInt32(KeyCode);
            bp.WriteInt32(X);
            bp.WriteInt32(Y);
            bp.WriteInt32(WheelDelta);
            return bp.GetBytes();
        }

        /// <summary>从 payload 字节反序列化。</summary>
        public static InputEventMessage Unpack(byte[] data)
        {
            var bp = BinaryPacker.From(data);
            return new InputEventMessage
            {
                Type = (InputEventType)bp.ReadByte(),
                KeyCode = bp.ReadInt32(),
                X = bp.ReadInt32(),
                Y = bp.ReadInt32(),
                WheelDelta = bp.ReadInt32()
            };
        }
    }
}
