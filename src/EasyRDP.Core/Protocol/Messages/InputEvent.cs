using System;

namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 输入事件消息 C→S
    /// </summary>
    public class InputEventMessage
    {
        /// <summary>事件类型</summary>
        public InputEventType EventType;

        /// <summary>输入单元列表</summary>
        public InputUnit[] Units;

        public InputEventMessage()
        {
            Units = new InputUnit[0];
        }

        public byte[] Encode()
        {
            // EventType(1) + Count(1) + Units
            int unitsSize = 0;
            for (int i = 0; i < Units.Length; i++)
                unitsSize += Units[i].GetSize(EventType);

            int size = 1 + 1 + unitsSize;
            byte[] buffer = new byte[size];
            int offset = 0;

            buffer[offset] = (byte)EventType;
            offset += 1;
            buffer[offset] = (byte)Units.Length;
            offset += 1;

            for (int i = 0; i < Units.Length; i++)
            {
                Units[i].WriteTo(buffer, ref offset, EventType);
            }

            return buffer;
        }

        public void Decode(byte[] payload)
        {
            int offset = 0;
            EventType = (InputEventType)BinaryPacker.ReadByte(payload, offset);
            offset += 1;
            byte count = BinaryPacker.ReadByte(payload, offset);
            offset += 1;

            Units = new InputUnit[count];
            for (int i = 0; i < count; i++)
            {
                Units[i] = InputUnit.ReadFrom(payload, ref offset, EventType);
            }
        }
    }

    /// <summary>
    /// 输入单元——每种事件类型的负载不同
    /// </summary>
    public struct InputUnit
    {
        // MouseMove
        public bool Absolute;
        public short X;
        public short Y;
        public ushort MouseFlags;

        // MouseDown / MouseUp / Wheel
        public byte Button;
        public short WheelDelta;

        // Keyboard
        public byte VirtualKey;
        public ushort KeyFlags;

        // UnicodeText
        public string Text;

        public int GetSize(InputEventType eventType)
        {
            switch (eventType)
            {
                case InputEventType.MouseMove:
                    return 7; // Absolute(1) + X(2) + Y(2) + Flags(2)
                case InputEventType.MouseDown:
                case InputEventType.MouseUp:
                    return 3; // Button(1) + Flags(2)
                case InputEventType.MouseWheel:
                    return 2; // Delta(2)
                case InputEventType.KeyDown:
                case InputEventType.KeyUp:
                    return 3; // VirtualKey(1) + Flags(2)
                case InputEventType.UnicodeText:
                    // CharLen(1) + Chars(N*2)
                    return 1 + (Text != null ? Text.Length * 2 : 0);
                default:
                    return 0;
            }
        }

        public void WriteTo(byte[] buffer, ref int offset, InputEventType eventType)
        {
            switch (eventType)
            {
                case InputEventType.MouseMove:
                    buffer[offset] = (byte)(Absolute ? 1 : 0);
                    offset += 1;
                    BinaryPacker.WriteInt16LE(buffer, offset, X);
                    offset += 2;
                    BinaryPacker.WriteInt16LE(buffer, offset, Y);
                    offset += 2;
                    BinaryPacker.WriteUInt16LE(buffer, offset, MouseFlags);
                    offset += 2;
                    break;

                case InputEventType.MouseDown:
                case InputEventType.MouseUp:
                    buffer[offset] = Button;
                    offset += 1;
                    BinaryPacker.WriteUInt16LE(buffer, offset, MouseFlags);
                    offset += 2;
                    break;

                case InputEventType.MouseWheel:
                    BinaryPacker.WriteInt16LE(buffer, offset, WheelDelta);
                    offset += 2;
                    break;

                case InputEventType.KeyDown:
                case InputEventType.KeyUp:
                    buffer[offset] = VirtualKey;
                    offset += 1;
                    BinaryPacker.WriteUInt16LE(buffer, offset, KeyFlags);
                    offset += 2;
                    break;

                case InputEventType.UnicodeText:
                    string txt = Text ?? string.Empty;
                    buffer[offset] = (byte)txt.Length;
                    offset += 1;
                    for (int i = 0; i < txt.Length; i++)
                    {
                        BinaryPacker.WriteUInt16LE(buffer, offset, (ushort)txt[i]);
                        offset += 2;
                    }
                    break;
            }
        }

        public static InputUnit ReadFrom(byte[] buffer, ref int offset, InputEventType eventType)
        {
            InputUnit unit = new InputUnit();
            switch (eventType)
            {
                case InputEventType.MouseMove:
                    unit.Absolute = BinaryPacker.ReadByte(buffer, offset) != 0;
                    offset += 1;
                    unit.X = BinaryPacker.ReadInt16LE(buffer, offset);
                    offset += 2;
                    unit.Y = BinaryPacker.ReadInt16LE(buffer, offset);
                    offset += 2;
                    unit.MouseFlags = BinaryPacker.ReadUInt16LE(buffer, offset);
                    offset += 2;
                    break;

                case InputEventType.MouseDown:
                case InputEventType.MouseUp:
                    unit.Button = BinaryPacker.ReadByte(buffer, offset);
                    offset += 1;
                    unit.MouseFlags = BinaryPacker.ReadUInt16LE(buffer, offset);
                    offset += 2;
                    break;

                case InputEventType.MouseWheel:
                    unit.WheelDelta = BinaryPacker.ReadInt16LE(buffer, offset);
                    offset += 2;
                    break;

                case InputEventType.KeyDown:
                case InputEventType.KeyUp:
                    unit.VirtualKey = BinaryPacker.ReadByte(buffer, offset);
                    offset += 1;
                    unit.KeyFlags = BinaryPacker.ReadUInt16LE(buffer, offset);
                    offset += 2;
                    break;

                case InputEventType.UnicodeText:
                    byte charLen = BinaryPacker.ReadByte(buffer, offset);
                    offset += 1;
                    char[] chars = new char[charLen];
                    for (int i = 0; i < charLen; i++)
                    {
                        chars[i] = (char)BinaryPacker.ReadUInt16LE(buffer, offset);
                        offset += 2;
                    }
                    unit.Text = new string(chars);
                    break;
            }
            return unit;
        }
    }
}
