using System;
using EasyRDP.Core.Protocol;
using Xunit;

namespace EasyRDP.Core.Tests.Protocol
{
    /// <summary>
    /// ZrleDecoder 测试：各编码类型应用、累积帧、CopyRect 重叠、输出缓冲、恶意数据。
    /// </summary>
    public class ZrleDecoderTests
    {
        private const int W = 192;
        private const int H = 128;

        private static byte[] CreateFrame(int w, int h, byte b, byte g, byte r)
        {
            byte[] pixels = new byte[w * h * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = b;
                pixels[i + 1] = g;
                pixels[i + 2] = r;
                pixels[i + 3] = 255;
            }
            return pixels;
        }

        private static byte[] DeflateCompress(byte[] data)
        {
            using (var ms = new System.IO.MemoryStream())
            {
                using (var ds = new System.IO.Compression.DeflateStream(
                    ms, System.IO.Compression.CompressionMode.Compress, true))
                {
                    ds.Write(data, 0, data.Length);
                }
                return ms.ToArray();
            }
        }

        [Fact]
        public void Decode_ZeroRegionFrame_ReturnsOk()
        {
            var dec = new ZrleDecoder();
            dec.Initialize(W, H);
            byte[] empty = ZrleRegionCodec.Pack(new ZrleRegion[0]);

            var result = dec.Decode(empty);
            Assert.Equal(DecodeStatus.Ok, result.Status);
            dec.Dispose();
        }

        [Fact]
        public void Decode_DeflateRegion_AppliesPixels()
        {
            var dec = new ZrleDecoder();
            dec.Initialize(W, H);
            byte[] frame = CreateFrame(W, H, 10, 20, 30);
            byte[] tile = new byte[64 * 64 * 4];
            for (int i = 0; i < tile.Length; i += 4)
            {
                tile[i] = 200;
                tile[i + 1] = 100;
                tile[i + 2] = 50;
                tile[i + 3] = 255;
            }
            byte[] compressed = DeflateCompress(tile);

            var regions = new ZrleRegion[]
            {
                new ZrleRegion
                {
                    X = 0, Y = 0, Width = 64, Height = 64,
                    Encoding = ZrleRegionEncoding.Deflate,
                    Data = compressed,
                    DataLen = compressed.Length
                }
            };
            var result = dec.Decode(ZrleRegionCodec.Pack(regions));
            Assert.Equal(DecodeStatus.Ok, result.Status);

            // 验证 (0,0) 瓦片被替换为纯色，其他区域保持零（decoder 帧缓冲初始全零）
            byte[] pixels = result.Pixels;
            Assert.Equal(200, pixels[0]);
            Assert.Equal(100, pixels[1]);
            Assert.Equal(50, pixels[2]);
            // (1,0) 瓦片（x=64）未被修改：初始全零
            int off = 64 * 4;
            Assert.Equal(0, pixels[off]);
            dec.Dispose();
        }

        [Fact]
        public void Decode_FillRect_AppliesSolidColor()
        {
            var dec = new ZrleDecoder();
            dec.Initialize(W, H);

            var regions = new ZrleRegion[]
            {
                new ZrleRegion
                {
                    X = 64, Y = 0, Width = 64, Height = 64,
                    Encoding = ZrleRegionEncoding.FillRect,
                    Data = new byte[] { 7, 8, 9, 255 },  // B,G,R,A
                    DataLen = 4
                }
            };
            var result = dec.Decode(ZrleRegionCodec.Pack(regions));
            Assert.Equal(DecodeStatus.Ok, result.Status);

            int off = (0 * W + 64) * 4;
            Assert.Equal(7, result.Pixels[off]);
            Assert.Equal(8, result.Pixels[off + 1]);
            Assert.Equal(9, result.Pixels[off + 2]);
            // 瓦片内右下角也是纯色
            int off2 = (63 * W + 127) * 4;
            Assert.Equal(7, result.Pixels[off2]);
            dec.Dispose();
        }

        [Fact]
        public void Decode_CopyRect_SourceBelowTarget()
        {
            var dec = new ZrleDecoder();
            dec.Initialize(W, H);

            // 先把 (64,64) 区域填上内容（模拟上一帧）
            var fill = new ZrleRegion[]
            {
                new ZrleRegion
                {
                    X = 64, Y = 64, Width = 64, Height = 64,
                    Encoding = ZrleRegionEncoding.FillRect,
                    Data = new byte[] { 200, 100, 50, 255 },
                    DataLen = 4
                }
            };
            dec.Decode(ZrleRegionCodec.Pack(fill));

            // CopyRect：从 (64,64) 复制到 (64,0)（源在目标下方 → 从前向后）
            var copy = new ZrleRegion[]
            {
                new ZrleRegion
                {
                    X = 64, Y = 0, Width = 64, Height = 64,
                    Encoding = ZrleRegionEncoding.CopyRect,
                    Data = new byte[] { 64, 0, 0, 0, 64, 0, 0, 0 },
                    DataLen = 8
                }
            };
            var result = dec.Decode(ZrleRegionCodec.Pack(copy));
            Assert.Equal(DecodeStatus.Ok, result.Status);

            int off = (0 * W + 64) * 4;
            Assert.Equal(200, result.Pixels[off]);
            Assert.Equal(100, result.Pixels[off + 1]);
            Assert.Equal(50, result.Pixels[off + 2]);
            dec.Dispose();
        }

