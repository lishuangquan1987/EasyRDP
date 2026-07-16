using System;

namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 消息头（14 字节）。
    /// 
    /// 布局：Magic(4B, LE) + Version(1B) + Type(1B) + Sequence(4B, LE) + Length(4B, LE)
    /// </summary>
    public struct MessageHeader
    {
        /// <summary>魔数，必须等于 ProtocolConstants.Magic</summary>
        public uint Magic;

        /// <summary>协议版本</summary>
        public byte Version;

        /// <summary>消息类型</summary>
        public MessageType Type;

        /// <summary>消息序号</summary>
        public uint Sequence;

        /// <summary>负载字节数</summary>
        public uint Length;

        /// <summary>头总字节数</summary>
        public const int Size = 14;

        /// <summary>
        /// 将消息头序列化为 14 字节数组。
        /// </summary>
        public byte[] ToBytes()
        {
            byte[] buffer = new byte[Size];
            BinaryPacker.WriteUInt32LE(buffer, 0, Magic);
            buffer[4] = Version;
            buffer[5] = (byte)Type;
            BinaryPacker.WriteUInt32LE(buffer, 6, Sequence);
            BinaryPacker.WriteUInt32LE(buffer, 10, Length);
            return buffer;
        }

        /// <summary>
        /// 将消息头写入指定 buffer 的 offset 位置。
        /// </summary>
        public void WriteTo(byte[] buffer, int offset)
        {
            BinaryPacker.WriteUInt32LE(buffer, offset, Magic);
            buffer[offset + 4] = Version;
            buffer[offset + 5] = (byte)Type;
            BinaryPacker.WriteUInt32LE(buffer, offset + 6, Sequence);
            BinaryPacker.WriteUInt32LE(buffer, offset + 10, Length);
        }

        /// <summary>
        /// 从 14 字节数组反序列化消息头。返回是否魔数和版本有效。
        /// </summary>
        public static MessageHeader FromBytes(byte[] buffer)
        {
            MessageHeader header = new MessageHeader();
            header.Magic = BinaryPacker.ReadUInt32LE(buffer, 0);
            header.Version = buffer[4];
            header.Type = (MessageType)buffer[5];
            header.Sequence = BinaryPacker.ReadUInt32LE(buffer, 6);
            header.Length = BinaryPacker.ReadUInt32LE(buffer, 10);
            return header;
        }

        /// <summary>
        /// 从 buffer 的 offset 位置读取消息头。
        /// </summary>
        public static MessageHeader ReadFrom(byte[] buffer, int offset)
        {
            MessageHeader header = new MessageHeader();
            header.Magic = BinaryPacker.ReadUInt32LE(buffer, offset);
            header.Version = buffer[offset + 4];
            header.Type = (MessageType)buffer[offset + 5];
            header.Sequence = BinaryPacker.ReadUInt32LE(buffer, offset + 6);
            header.Length = BinaryPacker.ReadUInt32LE(buffer, offset + 10);
            return header;
        }

        /// <summary>
        /// 检查魔数和版本是否有效。
        /// </summary>
        public bool IsValid()
        {
            return Magic == ProtocolConstants.Magic && Version == ProtocolConstants.Version;
        }

        public override string ToString()
        {
            return string.Format("Type={0} Seq={1} Len={2}", Type, Sequence, Length);
        }
    }
}
