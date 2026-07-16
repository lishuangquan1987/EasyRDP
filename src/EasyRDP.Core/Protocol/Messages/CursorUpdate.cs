using System;

namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 光标更新消息 S→C
    /// </summary>
    public class CursorUpdateMessage
    {
        /// <summary>光标是否可见</summary>
        public bool Visible;

        /// <summary>屏幕 X 坐标</summary>
        public short X;

        /// <summary>屏幕 Y 坐标</summary>
        public short Y;

        /// <summary>热区 X</summary>
        public ushort HotspotX;

        /// <summary>热区 Y</summary>
        public ushort HotspotY;

        /// <summary>光标图像宽度</summary>
        public ushort Width;

        /// <summary>光标图像高度</summary>
        public ushort Height;

        /// <summary>AND 掩码 + XOR 掩码（标准 Windows 光标位图格式）</summary>
        public byte[] ImageData;

        public CursorUpdateMessage()
        {
            ImageData = new byte[0];
        }

        public byte[] Encode()
        {
            // Visible(1) + X(2) + Y(2) + HotspotX(2) + HotspotY(2) + Width(2) + Height(2) + ImageData
            int size = 1 + 2 + 2 + 2 + 2 + 2 + 2 + ImageData.Length;
            byte[] buffer = new byte[size];
            int offset = 0;

            buffer[offset] = (byte)(Visible ? 1 : 0);
            offset += 1;
            BinaryPacker.WriteInt16LE(buffer, offset, X);
            offset += 2;
            BinaryPacker.WriteInt16LE(buffer, offset, Y);
            offset += 2;
            BinaryPacker.WriteUInt16LE(buffer, offset, HotspotX);
            offset += 2;
            BinaryPacker.WriteUInt16LE(buffer, offset, HotspotY);
            offset += 2;
            BinaryPacker.WriteUInt16LE(buffer, offset, Width);
            offset += 2;
            BinaryPacker.WriteUInt16LE(buffer, offset, Height);
            offset += 2;

            if (ImageData.Length > 0)
            {
                Buffer.BlockCopy(ImageData, 0, buffer, offset, ImageData.Length);
            }

            return buffer;
        }

        public void Decode(byte[] payload)
        {
            int offset = 0;
            Visible = BinaryPacker.ReadByte(payload, offset) != 0;
            offset += 1;
            X = BinaryPacker.ReadInt16LE(payload, offset);
            offset += 2;
            Y = BinaryPacker.ReadInt16LE(payload, offset);
            offset += 2;
            HotspotX = BinaryPacker.ReadUInt16LE(payload, offset);
            offset += 2;
            HotspotY = BinaryPacker.ReadUInt16LE(payload, offset);
            offset += 2;
            Width = BinaryPacker.ReadUInt16LE(payload, offset);
            offset += 2;
            Height = BinaryPacker.ReadUInt16LE(payload, offset);
            offset += 2;

            int remaining = payload.Length - offset;
            ImageData = new byte[remaining];
            if (remaining > 0)
            {
                Buffer.BlockCopy(payload, offset, ImageData, 0, remaining);
            }
        }
    }
}
