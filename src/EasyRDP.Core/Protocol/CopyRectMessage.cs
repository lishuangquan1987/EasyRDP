using System;

namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 屏幕区域复制指令 S→C。
    /// 告诉客户端将指定区域从源位置复制到目标位置，无需传输像素数据。
    /// </summary>
    public class CopyRectMessage
    {
        /// <summary>复制操作列表</summary>
        public CopyRectEntry[] Entries;

        public CopyRectMessage()
        {
            Entries = new CopyRectEntry[0];
        }

        public byte[] Encode()
        {
            int size = 2; // Count(2)
            for (int i = 0; i < Entries.Length; i++)
                size += CopyRectEntry.Size;

            byte[] buffer = new byte[size];
            int offset = 0;
            BinaryPacker.WriteUInt16LE(buffer, offset, (ushort)Entries.Length);
            offset += 2;

            for (int i = 0; i < Entries.Length; i++)
            {
                Entries[i].WriteTo(buffer, ref offset);
            }

            return buffer;
        }

        public void Decode(byte[] payload)
        {
            int offset = 0;
            ushort count = BinaryPacker.ReadUInt16LE(payload, offset);
            offset += 2;

            Entries = new CopyRectEntry[count];
            for (int i = 0; i < count; i++)
            {
                Entries[i] = CopyRectEntry.ReadFrom(payload, ref offset);
            }
        }
    }

    /// <summary>
    /// 单条复制操作：将 (SrcX,SrcY) 处 W×H 像素复制到 (DstX,DstY)。
    /// </summary>
    public struct CopyRectEntry
    {
        public const int Size = 12;

        public ushort SrcX;
        public ushort SrcY;
        public ushort DstX;
        public ushort DstY;
        public ushort Width;
        public ushort Height;

        public void WriteTo(byte[] buffer, ref int offset)
        {
            BinaryPacker.WriteUInt16LE(buffer, offset, SrcX);
            offset += 2;
            BinaryPacker.WriteUInt16LE(buffer, offset, SrcY);
            offset += 2;
            BinaryPacker.WriteUInt16LE(buffer, offset, DstX);
            offset += 2;
            BinaryPacker.WriteUInt16LE(buffer, offset, DstY);
            offset += 2;
            BinaryPacker.WriteUInt16LE(buffer, offset, Width);
            offset += 2;
            BinaryPacker.WriteUInt16LE(buffer, offset, Height);
            offset += 2;
        }

        public static CopyRectEntry ReadFrom(byte[] buffer, ref int offset)
        {
            CopyRectEntry entry;
            entry.SrcX = BinaryPacker.ReadUInt16LE(buffer, offset);
            offset += 2;
            entry.SrcY = BinaryPacker.ReadUInt16LE(buffer, offset);
            offset += 2;
            entry.DstX = BinaryPacker.ReadUInt16LE(buffer, offset);
            offset += 2;
            entry.DstY = BinaryPacker.ReadUInt16LE(buffer, offset);
            offset += 2;
            entry.Width = BinaryPacker.ReadUInt16LE(buffer, offset);
            offset += 2;
            entry.Height = BinaryPacker.ReadUInt16LE(buffer, offset);
            offset += 2;
            return entry;
        }
    }
}
