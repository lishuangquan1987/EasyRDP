using System;
using System.Text;

namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 二进制数据读写工具类。
    /// 提供大小端转换、基本类型读写，兼容 .NET 4.0 / C# 5.0。
    /// </summary>
    public static class BinaryPacker
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        #region Write helpers

        public static void WriteByte(byte[] buffer, int offset, byte value)
        {
            buffer[offset] = value;
        }

        public static void WriteUInt16LE(byte[] buffer, int offset, ushort value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        public static void WriteInt16LE(byte[] buffer, int offset, short value)
        {
            WriteUInt16LE(buffer, offset, (ushort)value);
        }

        public static void WriteUInt32LE(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        public static void WriteInt32LE(byte[] buffer, int offset, int value)
        {
            WriteUInt32LE(buffer, offset, (uint)value);
        }

        public static void WriteUInt64LE(byte[] buffer, int offset, ulong value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
            buffer[offset + 4] = (byte)((value >> 32) & 0xFF);
            buffer[offset + 5] = (byte)((value >> 40) & 0xFF);
            buffer[offset + 6] = (byte)((value >> 48) & 0xFF);
            buffer[offset + 7] = (byte)((value >> 56) & 0xFF);
        }

        /// <summary>写入字节数组</summary>
        public static void WriteBytes(byte[] buffer, int offset, byte[] data, int dataOffset, int count)
        {
            Buffer.BlockCopy(data, dataOffset, buffer, offset, count);
        }

        /// <summary>写入 UTF-8 字符串，格式：[2字节长度 LE][UTF-8字节]</summary>
        public static void WriteStringUTF8(byte[] buffer, int offset, string value)
        {
            if (value == null)
                value = string.Empty;

            byte[] bytes = Utf8.GetBytes(value);
            int len = bytes.Length;
            if (len > ushort.MaxValue)
                throw new ArgumentException("String too long for protocol", "value");

            WriteUInt16LE(buffer, offset, (ushort)len);
            if (len > 0)
                WriteBytes(buffer, offset + 2, bytes, 0, len);
        }

        /// <summary>计算 WriteStringUTF8 需要的字节数</summary>
        public static int MeasureStringUTF8(string value)
        {
            if (value == null)
                return 2;
            return 2 + Utf8.GetByteCount(value);
        }

        #endregion

        #region Read helpers

        public static byte ReadByte(byte[] buffer, int offset)
        {
            return buffer[offset];
        }

        public static ushort ReadUInt16LE(byte[] buffer, int offset)
        {
            return (ushort)(buffer[offset] | (buffer[offset + 1] << 8));
        }

        public static short ReadInt16LE(byte[] buffer, int offset)
        {
            return (short)ReadUInt16LE(buffer, offset);
        }

        public static uint ReadUInt32LE(byte[] buffer, int offset)
        {
            return (uint)(buffer[offset]
                | (buffer[offset + 1] << 8)
                | (buffer[offset + 2] << 16)
                | (buffer[offset + 3] << 24));
        }

        public static int ReadInt32LE(byte[] buffer, int offset)
        {
            return (int)ReadUInt32LE(buffer, offset);
        }

        public static ulong ReadUInt64LE(byte[] buffer, int offset)
        {
            return (ulong)buffer[offset]
                | ((ulong)buffer[offset + 1] << 8)
                | ((ulong)buffer[offset + 2] << 16)
                | ((ulong)buffer[offset + 3] << 24)
                | ((ulong)buffer[offset + 4] << 32)
                | ((ulong)buffer[offset + 5] << 40)
                | ((ulong)buffer[offset + 6] << 48)
                | ((ulong)buffer[offset + 7] << 56);
        }

        /// <summary>读取字节数组</summary>
        public static byte[] ReadBytes(byte[] buffer, int offset, int count)
        {
            byte[] result = new byte[count];
            Buffer.BlockCopy(buffer, offset, result, 0, count);
            return result;
        }

        /// <summary>读取 UTF-8 字符串，格式：[2字节长度 LE][UTF-8字节]</summary>
        public static string ReadStringUTF8(byte[] buffer, int offset, out int bytesRead)
        {
            ushort len = ReadUInt16LE(buffer, offset);
            bytesRead = 2 + len;
            if (len == 0)
                return string.Empty;
            if (offset + 2 + len > buffer.Length)
                throw new ArgumentException(string.Format(
                    "String length {0} exceeds buffer bounds (offset={1}, buffer={2})",
                    len, offset, buffer.Length));
            return Utf8.GetString(buffer, offset + 2, len);
        }

        #endregion
    }
}
