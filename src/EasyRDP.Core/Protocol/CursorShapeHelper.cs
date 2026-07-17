using System;

namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 光标形状工具：格式转换（Windows 标准格式 → RGBA）和 FNV-1a 哈希。
    /// </summary>
    public static class CursorShapeHelper
    {
        private const uint FnvOffsetBasis = 2166136261;
        private const uint FnvPrime = 16777619;

        /// <summary>
        /// 计算 ImageData 的 FNV-1a 32-bit 哈希。
        /// 用于快速检测光标形状是否变化，避免重复传输同一形状。
        /// </summary>
        public static uint ComputeHash(byte[] imageData)
        {
            if (imageData == null || imageData.Length == 0)
                return 0;

            uint hash = FnvOffsetBasis;
            for (int i = 0; i < imageData.Length; i++)
            {
                hash ^= imageData[i];
                hash *= FnvPrime;
            }
            return hash;
        }

        /// <summary>
        /// 将 Windows 标准光标格式（AND mask 1bpp + XOR mask BGRA32）转换为 RGBA32 像素数组。
        /// 
        /// imageData 布局：
        ///   [AND mask: height * andStride bytes] + [XOR mask: height * width * 4 bytes]
        ///   AND stride = ((width + 15) / 16) * 2
        ///   XOR stride = width * 4
        /// 
        /// 转换规则：
        ///   AND=1 → 完全透明 (R=0, G=0, B=0, A=0)
        ///   AND=0 → 不透明，XOR BGRA → 输出 RGBA (A=255)
        /// </summary>
        /// <param name="imageData">Windows 标准光标原始数据</param>
        /// <param name="width">光标宽度</param>
        /// <param name="height">光标高度</param>
        /// <param name="rgbaPixels">输出：width * height * 4 的 RGBA 像素数组</param>
        /// <returns>是否成功转换</returns>
        public static bool ConvertToRGBA(byte[] imageData, int width, int height, out byte[] rgbaPixels)
        {
            rgbaPixels = null;

            if (imageData == null || imageData.Length == 0)
                return false;

            if (width <= 0 || height <= 0)
                return false;

            int andStride = ((width + 15) / 16) * 2;
            int xorStride = width * 4;
            int expectedSize = (andStride + xorStride) * height;

            if (imageData.Length < expectedSize)
                return false;

            int rgbaSize = width * height * 4;
            rgbaPixels = new byte[rgbaSize];
            int andBase = 0;
            int xorBase = andStride * height;

            for (int row = 0; row < height; row++)
            {
                for (int col = 0; col < width; col++)
                {
                    int rgbaIdx = (row * width + col) * 4;

                    // 读取 AND mask bit
                    int andByteIdx = andBase + row * andStride + col / 8;
                    int andBitIdx = 7 - (col % 8);
                    bool isTransparent = ((imageData[andByteIdx] >> andBitIdx) & 1) != 0;

                    if (isTransparent)
                    {
                        // 完全透明
                        rgbaPixels[rgbaIdx] = 0;     // R
                        rgbaPixels[rgbaIdx + 1] = 0; // G
                        rgbaPixels[rgbaIdx + 2] = 0; // B
                        rgbaPixels[rgbaIdx + 3] = 0; // A
                    }
                    else
                    {
                        // XOR mask: BGRA → RGBA
                        int xorIdx = xorBase + row * xorStride + col * 4;
                        byte b = imageData[xorIdx];     // B
                        byte g = imageData[xorIdx + 1]; // G
                        byte r = imageData[xorIdx + 2]; // R
                        // xorIdx + 3 is A in BGRA, but Windows cursor XOR mask
                        // uses 0x00 for the alpha byte in color cursors
                        rgbaPixels[rgbaIdx] = r;
                        rgbaPixels[rgbaIdx + 1] = g;
                        rgbaPixels[rgbaIdx + 2] = b;
                        rgbaPixels[rgbaIdx + 3] = 255; // 完全不透明
                    }
                }
            }

            return true;
        }
    }
}