        [Fact]
        public void Decode_CopyRect_OverlappingSource_TopDown()
        {
            var dec = new ZrleDecoder();
            dec.Initialize(W, H);

            // 目标 (64,0)，源 (64,8) —— 源在目标下方 8 像素，区域 64×64 高 → 源区 [8,72) 与目标区 [0,64) 重叠
            // 先用 FillRect 填充源区（目标区 [0,64) 全部填充，源区 [8,72) 中 [64,72) 部分留零——
            // 保持区域高 ≤ 64（ZRLE 瓦片上限），重叠验证不受影响）
            var fill = new ZrleRegion[]
            {
                new ZrleRegion
                {
                    X = 64, Y = 0, Width = 64, Height = 64,
                    Encoding = ZrleRegionEncoding.FillRect,
                    Data = new byte[] { 200, 100, 50, 255 },
                    DataLen = 4
                }
            };
            dec.Decode(ZrleRegionCodec.Pack(fill));

            // 用 1 像素高的区分行验证：把 y=8 行改成不同颜色
            var stripe = new ZrleRegion[]
            {
                new ZrleRegion
                {
                    X = 64, Y = 8, Width = 64, Height = 1,
                    Encoding = ZrleRegionEncoding.FillRect,
                    Data = new byte[] { 250, 0, 0, 255 },
                    DataLen = 4
                }
            };
            dec.Decode(ZrleRegionCodec.Pack(stripe));

            // CopyRect 源 (64,8) → 目标 (64,0)：复制 y∈[8,72) 到 y∈[0,64)
            // 重叠区域（目标 y 0-63 与源 y 8-71 相交）：源在目标下方 → 从前向后逐行复制
            var copy = new ZrleRegion[]
            {
                new ZrleRegion
                {
                    X = 64, Y = 0, Width = 64, Height = 64,
                    Encoding = ZrleRegionEncoding.CopyRect,
                    Data = new byte[] { 64, 0, 0, 0, 8, 0, 0, 0 },
                    DataLen = 8
                }
            };
            var result = dec.Decode(ZrleRegionCodec.Pack(copy));
            Assert.Equal(DecodeStatus.Ok, result.Status);

            // 目标 y=0 行应 = 源 y=8 行（红色条纹）
            int off = (0 * W + 64) * 4;
            Assert.Equal(250, result.Pixels[off]);
            Assert.Equal(0, result.Pixels[off + 1]);
            // 目标 y=1 行应 = 源 y=9 行（原纯色）
            int off2 = (1 * W + 64) * 4;
            Assert.Equal(200, result.Pixels[off2]);
            dec.Dispose();
        }

        [Fact]
        public void Decode_CopyRect_ProcessedBeforeOtherRegions()
        {
            var dec = new ZrleDecoder();
            dec.Initialize(W, H);

            // 关键场景：同一帧内 CopyRect 源区域也被 Raw 覆盖。
            // 正确行为：CopyRect 先从"上一帧"内容复制，然后 Raw 覆盖目标区域。
            // 用两帧构造：帧 1 在 (64,64) 填蓝色；帧 2 同时含
            //   CopyRect (64,64)→(64,0) 和 Raw 覆盖 (64,64) 为红色。
            // 若 CopyRect 后处理会读到已覆盖的红色（错误），先处理则读到蓝色（正确）。
            var fill = new ZrleRegion[]
            {
                new ZrleRegion
                {
                    X = 64, Y = 64, Width = 64, Height = 64,
                    Encoding = ZrleRegionEncoding.FillRect,
                    Data = new byte[] { 0, 0, 250, 255 },  // 蓝色
                    DataLen = 4
                }
            };
            dec.Decode(ZrleRegionCodec.Pack(fill));

            byte[] redTile = new byte[64 * 64 * 4];
            for (int i = 0; i < redTile.Length; i += 4)
            {
                redTile[i] = 0;
                redTile[i + 1] = 0;
                redTile[i + 2] = 250;  // 红
                redTile[i + 3] = 255;
            }
            var both = new ZrleRegion[]
            {
                new ZrleRegion
                {
                    X = 64, Y = 0, Width = 64, Height = 64,
                    Encoding = ZrleRegionEncoding.CopyRect,
                    Data = new byte[] { 64, 0, 0, 0, 64, 0, 0, 0 },
                    DataLen = 8
                },
                new ZrleRegion
                {
                    X = 64, Y = 64, Width = 64, Height = 64,
                    Encoding = ZrleRegionEncoding.Raw,
                    Data = redTile,
                    DataLen = redTile.Length
                }
            };
            var result = dec.Decode(ZrleRegionCodec.Pack(both));
            Assert.Equal(DecodeStatus.Ok, result.Status);

            // 目标 (64,0) 应为蓝色（CopyRect 先处理，读到的是帧 1 的蓝色）
            int off = (0 * W + 64) * 4;
            Assert.Equal(0, result.Pixels[off]);      // B
            Assert.Equal(0, result.Pixels[off + 1]);  // G
            Assert.Equal(250, result.Pixels[off + 2]);  // R
            // 源 (64,64) 已被 Raw 覆盖为红色
            int offSrc = (64 * W + 64) * 4;
            Assert.Equal(250, result.Pixels[offSrc + 2]);
            dec.Dispose();
        }

