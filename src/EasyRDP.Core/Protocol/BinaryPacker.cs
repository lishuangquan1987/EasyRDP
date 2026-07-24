namespace EasyRDP.Core.Protocol
{
    using System;
    using System.IO;
    using System.Text;

    /// <summary>
    /// 紧凑二进制序列化器。所有消息 payload 的读写都经它，保证小端、紧凑布局。
    /// net40/C#5.0 可用，内部基于 BinaryWriter/BinaryReader，无第三方依赖。
    /// </summary>
    public class BinaryPacker : IDisposable
    {
        private readonly MemoryStream _stream;
        private readonly BinaryWriter _writer;
        private readonly BinaryReader _reader;

        /// <summary>Initializes a new instance of the <see cref="BinaryPacker"/> class for writing.</summary>
        public BinaryPacker()
        {
            _stream = new MemoryStream();
            _writer = new BinaryWriter(_stream);
        }

        private BinaryPacker(byte[] data)
        {
            _stream = new MemoryStream(data);
            _reader = new BinaryReader(_stream);
        }

        /// <summary>Creates a <see cref="BinaryPacker"/> instance for reading from the given byte array.</summary>
        /// <param name="data">The byte array to read from.</param>
        public static BinaryPacker From(byte[] data)
        {
            return new BinaryPacker(data);
        }

        /// <summary>Returns the written bytes as an array.</summary>
        public byte[] GetBytes()
        {
            _writer.Flush();
            return _stream.ToArray();
        }

        /// <summary>Releases all resources used by the <see cref="BinaryPacker"/>.</summary>
        public void Dispose()
        {
            if (_writer != null) _writer.Dispose();
            if (_reader != null) _reader.Dispose();
            if (_stream != null) _stream.Dispose();
        }

        // —— 写方法 ——

        /// <summary>Writes a single byte.</summary>
        public void WriteByte(byte v)
        {
            _writer.Write(v);
        }

        /// <summary>Writes a 32-bit signed integer in little-endian format.</summary>
        public void WriteInt32(int v)
        {
            _writer.Write(v);
        }

        /// <summary>Writes a 32-bit unsigned integer in little-endian format.</summary>
        public void WriteUInt32(uint v)
        {
            _writer.Write(v);
        }

        /// <summary>Writes a 64-bit signed integer in little-endian format.</summary>
        public void WriteInt64(long v)
        {
            _writer.Write(v);
        }

        /// <summary>写入字符串：uint16 长度前缀 + UTF-8 编码。</summary>
        public void WriteString(string v)
        {
            if (v == null)
                v = "";
            byte[] b = Encoding.UTF8.GetBytes(v);
            _writer.Write((ushort)b.Length);
            _writer.Write(b);
        }

        /// <summary>写入字节数组：uint32 长度前缀 + 原始字节。null 按长度 0 处理。</summary>
        public void WriteBytes(byte[] v)
        {
            if (v == null)
            {
                _writer.Write((uint)0);
                return;
            }
            _writer.Write((uint)v.Length);
            if (v.Length > 0)
                _writer.Write(v);
        }

        // —— 读方法 ——

        /// <summary>Reads a single byte.</summary>
        public byte ReadByte()
        {
            return _reader.ReadByte();
        }

        /// <summary>Reads a 32-bit signed integer in little-endian format.</summary>
        public int ReadInt32()
        {
            return _reader.ReadInt32();
        }

        /// <summary>Reads a 32-bit unsigned integer in little-endian format.</summary>
        public uint ReadUInt32()
        {
            return _reader.ReadUInt32();
        }

        /// <summary>Reads a 64-bit signed integer in little-endian format.</summary>
        public long ReadInt64()
        {
            return _reader.ReadInt64();
        }

        /// <summary>读取字符串：先读 uint16 长度，再读 UTF-8 字节。</summary>
        public string ReadString()
        {
            int len = _reader.ReadUInt16();
            if (len == 0)
                return "";
            return Encoding.UTF8.GetString(_reader.ReadBytes(len));
        }

        /// <summary>读取字节数组：先读 uint32 长度，再读原始字节。长度为 0 返回 null。</summary>
        public byte[] ReadBytes()
        {
            int len = (int)_reader.ReadUInt32();
            if (len == 0)
                return null;
            return _reader.ReadBytes(len);
        }
    }
}
