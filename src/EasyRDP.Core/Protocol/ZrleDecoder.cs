namespace EasyRDP.Core.Protocol
{
    using System;
    using System.IO;
    using System.IO.Compression;
    using NLog;

    /// <summary>
    /// ZRLE 解码器：解包多矩形区域，合并到帧缓冲，输出完整 BGRA 帧。
    /// 无运动估计、无颜色空间转换，纯像素操作。
    /// 
    /// v2 修正：
    /// - Decode(data, outputBuffer) 直接写入 outputBuffer，省去一次 BlockCopy
    /// - DeflateStream 替代 GZipStream（与编码器一致）
    /// 
    /// v3 修正：
    /// - 单次区域应用 + 单次全帧拷贝（v2 的双倍应用是性能倒退）
    /// - _decompressBuffer 预分配解压缓冲（每区域 0 次堆分配）
    /// - CopyRect 必须先于 Raw/Deflate/FillRect 处理：CopyRect 的源是上一帧内容，
    ///   若本帧先应用了其他区域，CopyRect 会读到已更新的数据（源被污染）。
    /// </summary>
    public class ZrleDecoder : IVideoDecoder
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        /// <summary>最大瓦片边长（与编码器 TileSize 一致，决定解压缓冲大小）。</summary>
        private const int MaxTileSize = 64;

        private int _width;
        private int _height;
        private byte[] _frameBuffer;  // 完整帧缓冲（累积变化区域）
        /// <summary>v3：预分配解压缓冲（最大瓦片 64×64×4 = 16KB，足够容纳任意单区域解压结果）。</summary>
        private byte[] _decompressBuffer;
        /// <summary>FillRect 行填充缓冲（最大 64×4 = 256 字节，预分配复用）。</summary>
        private byte[] _fillRow;
        private bool _initialized;
        private bool _disposed;

        /// <summary>解码器类型。</summary>
        public CodecId Codec { get { return CodecId.Zrle; } }

        /// <summary>ZRLE 纯 C# 实现，始终可用（未 Dispose 即可用）。</summary>
        public bool IsAvailable { get { return !_disposed; } }

        /// <summary>初始化解码器。width/height 来自 HandshakeRes（编码分辨率）。</summary>
        public void Initialize(int width, int height)
        {
            if (_disposed) throw new ObjectDisposedException("ZrleDecoder");
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException("width", "width/height must be positive");
            _width = width;
            _height = height;
            _frameBuffer = new byte[width * height * 4];
            _decompressBuffer = new byte[MaxTileSize * MaxTileSize * 4];
            _fillRow = new byte[MaxTileSize * 4];
            _initialized = true;
            Logger.Info("ZRLE decoder initialized: {0}x{1}", width, height);
        }

        /// <summary>解码到内部帧缓冲（累积式）。返回内部缓冲引用（调用方不可跨帧持有）。</summary>
        public DecodeResult Decode(byte[] data)
        {
            if (!_initialized || _disposed)
                return new DecodeResult { Status = DecodeStatus.Failed };

            try
            {
                var regions = ZrleRegionCodec.Unpack(data);
                if (regions == null)
                    return new DecodeResult { Status = DecodeStatus.Failed };
                ApplyRegions(regions);
                return new DecodeResult { Status = DecodeStatus.Ok, Pixels = _frameBuffer };
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ZRLE decode failed");
                return new DecodeResult { Status = DecodeStatus.Failed };
            }
        }

        /// <summary>
        /// 解码到调用方提供的输出缓冲（省拷贝优化）。
        /// v3 做法：先将变化区域应用到 _frameBuffer（原地更新），然后整体拷贝到 outputBuffer。
        /// CopyRect 先于其他区域处理（防读到本帧已更新的数据）。
        /// </summary>
        public DecodeResult Decode(byte[] data, byte[] outputBuffer)
        {
            if (!_initialized || _disposed)
                return new DecodeResult { Status = DecodeStatus.Failed };

            if (outputBuffer == null || outputBuffer.Length < _width * _height * 4)
                return new DecodeResult { Status = DecodeStatus.Failed };

            try
            {
                var regions = ZrleRegionCodec.Unpack(data);
                if (regions == null)
                    return new DecodeResult { Status = DecodeStatus.Failed };
                ApplyRegions(regions);
                Buffer.BlockCopy(_frameBuffer, 0, outputBuffer, 0, _frameBuffer.Length);
                return new DecodeResult { Status = DecodeStatus.Ok, Pixels = outputBuffer };
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ZRLE decode failed");
                return new DecodeResult { Status = DecodeStatus.Failed };
            }
        }

        /// <summary>重置解码器内部状态（分辨率变更、断连重连）。</summary>
        public void Reset()
        {
            _frameBuffer = null;
            _decompressBuffer = null;
            _fillRow = null;
            _initialized = false;
        }

        /// <summary>释放解码器资源。</summary>
        public void Dispose()
        {
            _disposed = true;
            _frameBuffer = null;
            _decompressBuffer = null;
            _fillRow = null;
        }

        /// <summary>
        /// 应用一帧的全部区域到 _frameBuffer。
        /// 顺序：先 CopyRect（源=上一帧的 _frameBuffer 内容），再 Raw/Deflate/FillRect。
        /// 原因：CopyRect 的源必须是"本帧应用前"的帧内容；若先应用其他区域，
        /// 源区域可能已被覆盖 → 读到本帧新数据 → 画面错误。
        /// </summary>
        private void ApplyRegions(ZrleRegion[] regions)
        {
            // 第一遍：CopyRect（从 _frameBuffer 读上一帧内容）
            for (int i = 0; i < regions.Length; i++)
            {
                ZrleRegion region = regions[i];
                if (region.Encoding == ZrleRegionEncoding.CopyRect)
                {
                    // 目标越界的 CopyRect：跳过本区域（防恶意/损坏数据引发越界崩溃）
                    if (!IsRegionInBounds(region))
                        continue;
                    ApplyCopyRect(region);
                }
            }
            // 第二遍：Raw / Deflate / FillRect
            for (int i = 0; i < regions.Length; i++)
            {
                ZrleRegion region = regions[i];
                if (region.Encoding != ZrleRegionEncoding.CopyRect)
                {
                    // 目标越界的区域：跳过本区域（其余区域不受影响）
                    if (!IsRegionInBounds(region))
                        continue;
                    ApplyRegion(region);
                }
            }
        }

        /// <summary>校验区域目标矩形是否完全落在帧内（防越界写）。</summary>
        private bool IsRegionInBounds(ZrleRegion region)
        {
            return region.X >= 0 && region.Y >= 0
                && region.Width > 0 && region.Height > 0
                && region.X + region.Width <= _width
                && region.Y + region.Height <= _height;
        }

        /// <summary>将单个非 CopyRect 区域应用到帧缓冲。</summary>
        private void ApplyRegion(ZrleRegion region)
        {
            int stride = _width * 4;
            int regionStride = region.Width * 4;

            switch (region.Encoding)
            {
                case ZrleRegionEncoding.Raw:
                    for (int y = 0; y < region.Height; y++)
                    {
                        Buffer.BlockCopy(region.Data, y * regionStride,
                            _frameBuffer, (region.Y + y) * stride + region.X * 4, regionStride);
                    }
                    break;

                case ZrleRegionEncoding.Deflate:
                    // 解压到预分配缓冲（0 次堆分配），再逐行写入帧缓冲
                    int decompressedLen = DeflateDecompress(region.Data, region.DataLen, _decompressBuffer, 0);
                    if (decompressedLen < regionStride * region.Height)
                    {
                        // 解压数据不足：数据损坏，跳过本区域（其余区域不受影响）
                        Logger.Warn("ZRLE deflate region too short: {0} < {1}", decompressedLen, regionStride * region.Height);
                        break;
                    }
                    for (int y = 0; y < region.Height; y++)
                    {
                        Buffer.BlockCopy(_decompressBuffer, y * regionStride,
                            _frameBuffer, (region.Y + y) * stride + region.X * 4, regionStride);
                    }
                    break;

                case ZrleRegionEncoding.FillRect:
                    ApplyFillRect(region);
                    break;

                default:
                    // 未知编码类型：忽略本区域（防御，不中断解码）
                    Logger.Warn("ZRLE unknown region encoding: {0}", region.Encoding);
                    break;
            }
        }

        /// <summary>应用 CopyRect 区域：从 _frameBuffer 的源位置复制到目标位置（重叠安全）。</summary>
        private void ApplyCopyRect(ZrleRegion region)
        {
            int stride = _width * 4;
            int regionStride = region.Width * 4;
            if (region.Data == null || region.DataLen < 8)
                return;
            int srcX = BitConverter.ToInt32(region.Data, 0);
            int srcY = BitConverter.ToInt32(region.Data, 4);

            // 源区域边界检查（防越界）
            if (srcX < 0 || srcY < 0 || srcX + region.Width > _width || srcY + region.Height > _height)
            {
                Logger.Warn("ZRLE copy rect source out of bounds: src=({0},{1}) dst=({2},{3}) size={4}x{5}",
                    srcX, srcY, region.X, region.Y, region.Width, region.Height);
                return;
            }

            // 源和目标可能跨行重叠：源在目标上方时从后向前逐行复制，否则从前向后。
            // 单次 BlockCopy 具有 memmove 语义（.NET 保证重叠安全），行内水平重叠自动正确。
            if (srcY < region.Y || (srcY == region.Y && srcX < region.X))
            {
                // 源在目标上方/左方：从后向前复制（先复制最底行，避免覆盖未读的源行）
                for (int y = region.Height - 1; y >= 0; y--)
                {
                    int srcRow = (srcY + y) * stride + srcX * 4;
                    int dstRow = (region.Y + y) * stride + region.X * 4;
                    Buffer.BlockCopy(_frameBuffer, srcRow, _frameBuffer, dstRow, regionStride);
                }
            }
            else
            {
                // 源在目标下方/右方：从前向后复制
                for (int y = 0; y < region.Height; y++)
                {
                    int srcRow = (srcY + y) * stride + srcX * 4;
                    int dstRow = (region.Y + y) * stride + region.X * 4;
                    Buffer.BlockCopy(_frameBuffer, srcRow, _frameBuffer, dstRow, regionStride);
                }
            }
        }

        /// <summary>用 4 字节颜色填充矩形区域。</summary>
        private void ApplyFillRect(ZrleRegion region)
        {
            int stride = _width * 4;
            int regionStride = region.Width * 4;
            if (region.Data == null || region.DataLen < 4)
                return;
            if (regionStride > _fillRow.Length)
                return;  // 宽度超预分配上限（理论上不可能，防御）

            byte fillB = region.Data[0];
            byte fillG = region.Data[1];
            byte fillR = region.Data[2];
            byte fillA = region.Data[3];
            for (int x = 0; x < region.Width; x++)
            {
                _fillRow[x * 4] = fillB;
                _fillRow[x * 4 + 1] = fillG;
                _fillRow[x * 4 + 2] = fillR;
                _fillRow[x * 4 + 3] = fillA;
            }
            for (int y = 0; y < region.Height; y++)
            {
                int rowOffset = (region.Y + y) * stride + region.X * 4;
                Buffer.BlockCopy(_fillRow, 0, _frameBuffer, rowOffset, regionStride);
            }
        }

        /// <summary>
        /// v3 修正：Deflate 解压到预分配缓冲（替代 v2 的每次 4 次堆分配）。
        /// 返回实际写入长度；输出不足时返回小于期望的值（调用方按损坏处理）。
        /// </summary>
        private int DeflateDecompress(byte[] data, int dataLen, byte[] output, int outputOffset)
        {
            using (var ms = new MemoryStream(data, 0, dataLen))
            {
                using (var ds = new DeflateStream(ms, CompressionMode.Decompress, true))
                {
                    int totalRead = 0;
                    int remaining = output.Length - outputOffset;
                    while (totalRead < remaining)
                    {
                        int read = ds.Read(output, outputOffset + totalRead, remaining - totalRead);
                        if (read <= 0) break;
                        totalRead += read;
                    }
                    return totalRead;
                }
            }
        }
    }
}
