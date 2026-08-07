using System;
using EasyRDP.Core.Protocol;
using Xunit;

namespace EasyRDP.Core.Tests.Protocol
{
    /// <summary>
    /// ZrleEncoder 测试：首帧/静态帧/单瓦片变化/FillRect/CopyRect/往返一致性。
    /// </summary>
    public class ZrleEncoderTests
    {
        private const int W = 192;
        private const int H = 128;  // 192×128 → 3×2=6 瓦片

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

        private static void FillRect(byte[] pixels, int w, int x, int y, int rw, int rh, byte b, byte g, byte r)
        {
            for (int yy = y; yy < y + rh; yy++)
            {
                for (int xx = x; xx < x + rw; xx++)
                {
                    int off = (yy * w + xx) * 4;
                    pixels[off] = b;
                    pixels[off + 1] = g;
                    pixels[off + 2] = r;
                    pixels[off + 3] = 255;
                }
            }
        }

        [Fact]
        public void FirstFrame_EncodesAllTiles()
        {
            var enc = new ZrleEncoder();
            enc.Initialize(W, H, 1000000);
            byte[] frame = CreateFrame(W, H, 10, 20, 30);

            var result = enc.Encode(frame, false);

            Assert.NotNull(result.Data);
            Assert.False(result.IsKeyframe);  // ZRLE 始终非关键帧
            var rects = ZrleRegionCodec.ExtractRects(result.Data);
            Assert.NotNull(rects);
            Assert.Equal(6, rects.Length);  // 3×2 瓦片全编码
            enc.Dispose();
        }

        [Fact]
        public void StaticFrame_NoRegions()
        {
            var enc = new ZrleEncoder();
            enc.Initialize(W, H, 1000000);
            byte[] frame = CreateFrame(W, H, 10, 20, 30);

            enc.Encode(frame, false);  // 首帧
            var result = enc.Encode(frame, false);  // 同帧 → 无变化

            var rects = ZrleRegionCodec.ExtractRects(result.Data);
            Assert.NotNull(rects);
            Assert.Equal(0, rects.Length);
            enc.Dispose();
        }

        [Fact]
        public void SingleTileChange_ProducesOneRegion()
        {
            var enc = new ZrleEncoder();
            enc.Initialize(W, H, 1000000);
            byte[] frame = CreateFrame(W, H, 10, 20, 30);

            enc.Encode(frame, false);
            // 修改第 1 行第 1 列瓦片（x∈[64,128), y∈[0,64)）内的一个像素
            FillRect(frame, W, 70, 10, 1, 1, 200, 100, 50);
            var result = enc.Encode(frame, false);

            var rects = ZrleRegionCodec.ExtractRects(result.Data);
            Assert.NotNull(rects);
            Assert.Equal(1, rects.Length);
            Assert.Equal(64, rects[0].X);
            Assert.Equal(0, rects[0].Y);
            enc.Dispose();
        }

        [Fact]
        public void IsKeyframe_AlwaysFalse()
        {
            var enc = new ZrleEncoder();
            enc.Initialize(W, H, 1000000);
            byte[] frame = CreateFrame(W, H, 10, 20, 30);

            var r1 = enc.Encode(frame, true);   // forceKeyframe=true 也被忽略
            var r2 = enc.Encode(frame, false);
            Assert.False(r1.IsKeyframe);
            Assert.False(r2.IsKeyframe);
            enc.Dispose();
        }

        [Fact]
        public void EstimateChangeRatio_StaticZero_FullOne()
        {
            var enc = new ZrleEncoder();
            enc.Initialize(W, H, 1000000);
            byte[] frame = CreateFrame(W, H, 10, 20, 30);

            enc.Encode(frame, false);
            Assert.Equal(0f, enc.EstimateChangeRatio(frame));
            byte[] changed = CreateFrame(W, H, 99, 99, 99);
            Assert.Equal(1f, enc.EstimateChangeRatio(changed));
            enc.Dispose();
        }