        [Fact]
        public void Decode_ToOutputBuffer_ReturnsSameBuffer()
        {
            var dec = new ZrleDecoder();
            dec.Initialize(W, H);
            byte[] output = new byte[W * H * 4];

            var regions = new ZrleRegion[]
            {
                new ZrleRegion
                {
                    X = 0, Y = 0, Width = 1, Height = 1,
                    Encoding = ZrleRegionEncoding.FillRect,
                    Data = new byte[] { 1, 2, 3, 4 },
                    DataLen = 4
                }
            };
            var result = dec.Decode(ZrleRegionCodec.Pack(regions), output);
            Assert.Equal(DecodeStatus.Ok, result.Status);
            Assert.Same(output, result.Pixels);
            Assert.Equal(1, output[0]);
            Assert.Equal(2, output[1]);
            dec.Dispose();
        }

        [Fact]
        public void Decode_OutputBufferTooSmall_ReturnsFailed()
        {
            var dec = new ZrleDecoder();
            dec.Initialize(W, H);
            var result = dec.Decode(ZrleRegionCodec.Pack(new ZrleRegion[0]), new byte[100]);
            Assert.Equal(DecodeStatus.Failed, result.Status);
            dec.Dispose();
        }

        [Fact]
        public void Decode_MalformedData_ReturnsFailed()
        {
            var dec = new ZrleDecoder();
            dec.Initialize(W, H);
            var result = dec.Decode(new byte[] { 1, 0, 0, 0 });  // 声明 1 区域但无数据
            Assert.Equal(DecodeStatus.Failed, result.Status);
            dec.Dispose();
        }

        [Fact]
        public void Decode_NullData_ReturnsFailed()
        {
            var dec = new ZrleDecoder();
            dec.Initialize(W, H);
            Assert.Equal(DecodeStatus.Failed, dec.Decode(null).Status);
            dec.Dispose();
        }

        [Fact]
        public void Decode_RegionOutOfBounds_SkipsRegionNotWholeFrame()
        {
            var dec = new ZrleDecoder();
            dec.Initialize(W, H);

            // 一个合法区域 + 一个越界区域（X=1000 超出帧宽 192）
            var regions = new ZrleRegion[]
            {
                new ZrleRegion
                {
                    X = 0, Y = 0, Width = 1, Height = 1,
                    Encoding = ZrleRegionEncoding.FillRect,
                    Data = new byte[] { 1, 2, 3, 4 },
                    DataLen = 4
                },
                new ZrleRegion
                {
                    X = 1000, Y = 0, Width = 64, Height = 64,
                    Encoding = ZrleRegionEncoding.FillRect,
                    Data = new byte[] { 9, 9, 9, 9 },
                    DataLen = 4
                }
            };
            byte[] output = new byte[W * H * 4];
            var result = dec.Decode(ZrleRegionCodec.Pack(regions), output);
            // 整帧仍解码成功，越界区域被跳过
            Assert.Equal(DecodeStatus.Ok, result.Status);
            Assert.Equal(1, output[0]);  // 合法区域已应用
            dec.Dispose();
        }

        [Fact]
        public void Decode_CumulativeAcrossFrames()
        {
            var dec = new ZrleDecoder();
            dec.Initialize(W, H);
            byte[] output = new byte[W * H * 4];

            // 帧 1：在 (0,0) 写红色像素
            var r1 = new ZrleRegion[]
            {
                new ZrleRegion
                {
                    X = 0, Y = 0, Width = 1, Height = 1,
                    Encoding = ZrleRegionEncoding.FillRect,
                    Data = new byte[] { 0, 0, 250, 255 },
                    DataLen = 4
                }
            };
            dec.Decode(ZrleRegionCodec.Pack(r1), output);
            Assert.Equal(250, output[2]);

            // 帧 2：在 (100,50) 写绿色像素（帧 1 内容保留）
            var r2 = new ZrleRegion[]
            {
                new ZrleRegion
                {
                    X = 100, Y = 50, Width = 1, Height = 1,
                    Encoding = ZrleRegionEncoding.FillRect,
                    Data = new byte[] { 0, 250, 0, 255 },
                    DataLen = 4
                }
            };
            var result2 = dec.Decode(ZrleRegionCodec.Pack(r2), output);
            Assert.Equal(DecodeStatus.Ok, result2.Status);
            Assert.Equal(250, output[2]);  // 帧 1 红色保留
            int off = (50 * W + 100) * 4;
            Assert.Equal(250, output[off + 1]);  // 帧 2 绿色
            dec.Dispose();
        }
    }
}
