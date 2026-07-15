using System;

namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 文件数据块消息 (双向)
    /// </summary>
    public class FileTransferDataMessage
    {
        /// <summary>传输任务 ID</summary>
        public uint TransferId;

        /// <summary>当前块序号（从 0 开始）</summary>
        public uint BlockIndex;

        /// <summary>本块数据</summary>
        public byte[] Data;

        public FileTransferDataMessage()
        {
            Data = new byte[0];
        }

        public byte[] Encode()
        {
            // TransferId(4) + BlockIndex(4) + DataLen(2) + Data
            int size = 4 + 4 + 2 + Data.Length;
            byte[] buffer = new byte[size];
            int offset = 0;

            BinaryPacker.WriteUInt32LE(buffer, offset, TransferId);
            offset += 4;
            BinaryPacker.WriteUInt32LE(buffer, offset, BlockIndex);
            offset += 4;
            BinaryPacker.WriteUInt16LE(buffer, offset, (ushort)Data.Length);
            offset += 2;

            if (Data.Length > 0)
            {
                Buffer.BlockCopy(Data, 0, buffer, offset, Data.Length);
            }

            return buffer;
        }

        public void Decode(byte[] payload)
        {
            int offset = 0;
            TransferId = BinaryPacker.ReadUInt32LE(payload, offset);
            offset += 4;
            BlockIndex = BinaryPacker.ReadUInt32LE(payload, offset);
            offset += 4;
            ushort dataLen = BinaryPacker.ReadUInt16LE(payload, offset);
            offset += 2;

            Data = new byte[dataLen];
            if (dataLen > 0)
            {
                Buffer.BlockCopy(payload, offset, Data, 0, dataLen);
            }
        }
    }
}