        [Fact]
        public void FillRect_SolidTileUsesFillRectEncoding()
        {
            var enc = new ZrleEncoder();
            enc.Initialize(W, H, 1000000);
            byte[] frame = CreateFrame(W, H, 10, 20, 30);

            enc.Encode(frame, false);
            // 修改整个 (1,0) 瓦片为另一种纯色 → FillRect
            FillRect(frame, W, 64, 0, 64, 64, 200, 100, 50);
            var result = enc.Encode(frame, false);

            var regions = ZrleRegionCodec.Unpack(result.Data);
            Assert.NotNull(regions);
            Assert.Equal(1, regions.Length);
            Assert.Equal(ZrleRegionEncoding.FillRect, regions[0].Encoding);
            Assert.Equal(4, regions[0].DataLen);
            Assert.Equal(200, regions[0].Data[0]);  // B
            Assert.Equal(100, regions[0].Data[1]);  // G
            Assert.Equal(50, regions[0].Data[2]);   // R
            enc.Dispose();
        }

        [Fact]
        public void CopyRect_ShiftedTileUsesCopyRectEncoding()
        {
            var enc = new ZrleEncoder();
            enc.Initialize(W, H, 1000000);
            enc.SetMouseButtonDown(true);  // 仅鼠标按下时启用 CopyRect 搜索

            byte[] frameA = CreateFrame(W, H, 10, 20, 30);
            // 在 (64,64) 放一个 64×64 纯色方块
            FillRect(frameA, W, 64, 64, 64, 64, 200, 100, 50);
            enc.Encode(frameA, false);

            // 方块右移 8 像素：内容平移（64→72）
            byte[] frameB = CreateFrame(W, H, 10, 20, 30);
            FillRect(frameB, W, 72, 64, 64, 64, 200, 100, 50);
            var result = enc.Encode(frameB, false);

            var regions = ZrleRegionCodec.Unpack(result.Data);
            Assert.NotNull(regions);
            // 至少一个 CopyRect 区域（锚点瓦片 (72→64 偏移 -8)）
            bool foundCopyRect = false;
            for (int i = 0; i < regions.Length; i++)
            {
                if (regions[i].Encoding == ZrleRegionEncoding.CopyRect)
                {
                    foundCopyRect = true;
                    int srcX = BitConverter.ToInt32(regions[i].Data, 0);
                    int srcY = BitConverter.ToInt32(regions[i].Data, 4);
                    Assert.Equal(regions[i].X - 8, srcX);  // 源 = 目标左移 8
                    Assert.Equal(regions[i].Y, srcY);
                }
            }
            Assert.True(foundCopyRect, "expected at least one CopyRect region for shifted content");
            enc.Dispose();
        }

        [Fact]
        public void CopyRect_NotTriggeredWhenMouseUp()
        {
            var enc = new ZrleEncoder();
            enc.Initialize(W, H, 1000000);
            // 鼠标未按下：CopyRect 不触发
            byte[] frameA = CreateFrame(W, H, 10, 20, 30);
            FillRect(frameA, W, 64, 64, 64, 64, 200, 100, 50);
            enc.Encode(frameA, false);

            byte[] frameB = CreateFrame(W, H, 10, 20, 30);
            FillRect(frameB, W, 72, 64, 64, 64, 200, 100, 50);
            var result = enc.Encode(frameB, false);

            var regions = ZrleRegionCodec.Unpack(result.Data);
            Assert.NotNull(regions);
            for (int i = 0; i < regions.Length; i++)
            {
                Assert.NotEqual(ZrleRegionEncoding.CopyRect, regions[i].Encoding);
            }
            enc.Dispose();
        }

        [Fact]
        public void RoundTrip_EncodeDecode_PixelsMatch()
        {
            var enc = new ZrleEncoder();
            enc.Initialize(W, H, 1000000);
            var dec = new ZrleDecoder();
            dec.Initialize(W, H);

            byte[] frame = CreateFrame(W, H, 5, 6, 7);
            // 添加确定性"随机"内容：简单的正弦图案（保证非纯色瓦片走 Deflate）
            var rng = new Random(42);
            for (int i = 0; i < frame.Length; i += 4)
            {
                frame[i] = (byte)rng.Next(256);
                frame[i + 1] = (byte)rng.Next(256);
                frame[i + 2] = (byte)rng.Next(256);
                frame[i + 3] = 255;
            }

            var r1 = enc.Encode(frame, false);
            var d1 = dec.Decode(r1.Data, new byte[W * H * 4]);
            Assert.Equal(DecodeStatus.Ok, d1.Status);
            Assert.Equal(frame, d1.Pixels);

            // 第二帧：修改一小块再往返
            byte[] frame2 = (byte[])frame.Clone();
            FillRect(frame2, W, 100, 50, 30, 20, 250, 0, 0);
            var r2 = enc.Encode(frame2, false);
            byte[] outBuf = new byte[W * H * 4];
            var d2 = dec.Decode(r2.Data, outBuf);
            Assert.Equal(DecodeStatus.Ok, d2.Status);
            Assert.Equal(frame2, d2.Pixels);

            enc.Dispose();
            dec.Dispose();
        }

