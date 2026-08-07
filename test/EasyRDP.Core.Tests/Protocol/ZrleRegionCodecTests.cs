using EasyRDP.Core.Protocol;
using Xunit;

namespace EasyRDP.Core.Tests.Protocol
{
    /// <summary>
    /// ZrleRegionCodec 打包/解包测试：格式往返、DataLen 池化语义、恶意数据防护。
    /// </summary>
    public class ZrleRegionCodecTests
    {
        [Fact]
        public void PackUnpack_RoundTrip_MixedEncodings()
        {
            // 构造 4 种编码类型的区域（模拟编码器输出）
            var regions = new ZrleRegion[]
            {
                new ZrleRegion
                {
                    X = 0, Y = 0, Width = 64, Height = 64,
                    Encoding = ZrleRegionEncoding.Deflate,
                    Data = new byte[] { 1, 2, 3, 4, 5 },
                    DataLen = 5
                },
                new ZrleRegion
                {
                    X = 64, Y = 0, Width = 64, Height = 64,
                    Encoding = ZrleRegionEncoding.FillRect,
                    Data = new byte[] { 10, 20, 30, 255 },
                    DataLen = 4
                },
                new ZrleRegion
                {
                    X = 0, Y = 64, Width = 32, Height = 32,
                    Encoding = ZrleRegionEncoding.Raw,
                    Data = new byte[32 * 32 * 4],
                    DataLen = 32 * 32 * 4
                },
                new ZrleRegion
                {
                    X = 64, Y = 64, Width = 64, Height = 64,
                    Encoding = ZrleRegionEncoding.CopyRect,
                    Data = new byte[] { 50, 0, 0, 0, 100, 0, 0, 0 },
                    DataLen = 8
                }
            };

            byte[] packed = ZrleRegionCodec.Pack(regions);
            ZrleRegion[] unpacked = ZrleRegionCodec.Unpack(packed);

            Assert.NotNull(unpacked);
            Assert.Equal(4, unpacked.Length);
            for (int i = 0; i < 4; i++)
            {
                Assert.Equal(regions[i].X, unpacked[i].X);
                Assert.Equal(regions[i].Y, unpacked[i].Y);
                Assert.Equal(regions[i].Width, unpacked[i].Width);
                Assert.Equal(regions[i].Height, unpacked[i].Height);
                Assert.Equal(regions[i].Encoding, unpacked[i].Encoding);
                Assert.Equal(regions[i].DataLen, unpacked[i].DataLen);
                Assert.Equal(regions[i].Data, unpacked[i].Data);
            }
        }

        [Fact]
        public void Pack_DataLenShorterThanData_OnlyPacksDataLenBytes()
        {
            // 池化语义：Data.Length=100 但 DataLen=5，只打包前 5 字节
            var regions = new ZrleRegion[]
            {
                new ZrleRegion
                {
                    X = 0, Y = 0, Width = 1, Height = 1,
                    Encoding = ZrleRegionEncoding.Raw,
                    Data = new byte[100],
                    DataLen = 4
                }
            };
            for (int i = 0; i < 4; i++) regions[0].Data[i] = (byte)(i + 7);

            byte[] packed = ZrleRegionCodec.Pack(regions);
            // 4(数量) + 21(头) + 4(数据)
            Assert.Equal(4 + 21 + 4, packed.Length);

            ZrleRegion[] unpacked = ZrleRegionCodec.Unpack(packed);
            Assert.NotNull(unpacked);
            Assert.Equal(1, unpacked.Length);
            Assert.Equal(4, unpacked[0].DataLen);
            Assert.Equal(4, unpacked[0].Data.Length);
            Assert.Equal(7, unpacked[0].Data[0]);
            Assert.Equal(10, unpacked[0].Data[3]);
        }

        [Fact]
        public void Pack_EmptyRegions_ProducesZeroCountPayload()
        {
            byte[] packed = ZrleRegionCodec.Pack(new ZrleRegion[0]);
            Assert.Equal(4, packed.Length);
            ZrleRegion[] unpacked = ZrleRegionCodec.Unpack(packed);
            Assert.NotNull(unpacked);
            Assert.Equal(0, unpacked.Length);
        }

        [Fact]
        public void Pack_CountLessThanArrayLength_PacksOnlyFirstCount()
        {
            var regions = new ZrleRegion[]
            {
                new ZrleRegion
                {
                    X = 1, Y = 2, Width = 3, Height = 4,
                    Encoding = ZrleRegionEncoding.FillRect,
                    Data = new byte[] { 1, 2, 3, 4 },
                    DataLen = 4
                },
                new ZrleRegion
                {
                    X = 9, Y = 9, Width = 9, Height = 9,
                    Encoding = ZrleRegionEncoding.FillRect,
                    Data = new byte[] { 5, 6, 7, 8 },
                    DataLen = 4
                }
            };

            byte[] packed = ZrleRegionCodec.Pack(regions, 1);
            ZrleRegion[] unpacked = ZrleRegionCodec.Unpack(packed);
            Assert.NotNull(unpacked);
            Assert.Equal(1, unpacked.Length);
            Assert.Equal(1, unpacked[0].X);
        }

