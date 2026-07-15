using System;

namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 文件传输请求消息 (双向)
    /// </summary>
    public class FileTransferReqMessage
    {
        /// <summary>传输动作</summary>
        public FileTransferAction Action;

        /// <summary>传输任务 ID</summary>
        public uint TransferId;

        /// <summary>文件名 (UTF-8)</summary>
        public string FileName;

        /// <summary>文件总大小（字节）</summary>
        public ulong FileSize;

        /// <summary>分块大小</summary>
        public ushort BlockSize;

        public FileTransferReqMessage()
        {
            FileName = string.Empty;
            BlockSize = ProtocolConstants.DefaultBlockSize;
        }

        public byte[] Encode()
        {
            int fileNameSize = BinaryPacker.MeasureStringUTF8(FileName);
            // Action(1) + TransferId(4) + FileNameLen(2) + FileName + FileSize(8) + BlockSize(2)
            int size = 1 + 4 + fileNameSize + 8 + 2;
            byte[] buffer = new byte[size];
            int offset = 0;

            buffer[offset] = (byte)Action;
            offset += 1;
            BinaryPacker.WriteUInt32LE(buffer, offset, TransferId);
            offset += 4;
            BinaryPacker.WriteStringUTF8(buffer, offset, FileName);
            offset += fileNameSize;
            BinaryPacker.WriteUInt64LE(buffer, offset, FileSize);
            offset += 8;
            BinaryPacker.WriteUInt16LE(buffer, offset, BlockSize);

            return buffer;
        }

        public void Decode(byte[] payload)
        {
            int offset = 0;
            Action = (FileTransferAction)BinaryPacker.ReadByte(payload, offset);
            offset += 1;
            TransferId = BinaryPacker.ReadUInt32LE(payload, offset);
            offset += 4;

            int bytesRead;
            FileName = BinaryPacker.ReadStringUTF8(payload, offset, out bytesRead);
            offset += bytesRead;

            FileSize = BinaryPacker.ReadUInt64LE(payload, offset);
            offset += 8;
            BlockSize = BinaryPacker.ReadUInt16LE(payload, offset);
        }
    }
}