        [Fact]
        public void NonAlignedResolution_EncodesAndRoundTrips()
        {
            int w = 100, h = 80;  // 非 64 的倍数
            var enc = new ZrleEncoder();
            enc.Initialize(w, h, 1000000);
            var dec = new ZrleDecoder();
            dec.Initialize(w, h);

            byte[] frame = CreateFrame(w, h, 10, 20, 30);
            var rng = new Random(7);
            for (int i = 0; i < frame.Length; i += 4)
            {
                frame[i] = (byte)rng.Next(256);
                frame[i + 1] = (byte)rng.Next(256);
                frame[i + 2] = (byte)rng.Next(256);
                frame[i + 3] = 255;
            }

            var r1 = enc.Encode(frame, false);
            var d1 = dec.Decode(r1.Data, new byte[w * h * 4]);
            Assert.Equal(DecodeStatus.Ok, d1.Status);
            Assert.Equal(frame, d1.Pixels);
            enc.Dispose();
            dec.Dispose();
        }

        [Fact]
        public void Reset_ForcesFullFrameAgain()
        {
            var enc = new ZrleEncoder();
            enc.Initialize(W, H, 1000000);
            byte[] frame = CreateFrame(W, H, 10, 20, 30);

            enc.Encode(frame, false);
            var r2 = enc.Encode(frame, false);
            Assert.Equal(0, ZrleRegionCodec.ExtractRects(r2.Data).Length);

            enc.Reset();
            enc.Initialize(W, H, 1000000);
            var r3 = enc.Encode(frame, false);
            Assert.Equal(6, ZrleRegionCodec.ExtractRects(r3.Data).Length);
            enc.Dispose();
        }

        [Fact]
        public void FillRect_MultipleSolidTiles_DistinctColors()
        {
            // 回归：一帧内多个不同颜色的纯色瓦片必须各自保持颜色
            // （共享缓冲会全部变成最后一个瓦片的颜色 → 画面串号）
            var enc = new ZrleEncoder();
            enc.Initialize(W, H, 1000000);
            byte[] frame = CreateFrame(W, H, 10, 20, 30);

            enc.Encode(frame, false);
            // 修改两个瓦片为不同纯色：(0,0) 红色、(1,0) 绿色
            FillRect(frame, W, 0, 0, 64, 64, 0, 0, 250);     // 红
            FillRect(frame, W, 64, 0, 64, 64, 0, 250, 0);    // 绿
            var result = enc.Encode(frame, false);

            var regions = ZrleRegionCodec.Unpack(result.Data);
            Assert.NotNull(regions);
            Assert.Equal(2, regions.Length);
            // 按 X 排序断言各自颜色
            ZrleRegion r0 = regions[0].X == 0 ? regions[0] : regions[1];
            ZrleRegion r1 = regions[0].X == 0 ? regions[1] : regions[0];
            Assert.Equal(ZrleRegionEncoding.FillRect, r0.Encoding);
            Assert.Equal(ZrleRegionEncoding.FillRect, r1.Encoding);
            Assert.Equal(250, r0.Data[2]);  // 红色 R=250
            Assert.Equal(250, r1.Data[1]);  // 绿色 G=250
            enc.Dispose();
        }

        [Fact]
        public void Encode_NotInitialized_ReturnsEmptyFrame()
        {
            var enc = new ZrleEncoder();
            var result = enc.Encode(new byte[16], false);
            Assert.Null(result.Data);
            enc.Dispose();
        }
    }
}
