namespace EasyRDP.Core.Protocol
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;

    /// <summary>
    /// 文件剪贴板格式广播消息（延迟渲染）。发送方检测到 CF_HDROP 后发送此消息，
    /// 仅包含文件元信息（文件名+大小），不传输文件内容。
    /// 接收方收到后启动按需下载流程（ClipFileContentsReq/Res）。
    /// </summary>
    public class ClipFormatListMessage
    {
        /// <summary>本次传输的唯一标识（用于关联后续的 FileContentsReq/Res）。</summary>
        public uint TransferId;

        /// <summary>文件元信息列表。</summary>
        public List<FileMeta> Files = new List<FileMeta>();

        /// <summary>文件元信息：文件名 + 文件大小。</summary>
        public class FileMeta
        {
            /// <summary>文件名（不含目录路径，仅文件名本身）。</summary>
            public string FileName;

            /// <summary>文件大小（字节）。</summary>
            public long FileSize;
        }

        /// <summary>序列化为 payload。</summary>
        public byte[] Pack()
        {
            using (var ms = new MemoryStream(64))
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(TransferId);
                bw.Write(Files != null ? Files.Count : 0);
                if (Files != null)
                {
                    foreach (var f in Files)
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
        public static ClipFormatListMessage Unpack(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                var msg = new ClipFormatListMessage
                {
                    TransferId = br.ReadUInt32()
                };
                int count = br.ReadInt32();
                if (count < 0 || count > 10000)
                    throw new ArgumentException("ClipFormatList file count out of bounds: " + count);
                msg.Files = new List<FileMeta>(count);
                for (int i = 0; i < count; i++)
                {
                    int nameLen = br.ReadInt32();
                    if (nameLen < 0 || nameLen > 4096)
                        throw new ArgumentException("ClipFormatList name length out of bounds: " + nameLen);
                    string name = Encoding.UTF8.GetString(br.ReadBytes(nameLen));
                    long size = br.ReadInt64();
                    msg.Files.Add(new FileMeta { FileName = name, FileSize = size });
                }
                return msg;
            }
        }
    }

    /// <summary>
    /// 文件内容请求消息（延迟渲染）。接收方按需请求发送方的文件内容分片。
    /// 接收方控制下载速率，避免灌满 TCP 连接。
    /// </summary>
    public class ClipFileContentsReqMessage
    {
        /// <summary>标志：请求指定范围的数据（position + requestedSize）。</summary>
        public const uint FlagRange = 0x2;

        /// <summary>关联的传输 ID（与 ClipFormatListMessage.TransferId 对应）。</summary>
        public uint TransferId;

        /// <summary>流 ID：每次请求唯一，用于匹配对应的 FileContentsRes。</summary>
        public uint StreamId;

        /// <summary>文件索引（在 ClipFormatListMessage.Files 中的位置）。</summary>
        public int FileIndex;

        /// <summary>请求标志（目前仅支持 FlagRange）。</summary>
        public uint Flags;

        /// <summary>文件偏移（字节）。</summary>
        public long Position;

        /// <summary>请求的字节数。</summary>
        public long RequestedSize;

        /// <summary>序列化为 payload。</summary>
        public byte[] Pack()
        {
            using (var ms = new MemoryStream(32))
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(TransferId);
                bw.Write(StreamId);
                bw.Write(FileIndex);
                bw.Write(Flags);
                bw.Write(Position);
                bw.Write(RequestedSize);
                return ms.ToArray();
            }
        }

        /// <summary>从 payload 反序列化。</summary>
        public static ClipFileContentsReqMessage Unpack(byte[] data)
        {
            if (data == null || data.Length < 32)
                throw new ArgumentException("ClipFileContentsReq payload too short");
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                return new ClipFileContentsReqMessage
                {
                    TransferId = br.ReadUInt32(),
                    StreamId = br.ReadUInt32(),
                    FileIndex = br.ReadInt32(),
                    Flags = br.ReadUInt32(),
                    Position = br.ReadInt64(),
                    RequestedSize = br.ReadInt64()
                };
            }
        }
    }

    /// <summary>
    /// 文件内容响应消息（延迟渲染）。发送方按 FileContentsReq 的 position+size 返回文件内容分片。
    /// </summary>
    public class ClipFileContentsResMessage
    {
        /// <summary>状态码：0=成功，1=失败。</summary>
        public const byte StatusOk = 0;

        /// <summary>状态码：失败（文件不存在、读取错误等）。</summary>
        public const byte StatusError = 1;

        /// <summary>关联的传输 ID（与 ClipFormatListMessage.TransferId 对应，用于路由到正确的 Consumer）。</summary>
        public uint TransferId;

        /// <summary>流 ID（与请求方的 ClipFileContentsReqMessage.StreamId 对应）。</summary>
        public uint StreamId;

        /// <summary>状态码：0=成功，1=失败。</summary>
        public byte Status;

        /// <summary>数据长度（字节）。</summary>
        public int DataLen;

        /// <summary>文件内容分片数据。</summary>
        public byte[] Data;

        /// <summary>序列化为 payload。</summary>
        public byte[] Pack()
        {
            int dataLen = Data != null ? Data.Length : 0;
            using (var ms = new MemoryStream(13 + dataLen))
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(TransferId);
                bw.Write(StreamId);
                bw.Write(Status);
                bw.Write(dataLen);
                if (dataLen > 0)
                    bw.Write(Data, 0, dataLen);
                return ms.ToArray();
            }
        }

        /// <summary>从 payload 反序列化。</summary>
        public static ClipFileContentsResMessage Unpack(byte[] data)
        {
            if (data == null || data.Length < 13)
                throw new ArgumentException("ClipFileContentsRes payload too short");
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                var msg = new ClipFileContentsResMessage
                {
                    TransferId = br.ReadUInt32(),
                    StreamId = br.ReadUInt32(),
                    Status = br.ReadByte(),
                    DataLen = br.ReadInt32()
                };
                if (msg.DataLen < 0 || msg.DataLen > Constants.MaxSafePayloadSize)
                    throw new ArgumentException("ClipFileContentsRes data length out of bounds: " + msg.DataLen);
                if (msg.DataLen > 0)
                {
                    msg.Data = br.ReadBytes(msg.DataLen);
                }
                else
                {
                    msg.Data = new byte[0];
                }
                return msg;
            }
        }
    }
}
