using System;
using System.IO;
using System.Text;

namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 剪贴板同步消息。双向：客户端→服务端（客户端复制→服务端粘贴），
    /// 或服务端→客户端（服务端复制→客户端粘贴）。
    /// Payload 布局: Format(1) Reserved(3) DataLen(4 LE) Data(*)
    /// 当前仅支持 Format=1 (UTF-8 文本)。Reserved 保留用于未来扩展（文件、HTML 等）。
    /// </summary>
    public class ClipboardSyncMessage
    {
        /// <summary>剪贴板数据格式。1=UTF-8 文本。</summary>
        public const byte FormatText = 1;

        /// <summary>最大剪贴板数据长度（4MB），防止恶意数据触发 OOM。</summary>
        private const int MaxDataLen = 4 * 1024 * 1024;

        /// <summary>数据格式标识。</summary>
        public byte Format;
        /// <summary>剪贴板数据（Format=1 时为 UTF-8 字节）。</summary>
        public byte[] Data;

        /// <summary>便捷属性：当 Format=Text 时获取/设置文本。</summary>
        public string Text
        {
            get { return Data != null ? Encoding.UTF8.GetString(Data) : string.Empty; }
            set { Data = value != null ? Encoding.UTF8.GetBytes(value) : new byte[0]; }
        }

        /// <summary>序列化为 payload 字节。</summary>
        public byte[] Pack()
        {
            int dataLen = Data != null ? Data.Length : 0;
            using (var ms = new MemoryStream(8 + dataLen))
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(Format);
                // 3 字节保留字段（用于未来扩展，全 0）
                bw.Write((byte)0);
                bw.Write((byte)0);
                bw.Write((byte)0);
                bw.Write(dataLen);
                if (dataLen > 0)
                    bw.Write(Data, 0, dataLen);
                return ms.ToArray();
            }
        }

        /// <summary>从 payload 字节反序列化。</summary>
        public static ClipboardSyncMessage Unpack(byte[] payload)
        {
            if (payload == null || payload.Length < 8)
                throw new ArgumentException("ClipboardSync payload too short");
            using (var ms = new MemoryStream(payload))
            using (var br = new BinaryReader(ms))
            {
                var msg = new ClipboardSyncMessage
                {
                    Format = br.ReadByte()
                };
                br.ReadByte(); // reserved
                br.ReadByte();
                br.ReadByte();
                int dataLen = br.ReadInt32();
                if (dataLen < 0 || dataLen > MaxDataLen)
                    throw new ArgumentException("ClipboardSync data length out of bounds: " + dataLen);
                msg.Data = new byte[dataLen];
                if (dataLen > 0)
                {
                    int read = br.Read(msg.Data, 0, dataLen);
                    if (read != dataLen)
                        throw new ArgumentException("ClipboardSync data truncated: expected " + dataLen + " got " + read);
                }
                return msg;
            }
        }
    }
}
