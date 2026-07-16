using System;

namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 屏幕帧中的矩形区域
    /// </summary>
    public class ScreenRect
    {
        public ushort X;
        public ushort Y;
        public ushort Width;
        public ushort Height;
        public uint Offset; // 该矩形像素数据在 Pixels 区中的偏移

        public const int Size = 12;

        public void WriteTo(byte[] buffer, int offset)
        {
            BinaryPacker.WriteUInt16LE(buffer, offset, X);
            BinaryPacker.WriteUInt16LE(buffer, offset + 2, Y);
            BinaryPacker.WriteUInt16LE(buffer, offset + 4, Width);
            BinaryPacker.WriteUInt16LE(buffer, offset + 6, Height);
            BinaryPacker.WriteUInt32LE(buffer, offset + 8, Offset);
        }

        public static ScreenRect ReadFrom(byte[] buffer, int offset)
        {
            ScreenRect rect = new ScreenRect();
            rect.X = BinaryPacker.ReadUInt16LE(buffer, offset);
            rect.Y = BinaryPacker.ReadUInt16LE(buffer, offset + 2);
            rect.Width = BinaryPacker.ReadUInt16LE(buffer, offset + 4);
            rect.Height = BinaryPacker.ReadUInt16LE(buffer, offset + 6);
            rect.Offset = BinaryPacker.ReadUInt32LE(buffer, offset + 8);
            return rect;
        }
    }

    /// <summary>
    /// 屏幕帧消息 S→C
    /// </summary>
    public class ScreenFrameMessage
    {
        /// <summary>帧类型：全帧或增量帧</summary>
        public FrameType FrameType;

        /// <summary>压缩方式</summary>
        public CompressType Compress;

        /// <summary>矩形区域列表</summary>
        public ScreenRect[] Rects;

        /// <summary>像素数据（BGRA32，可选压缩）</summary>
        public byte[] Pixels;

        public ScreenFrameMessage()
        {
            Rects = new ScreenRect[0];
            Pixels = new byte[0];
        }

        public byte[] Encode()
        {
            // FrameType(1) + Compress(1) + RectCount(2) + DataLen(4) + Rects + Pixels
            int rectsSize = Rects.Length * ScreenRect.Size;
            int size = 1 + 1 + 2 + 4 + rectsSize + Pixels.Length;
            byte[] buffer = new byte[size];
            int offset = 0;

            buffer[offset] = (byte)FrameType;
            offset += 1;
            buffer[offset] = (byte)Compress;
            offset += 1;
            BinaryPacker.WriteUInt16LE(buffer, offset, (ushort)Rects.Length);
            offset += 2;
            BinaryPacker.WriteUInt32LE(buffer, offset, (uint)Pixels.Length);
            offset += 4;

            for (int i = 0; i < Rects.Length; i++)
            {
                Rects[i].WriteTo(buffer, offset);
                offset += ScreenRect.Size;
            }

            if (Pixels.Length > 0)
            {
                Buffer.BlockCopy(Pixels, 0, buffer, offset, Pixels.Length);
            }

            return buffer;
        }

        public void Decode(byte[] payload)
        {
            int offset = 0;
            FrameType = (FrameType)BinaryPacker.ReadByte(payload, offset);
            offset += 1;
            Compress = (CompressType)BinaryPacker.ReadByte(payload, offset);
            offset += 1;
            ushort rectCount = BinaryPacker.ReadUInt16LE(payload, offset);
            offset += 2;
            uint dataLen = BinaryPacker.ReadUInt32LE(payload, offset);
            offset += 4;

            Rects = new ScreenRect[rectCount];
            for (int i = 0; i < rectCount; i++)
            {
                Rects[i] = ScreenRect.ReadFrom(payload, offset);
                offset += ScreenRect.Size;
            }

            Pixels = new byte[dataLen];
            if (dataLen > 0)
            {
                Buffer.BlockCopy(payload, offset, Pixels, 0, (int)dataLen);
            }
        }
    }
}
