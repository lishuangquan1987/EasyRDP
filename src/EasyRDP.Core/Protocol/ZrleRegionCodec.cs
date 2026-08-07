namespace EasyRDP.Core.Protocol
{
    using EasyRDP.Core.Rendering;

    /// <summary>
    /// ZRLE 区域编码格式：一帧由多个矩形区域组成，每个区域独立编码。
    /// 阶段一支持 Raw/Deflate 编码；阶段二扩展 FillRect/CopyRect。
    /// </summary>
    public enum ZrleRegionEncoding : byte
    {
        /// <summary>无压缩 BGRA 像素。</summary>
        Raw = 0,
        /// <summary>Deflate 压缩 BGRA 像素（v2：从 Zlib 改为 Deflate，去掉 GZip 18 字节头尾开销）。</summary>
        Deflate = 1,
        /// <summary>纯色矩形（Data 仅 4 字节 BGRA 颜色值）。</summary>
        FillRect = 2,
        /// <summary>矩形移动（Data 为源坐标 SrcX+SrcY，8 字节）。</summary>
        CopyRect = 3
    }

    /// <summary>
    /// 单个矩形区域更新。
    /// v3 修正：新增 DataLen 字段，支持池化 Data 引用（避免每瓦片 new byte[]）。
    /// </summary>
    public struct ZrleRegion
    {
        /// <summary>区域左上角 X 坐标（像素）。</summary>
        public int X;
        /// <summary>区域左上角 Y 坐标（像素）。</summary>
        public int Y;
        /// <summary>区域宽度（像素）。</summary>
        public int Width;
        /// <summary>区域高度（像素）。</summary>
        public int Height;
        /// <summary>编码类型。</summary>
        public ZrleRegionEncoding Encoding;
        /// <summary>编码后的数据（可能指向池化缓冲，实际有效长度由 DataLen 决定）。</summary>
        public byte[] Data;
        /// <summary>v3 新增：Data 中的有效字节数（Data.Length 可能大于此值，因为 Data 可能是池化缓冲）。</summary>
        public int DataLen;
    }

    /// <summary>
    /// ZRLE 区域打包/解包工具。独立于编码器/解码器，便于单元测试。
    /// 格式：RegionCount(4 LE) + N × [X(4)+Y(4)+W(4)+H(4)+Enc(1)+DataLen(4)+Data(*)]。
    /// 所有整数均为小端序（BitConverter 在 x86/x64 即小端，此处手工移位显式保证）。
    /// </summary>
    public static class ZrleRegionCodec
    {
        /// <summary>最大区域数限制（防恶意数据 OOM）。
        /// 必须 ≥ 1080p 的 64×64 瓦片数（1920×1080 → 34×17=578）；
        /// 1024 覆盖 2K 分辨率（2560×1440 → 41×23=943）。</summary>
        public const int MaxRegionCount = 1024;

        /// <summary>单区域头大小：X(4)+Y(4)+W(4)+H(4)+Enc(1)+DataLen(4) = 21 字节。</summary>
        public const int RegionHeaderSize = 21;

        /// <summary>单区域数据上限：最大瓦片 64×64×4=16KB，Deflate 最坏膨胀约 1.0003 倍，取 2 倍余量 32KB。</summary>
        public const int MaxRegionDataSize = 64 * 64 * 4 * 2;

        /// <summary>打包多个区域为字节数组（等价于 Pack(regions, regions.Length)）。</summary>
        public static byte[] Pack(ZrleRegion[] regions)
        {
            return Pack(regions, regions != null ? regions.Length : 0);
        }

        /// <summary>
        /// v3 新增：打包前 count 个区域（避免调用方 Array.Copy 分配新数组）。
        /// 仅打包 regions[0..count-1]，忽略后续元素。
        /// </summary>
        /// <param name="regions">区域数组。</param>
        /// <param name="count">实际打包的区域数（必须 0 ≤ count ≤ regions.Length）。</param>
        /// <returns>打包后的字节数组。</returns>
        public static byte[] Pack(ZrleRegion[] regions, int count)
        {
            if (regions == null) count = 0;
            if (count < 0) count = 0;
            if (count > MaxRegionCount) count = MaxRegionCount;
            if (regions != null && count > regions.Length) count = regions.Length;

            // 先遍历计算总长度，一次分配（避免 MemoryStream 内部多次扩容拷贝）
            int total = 4;
            for (int i = 0; i < count; i++)
            {
                ZrleRegion r = regions[i];
                int dataLen = (r.DataLen > 0) ? r.DataLen : (r.Data != null ? r.Data.Length : 0);
                total += RegionHeaderSize + dataLen;
            }

            byte[] result = new byte[total];
            int offset = 0;

            WriteInt32(result, ref offset, count);
            for (int i = 0; i < count; i++)
            {
                ZrleRegion r = regions[i];
                int dataLen = (r.DataLen > 0) ? r.DataLen : (r.Data != null ? r.Data.Length : 0);
                WriteInt32(result, ref offset, r.X);
                WriteInt32(result, ref offset, r.Y);
                WriteInt32(result, ref offset, r.Width);
                WriteInt32(result, ref offset, r.Height);
                result[offset++] = (byte)r.Encoding;
                WriteInt32(result, ref offset, dataLen);
                if (dataLen > 0 && r.Data != null)
                {
                    System.Buffer.BlockCopy(r.Data, 0, result, offset, dataLen);
                }
                offset += dataLen;
            }
            return result;
        }

        /// <summary>
        /// 从字节数组解包区域列表。数据损坏/恶意输入时返回 null（调用方按解码失败处理）。
        /// 每个区域的 Data 为独立新分配的 byte[DataLen]。
        /// </summary>
        public static ZrleRegion[] Unpack(byte[] data)
        {
            if (data == null || data.Length < 4)
                return null;

            int offset = 0;
            int count = ReadInt32(data, ref offset);
            if (count < 0 || count > MaxRegionCount)
                return null;
            // 最小长度：RegionCount(4) + count × 区域头(21)
            if (data.Length < 4 + (long)count * RegionHeaderSize)
                return null;

            ZrleRegion[] regions = new ZrleRegion[count];
            for (int i = 0; i < count; i++)
            {
                int x = ReadInt32(data, ref offset);
                int y = ReadInt32(data, ref offset);
                int w = ReadInt32(data, ref offset);
                int h = ReadInt32(data, ref offset);
                byte enc = data[offset++];
                int dataLen = ReadInt32(data, ref offset);

                // 区域几何合法性：ZRLE 区域由 64×64 瓦片编码生成，w/h 上限 64，
                // 与解码器预分配解压缓冲（64×64×4=16KB）对齐；超限视为恶意/损坏数据。
                if (w <= 0 || h <= 0 || w > 64 || h > 64 || (long)w * h * 4 > MaxRegionDataSize)
                    return null;
                // 按编码类型校验 DataLen（防越界/防错乱）
                if (!ValidateDataLen((ZrleRegionEncoding)enc, dataLen, w, h))
                    return null;
                if ((long)offset + dataLen > data.Length)
                    return null;

                byte[] regionData = null;
                if (dataLen > 0)
                {
                    regionData = new byte[dataLen];
                    System.Buffer.BlockCopy(data, offset, regionData, 0, dataLen);
                }
                offset += dataLen;

                regions[i] = new ZrleRegion
                {
                    X = x,
                    Y = y,
                    Width = w,
                    Height = h,
                    Encoding = (ZrleRegionEncoding)enc,
                    Data = regionData,
                    DataLen = dataLen
                };
            }
            return regions;
        }

        /// <summary>
        /// 从打包数据中提取矩形坐标列表（不解压 Data）。
        /// 用于客户端显示层脏矩形局部更新（阶段二）：ZRLE 帧的每个区域即一个脏矩形。
        /// 数据损坏时返回 null（调用方回退到全帧渲染）。
        /// </summary>
        public static ScreenRect[] ExtractRects(byte[] data)
        {
            if (data == null || data.Length < 4)
                return null;

            int offset = 0;
            int count = ReadInt32(data, ref offset);
            if (count < 0 || count > MaxRegionCount)
                return null;
            if (data.Length < 4 + (long)count * RegionHeaderSize)
                return null;

            ScreenRect[] rects = new ScreenRect[count];
            for (int i = 0; i < count; i++)
            {
                int x = ReadInt32(data, ref offset);
                int y = ReadInt32(data, ref offset);
                int w = ReadInt32(data, ref offset);
                int h = ReadInt32(data, ref offset);
                byte enc = data[offset++];
                int dataLen = ReadInt32(data, ref offset);
                // 区域几何合法性（与 Unpack 一致）：w/h 上限 64 与解码解压缓冲对齐
                if (w <= 0 || h <= 0 || w > 64 || h > 64 || (long)w * h * 4 > MaxRegionDataSize)
                    return null;
                if (!ValidateDataLen((ZrleRegionEncoding)enc, dataLen, w, h))
                    return null;
                if ((long)offset + dataLen > data.Length)
                    return null;
                offset += dataLen;

                rects[i] = new ScreenRect { X = x, Y = y, Width = w, Height = h };
            }
            return rects;
        }

        /// <summary>按编码类型校验 DataLen 是否合法（防恶意数据越界/错乱）。</summary>
        private static bool ValidateDataLen(ZrleRegionEncoding encoding, int dataLen, int w, int h)
        {
            if (dataLen < 0 || dataLen > MaxRegionDataSize)
                return false;
            switch (encoding)
            {
                case ZrleRegionEncoding.Raw:
                    // Raw 必须精确等于 W×H×4 字节
                    return dataLen == (long)w * h * 4;
                case ZrleRegionEncoding.Deflate:
                    // 压缩结果最坏略大于输入，上限已由 MaxRegionDataSize 覆盖
                    return true;
                case ZrleRegionEncoding.FillRect:
                    return dataLen == 4;
                case ZrleRegionEncoding.CopyRect:
                    return dataLen == 8;
                default:
                    return false;
            }
        }

        /// <summary>写小端 int 到缓冲并推进偏移。</summary>
        private static void WriteInt32(byte[] buffer, ref int offset, int value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
            offset += 4;
        }

        /// <summary>读小端 int 并推进偏移。</summary>
        private static int ReadInt32(byte[] buffer, ref int offset)
        {
            int value = buffer[offset]
                | (buffer[offset + 1] << 8)
                | (buffer[offset + 2] << 16)
                | (buffer[offset + 3] << 24);
            offset += 4;
            return value;
        }
    }
}
