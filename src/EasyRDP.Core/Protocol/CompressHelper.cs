using System;
using System.IO;
using System.IO.Compression;

namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 屏幕帧压缩/解压工具类。
    /// 使用 DeflateStream（RFC 1951），兼容 net40 和 net8.0。
    /// </summary>
    public static class CompressHelper
    {
        /// <summary>压缩字节数组</summary>
        public static byte[] Compress(byte[] data, CompressType type)
        {
            if (data == null || data.Length == 0)
                return new byte[0];

            switch (type)
            {
                case CompressType.None:
                    return data;
                case CompressType.Zlib:
                    return CompressDeflate(data);
                default:
                    throw new NotSupportedException(string.Format("Compress type {0} not implemented", type));
            }
        }

        /// <summary>解压字节数组</summary>
        public static byte[] Decompress(byte[] data, CompressType type, int originalSize)
        {
            if (data == null || data.Length == 0)
                return new byte[0];

            switch (type)
            {
                case CompressType.None:
                    return data;
                case CompressType.Zlib:
                    return DecompressDeflate(data, originalSize);
                default:
                    throw new NotSupportedException(string.Format("Compress type {0} not implemented", type));
            }
        }

        private static byte[] CompressDeflate(byte[] data)
        {
            using (var output = new MemoryStream())
            {
                using (var deflate = new DeflateStream(output, CompressionMode.Compress, true))
                {
                    deflate.Write(data, 0, data.Length);
                    deflate.Flush();
                }
                return output.ToArray();
            }
        }

        private static byte[] DecompressDeflate(byte[] data, int originalSize)
        {
            // Pre-allocate buffer based on original size hint
            using (var input = new MemoryStream(data))
            using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream(originalSize > 0 ? originalSize : data.Length * 4))
            {
                byte[] buffer = new byte[4096];
                int bytesRead;
                while ((bytesRead = deflate.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, bytesRead);
                }
                return output.ToArray();
            }
        }
    }
}