        [Fact]
        public void Unpack_NullData_ReturnsNull()
        {
            Assert.Null(ZrleRegionCodec.Unpack(null));
        }

        [Fact]
        public void Unpack_TooShortData_ReturnsNull()
        {
            Assert.Null(ZrleRegionCodec.Unpack(new byte[] { 1, 0, 0, 0 })); // 声明 1 区域但无头
        }

        [Fact]
        public void Unpack_ExcessiveRegionCount_ReturnsNull()
        {
            // 声明 MaxRegionCount+1 个区域（1025 = 0x401）
            byte[] data = new byte[4];
            int badCount = ZrleRegionCodec.MaxRegionCount + 1;
            data[0] = (byte)badCount;
            data[1] = (byte)(badCount >> 8);
            data[2] = (byte)(badCount >> 16);
            data[3] = (byte)(badCount >> 24);
            Assert.Null(ZrleRegionCodec.Unpack(data));
        }

        [Fact]
        public void Unpack_TruncatedData_ReturnsNull()
        {
            // 构造 2 个 Raw 区域（每区域 64×64×4=16KB），截断数据
            var regions = new ZrleRegion[]
            {
                new ZrleRegion
                {
                    X = 0, Y = 0, Width = 64, Height = 64,
                    Encoding = ZrleRegionEncoding.Raw,
                    Data = new byte[64 * 64 * 4],
                    DataLen = 64 * 64 * 4
                },
                new ZrleRegion
                {
                    X = 64, Y = 0, Width = 64, Height = 64,
                    Encoding = ZrleRegionEncoding.Raw,
                    Data = new byte[64 * 64 * 4],
                    DataLen = 64 * 64 * 4
                }
            };
            byte[] packed = ZrleRegionCodec.Pack(regions);
            byte[] truncated = new byte[packed.Length - 100];
            System.Buffer.BlockCopy(packed, 0, truncated, 0, truncated.Length);
            Assert.Null(ZrleRegionCodec.Unpack(truncated));
        }

        [Fact]
        public void Unpack_InvalidRawDataLen_ReturnsNull()
        {
            // Raw 区域 DataLen 必须 = W×H×4；构造错误 DataLen
            var regions = new ZrleRegion[]
            {
                new ZrleRegion
                {
                    X = 0, Y = 0, Width = 64, Height = 64,
                    Encoding = ZrleRegionEncoding.Raw,
                    Data = new byte[100],
                    DataLen = 100  // 错误：应为 16384
                }
            };
            byte[] packed = ZrleRegionCodec.Pack(regions);
            Assert.Null(ZrleRegionCodec.Unpack(packed));
        }

        [Fact]
        public void ExtractRects_ReturnsRectanglesWithoutData()
        {
            var regions = new ZrleRegion[]
            {
                new ZrleRegion
                {
                    X = 10, Y = 20, Width = 64, Height = 64,
                    Encoding = ZrleRegionEncoding.Deflate,
                    Data = new byte[] { 1, 2, 3 },
                    DataLen = 3
                },
                new ZrleRegion
                {
                    X = 130, Y = 20, Width = 64, Height = 64,
                    Encoding = ZrleRegionEncoding.FillRect,
                    Data = new byte[] { 1, 2, 3, 4 },
                    DataLen = 4
                }
            };
            byte[] packed = ZrleRegionCodec.Pack(regions);

            var rects = ZrleRegionCodec.ExtractRects(packed);
            Assert.NotNull(rects);
            Assert.Equal(2, rects.Length);
            Assert.Equal(10, rects[0].X);
            Assert.Equal(20, rects[0].Y);
            Assert.Equal(64, rects[0].Width);
            Assert.Equal(130, rects[1].X);
        }

        [Fact]
        public void Unpack_RegionExceedingTileSize_ReturnsNull()
        {
            // 区域几何上限 64（ZRLE 瓦片边长）：超限视为恶意/损坏数据拒绝
            var regions = new ZrleRegion[]
            {
                new ZrleRegion
                {
                    X = 0, Y = 0, Width = 65, Height = 64,
                    Encoding = ZrleRegionEncoding.Raw,
                    Data = new byte[65 * 64 * 4],
                    DataLen = 65 * 64 * 4
                }
            };
            byte[] packed = ZrleRegionCodec.Pack(regions);
            Assert.Null(ZrleRegionCodec.Unpack(packed));
            Assert.Null(ZrleRegionCodec.ExtractRects(packed));
        }

        [Fact]
        public void ExtractRects_Malformed_ReturnsNull()
        {
            Assert.Null(ZrleRegionCodec.ExtractRects(new byte[] { 5, 0, 0, 0 }));
        }
    }
}
