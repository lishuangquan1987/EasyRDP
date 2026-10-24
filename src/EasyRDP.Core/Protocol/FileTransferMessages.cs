namespace EasyRDP.Core.Protocol
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;

    /// <summary>
    /// 文件传输协议常量与流控参数。分块 + 滑动窗口 + 累积确认：
    /// 发送方最多领先接收方 WindowBytes，接收方每收一块回一个 Ack，
    /// 既限制内存占用又让发送方能按网络状况自适应调速。
    /// </summary>
    public static class FileTransferProtocol
    {
        /// <summary>数据块大小（字节）。256KB 在弱机上也只需一次内存拷贝。</summary>
        public const int ChunkSize = 256 * 1024;

        /// <summary>滑动窗口上限（字节）：未确认数据不得超过此值。</summary>
        public const int WindowBytes = 2 * 1024 * 1024;

        /// <summary>单次传输的最大文件数。</summary>
        public const int MaxFiles = 4096;

        /// <summary>文件名最大长度（字符）。</summary>
        public const int MaxFileNameLength = 260;

        /// <summary>文件结束结果：成功。</summary>
        public const byte ResultOk = 0;

        /// <summary>文件结束结果：失败（发送方读盘失败等）。</summary>
        public const byte ResultError = 1;

        /// <summary>取消原因：用户取消。</summary>
        public const byte CancelUser = 0;

        /// <summary>取消原因：错误（读盘/写盘/校验失败）。</summary>
        public const byte CancelError = 1;

        /// <summary>取消原因：协议错误（偏移不匹配/未知传输）。</summary>
        public const byte CancelProtocol = 2;

        /// <summary>取消原因：超时（长时间无进展）。</summary>
        public const byte CancelTimeout = 3;
    }

    /// <summary>
    /// 文件传输清单（发起方 → 接收方）。复制/拖拽时只发本消息，不含文件内容。
    /// 接收方同意后回 FileTransferStart 逐文件请求数据。
    /// </summary>
    public class FileTransferOfferMessage
    {
        /// <summary>传输标识（双向唯一，用于关联后续消息）。</summary>
        public uint TransferId;

        /// <summary>文件元信息列表（发送顺序即文件索引）。</summary>
        public List<FileMeta> Files = new List<FileMeta>();

        /// <summary>单个文件的元信息。</summary>
        public class FileMeta
        {
            /// <summary>文件名（不含目录路径）。</summary>
            public string FileName;

            /// <summary>文件长度（字节）。</summary>
            public long FileSize;
        }

        /// <summary>序列化为 payload。</summary>
        public byte[] Pack()
        {
            using (var ms = new MemoryStream(64))
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(TransferId);
                int count = Files != null ? Files.Count : 0;
                bw.Write(count);
                if (Files != null)
                {
                    foreach (FileMeta f in Files)
                    {
                        byte[] nameBytes = Encoding.UTF8.GetBytes(f.FileName ?? "");
                        bw.Write(nameBytes.Length);
                        bw.Write(nameBytes);
                        bw.Write(f.FileSize);
                    }
                }
                return ms.ToArray();
            }
        }

        /// <summary>从 payload 反序列化。</summary>
        public static FileTransferOfferMessage Unpack(byte[] data)
        {
            if (data == null || data.Length < 8)
                throw new ArgumentException("FileTransferOffer payload too short");
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                var msg = new FileTransferOfferMessage { TransferId = br.ReadUInt32() };
                int count = br.ReadInt32();
                if (count < 0 || count > FileTransferProtocol.MaxFiles)
                    throw new ArgumentException("FileTransferOffer file count out of bounds: " + count);
                msg.Files = new List<FileMeta>(count);
                for (int i = 0; i < count; i++)
                {
                    int nameLen = br.ReadInt32();
                    if (nameLen < 0 || nameLen > 4096)
                        throw new ArgumentException("FileTransferOffer name length out of bounds: " + nameLen);
                    string name = Encoding.UTF8.GetString(br.ReadBytes(nameLen));
                    long size = br.ReadInt64();
                    msg.Files.Add(new FileMeta { FileName = name, FileSize = size });
                }
                return msg;
            }
        }
    }

    /// <summary>开始传输指定文件（接收方 → 发送方）：同意清单并请求该文件的数据。</summary>
    public class FileTransferStartMessage
    {
        /// <summary>传输标识。</summary>
        public uint TransferId;

        /// <summary>文件索引（从 0 开始）。</summary>
        public ushort FileIndex;

        /// <summary>序列化为 payload。</summary>
        public byte[] Pack()
        {
            using (var ms = new MemoryStream(6))
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(TransferId);
                bw.Write(FileIndex);
                return ms.ToArray();
            }
        }

        /// <summary>从 payload 反序列化。</summary>
        public static FileTransferStartMessage Unpack(byte[] data)
        {
            if (data == null || data.Length < 6)
                throw new ArgumentException("FileTransferStart payload too short");
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                return new FileTransferStartMessage
                {
                    TransferId = br.ReadUInt32(),
                    FileIndex = br.ReadUInt16()
                };
            }
        }
    }

    /// <summary>文件数据块（发送方 → 接收方）。Offset 为该块在文件内的起始偏移。</summary>
    public class FileTransferDataMessage
    {
        /// <summary>传输标识。</summary>
        public uint TransferId;

        /// <summary>文件索引。</summary>
        public ushort FileIndex;

        /// <summary>块在文件内的偏移（字节）。</summary>
        public long Offset;

        /// <summary>块数据（长度 ≤ ChunkSize）。</summary>
        public byte[] Data;

        /// <summary>序列化为 payload。</summary>
        public byte[] Pack()
        {
            int len = Data != null ? Data.Length : 0;
            using (var ms = new MemoryStream(14 + len))
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(TransferId);
                bw.Write(FileIndex);
                bw.Write(Offset);
                bw.Write(len);
                if (len > 0)
                    bw.Write(Data, 0, len);
                return ms.ToArray();
            }
        }

        /// <summary>从 payload 反序列化。</summary>
        public static FileTransferDataMessage Unpack(byte[] data)
        {
            if (data == null || data.Length < 14)
                throw new ArgumentException("FileTransferData payload too short");
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                var msg = new FileTransferDataMessage
                {
                    TransferId = br.ReadUInt32(),
                    FileIndex = br.ReadUInt16(),
                    Offset = br.ReadInt64()
                };
                int len = br.ReadInt32();
                if (len < 0 || len > FileTransferProtocol.ChunkSize)
                    throw new ArgumentException("FileTransferData chunk length out of bounds: " + len);
                msg.Data = len > 0 ? br.ReadBytes(len) : new byte[0];
                return msg;
            }
        }
    }

    /// <summary>文件结束（发送方 → 接收方）：该文件数据已全部发出。</summary>
    public class FileTransferEndMessage
    {
        /// <summary>传输标识。</summary>
        public uint TransferId;

        /// <summary>文件索引。</summary>
        public ushort FileIndex;

        /// <summary>结果：0=成功，1=失败（发送方读盘异常）。</summary>
        public byte Result;

        /// <summary>序列化为 payload。</summary>
        public byte[] Pack()
        {
            using (var ms = new MemoryStream(7))
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(TransferId);
                bw.Write(FileIndex);
                bw.Write(Result);
                return ms.ToArray();
            }
        }

        /// <summary>从 payload 反序列化。</summary>
        public static FileTransferEndMessage Unpack(byte[] data)
        {
            if (data == null || data.Length < 7)
                throw new ArgumentException("FileTransferEnd payload too short");
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                return new FileTransferEndMessage
                {
                    TransferId = br.ReadUInt32(),
                    FileIndex = br.ReadUInt16(),
                    Result = br.ReadByte()
                };
            }
        }
    }

    /// <summary>取消传输（双向）。收到后双方清理未完成文件。</summary>
    public class FileTransferCancelMessage
    {
        /// <summary>传输标识。</summary>
        public uint TransferId;

        /// <summary>取消原因（见 FileTransferProtocol.Cancel*）。</summary>
        public byte Reason;

        /// <summary>序列化为 payload。</summary>
        public byte[] Pack()
        {
            using (var ms = new MemoryStream(5))
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(TransferId);
                bw.Write(Reason);
                return ms.ToArray();
            }
        }

        /// <summary>从 payload 反序列化。</summary>
        public static FileTransferCancelMessage Unpack(byte[] data)
        {
            if (data == null || data.Length < 5)
                throw new ArgumentException("FileTransferCancel payload too short");
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                return new FileTransferCancelMessage
                {
                    TransferId = br.ReadUInt32(),
                    Reason = br.ReadByte()
                };
            }
        }
    }

    /// <summary>数据确认（接收方 → 发送方）：NextOffset 之前的数据已落盘。</summary>
    public class FileTransferAckMessage
    {
        /// <summary>传输标识。</summary>
        public uint TransferId;

        /// <summary>文件索引。</summary>
        public ushort FileIndex;

        /// <summary>已确认的下一字节偏移（累积确认）。</summary>
        public long NextOffset;

        /// <summary>序列化为 payload。</summary>
        public byte[] Pack()
        {
            using (var ms = new MemoryStream(14))
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(TransferId);
                bw.Write(FileIndex);
                bw.Write(NextOffset);
                return ms.ToArray();
            }
        }

        /// <summary>从 payload 反序列化。</summary>
        public static FileTransferAckMessage Unpack(byte[] data)
        {
            if (data == null || data.Length < 14)
                throw new ArgumentException("FileTransferAck payload too short");
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                return new FileTransferAckMessage
                {
                    TransferId = br.ReadUInt32(),
                    FileIndex = br.ReadUInt16(),
                    NextOffset = br.ReadInt64()
                };
            }
        }
    }
}
