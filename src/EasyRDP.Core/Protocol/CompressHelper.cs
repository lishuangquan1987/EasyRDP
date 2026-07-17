using System;
using System.IO;
using System.IO.Compression;

namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 屏幕帧压缩/解压工具类。
    /// 支持 Deflate (Zlib) 和 JPEG 两种压缩方式。
    /// 兼容 .NET 4.0 / C# 5.0。
    /// </summary>
    public static class CompressHelper
    {
        /// <summary>JPEG 默认质量（1-100）</summary>
        public const int JpegQuality = 80;

        /// <summary>压缩字节数组（需要宽高用于有损编码）</summary>
        public static byte[] Compress(byte[] data, CompressType type, int width = 0, int height = 0)
        {
            if (data == null || data.Length == 0)
                return new byte[0];

            switch (type)
            {
                case CompressType.None:
                    return data;
                case CompressType.Zlib:
                    return CompressDeflate(data);
                case CompressType.JPEG:
                    if (width > 0 && height > 0 && data.Length == width * height * 4)
                        return EncodeJPEG(data, width, height);
                    return data; // 无法确定尺寸时降级
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
                case CompressType.JPEG:
                    return DecompressJPEG(data, originalSize);
                default:
                    throw new NotSupportedException(string.Format("Compress type {0} not implemented", type));
            }
        }

        #region Deflate (Zlib)

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

        #endregion

        #region JPEG

        /// <summary>
        /// 将 BGRA32 像素编码为 JPEG 字节数组。
        /// </summary>
        /// <param name="bgraData">BGRA32 像素数据</param>
        /// <param name="width">图像宽度</param>
        /// <param name="height">图像高度</param>
        /// <returns>JPEG 字节数组</returns>
        public static byte[] EncodeJPEG(byte[] bgraData, int width, int height)
        {
            using (var bmp = new System.Drawing.Bitmap(width, height,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                // 锁定位图，将 BGRA 数据写入
                var rect = new System.Drawing.Rectangle(0, 0, width, height);
                var bmpData = bmp.LockBits(rect,
                    System.Drawing.Imaging.ImageLockMode.WriteOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);

                try
                {
                    // Copy BGRA pixels (stride may differ, copy row by row)
                    int srcStride = width * 4;
                    int dstStride = bmpData.Stride;
                    int rowSize = Math.Min(srcStride, Math.Abs(dstStride));
                    for (int y = 0; y < height; y++)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(
                            bgraData, y * srcStride,
                            new IntPtr(bmpData.Scan0.ToInt64() + y * dstStride),
                            rowSize);
                    }
                }
                finally
                {
                    bmp.UnlockBits(bmpData);
                }

                // 编码为 JPEG
                var encoder = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders();
                System.Drawing.Imaging.ImageCodecInfo jpegCodec = null;
                for (int i = 0; i < encoder.Length; i++)
                {
                    if (encoder[i].FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid)
                    {
                        jpegCodec = encoder[i];
                        break;
                    }
                }

                if (jpegCodec == null)
                    return bgraData; // 降级

                var qualityParam = new System.Drawing.Imaging.EncoderParameter(
                    System.Drawing.Imaging.Encoder.Quality, (long)JpegQuality);
                var encoderParams = new System.Drawing.Imaging.EncoderParameters(1);
                encoderParams.Param[0] = qualityParam;

                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, jpegCodec, encoderParams);
                    return ms.ToArray();
                }
            }
        }

        /// <summary>
        /// 将 JPEG 字节数组解码为 BGRA32 像素数据。
        /// </summary>
        /// <param name="jpegData">JPEG 字节数组</param>
        /// <param name="expectedSize">期望的 BGRA32 数据大小（width*height*4）</param>
        /// <returns>BGRA32 像素数据；失败时返回空数组</returns>
        public static byte[] DecompressJPEG(byte[] jpegData, int expectedSize)
        {
            try
            {
                using (var ms = new MemoryStream(jpegData))
                using (var bmp = new System.Drawing.Bitmap(ms))
                {
                    int w = bmp.Width;
                    int h = bmp.Height;
                    int stride = w * 4;
                    int size = stride * h;

                    byte[] result = new byte[size];
                    var rect = new System.Drawing.Rectangle(0, 0, w, h);
                    var bmpData = bmp.LockBits(rect,
                        System.Drawing.Imaging.ImageLockMode.ReadOnly,
                        System.Drawing.Imaging.PixelFormat.Format32bppArgb);

                    try
                    {
                        int srcStride = bmpData.Stride;
                        int rowSize = Math.Min(stride, Math.Abs(srcStride));
                        for (int y = 0; y < h; y++)
                        {
                            System.Runtime.InteropServices.Marshal.Copy(
                                new IntPtr(bmpData.Scan0.ToInt64() + y * srcStride),
                                result, y * stride, rowSize);
                        }
                    }
                    finally
                    {
                        bmp.UnlockBits(bmpData);
                    }

                    return result;
                }
            }
            catch
            {
                return new byte[0];
            }
        }

        /// <summary>
        /// 判断 JPEG 压缩是否可能有效：对于复杂/照片类内容有效，纯色/简单内容用 Zlib 更好。
        /// </summary>
        /// <param name="bgraData">BGRA32 像素数据</param>
        /// <param name="pixelCount">像素总数</param>
        /// <returns>true 表示 JPEG 可能更好</returns>
        public static bool ShouldUseJPEG(byte[] bgraData, int pixelCount)
        {
            if (bgraData == null || pixelCount < 1024)
                return false; // 小图用 Zlib

            // 采样判断：如果前 64 个像素颜色离散度很高，用 JPEG
            int sampleCount = Math.Min(64, pixelCount);
            int uniqueColors = 0;
            var seen = new System.Collections.Generic.HashSet<int>();
            for (int i = 0; i < sampleCount; i++)
            {
                int color = BitConverter.ToInt32(bgraData, i * 4);
                if (seen.Add(color))
                    uniqueColors++;
            }

            // 采样颜色数 > 40 或像素总数 > 50000 → JPEG 更优
            return uniqueColors > 40 || pixelCount > 50000;
        }

        #endregion
    }
}
