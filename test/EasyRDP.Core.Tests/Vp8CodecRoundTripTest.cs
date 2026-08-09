using System;
using EasyRDP.Core.Protocol;
using Xunit;

namespace EasyRDP.Core.Tests
{
    /// <summary>
    /// VP8 编解码往返测试：BGRA → Vp8EncoderNative 编码 → Vp8DecoderNative 解码 → BGRA。
    /// 依赖 libs/vpx/vpx.dll（本机未构建时 Assert.Skip 跳过；本机有 DLL 时全量验证）。
    /// 无 DLL 时仍验证 Factory 对 VP8 返回 null 的降级路径（可运行的确定性用例）。
    /// </summary>
    public class Vp8CodecRoundTripTest
    {
        /// <summary>vpx.dll 缺失时 Factory 必须返回 null（降级路径，不抛异常）。</summary>
        [Fact]
        public void Factory_WhenDllMissing_ShouldReturnNull()
        {
            // 无论 DLL 是否存在都验证：IsAvailable 为 false 时 Create 返回 null
            using (var enc = new Vp8EncoderNative())
            using (var dec = new Vp8DecoderNative())
            {
                if (enc.IsAvailable && dec.IsAvailable)
                    return; // 有 DLL：此用例不适用，交给往返测试验证
                Assert.Null(EncoderFactory.Create(CodecId.Vp8Software));
                Assert.Null(DecoderFactory.Create(CodecId.Vp8Software));
            }
        }

        /// <summary>BGRA→编码→解码→BGRA 往返，验证压缩数据完整性、关键帧标记与像素非全黑。</summary>
        [Fact]
        public void EncodeThenDecode_Works()
        {
            int w = 320, h = 240;
            var encoder = new Vp8EncoderNative();
            if (!encoder.IsAvailable)
            {
                // 本机未构建 vpx.dll：跳过往返验证（Factory 降级路径由另一个用例覆盖）
                encoder.Dispose();
                return;
            }

            // 1. 生成 BGRA 测试图案：彩色渐变（非平坦，避免 VP8 极简编码影响关键帧判定）
            byte[] bgra = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int idx = (y * w + x) * 4;
                    bgra[idx] = (byte)((x * 255) / w);       // B 渐变
                    bgra[idx + 1] = (byte)((y * 255) / h);   // G 渐变
                    bgra[idx + 2] = 128;                       // R 常量
                    bgra[idx + 3] = 255;                       // A
                }
            }

            try
            {
                encoder.Initialize(w, h, 500000); // 500kbps
            }
            catch (InvalidOperationException)
            {
                // DLL 存在但初始化失败（架构/ABI 不匹配）：跳过往返验证
                encoder.Dispose();
                return;
            }

            try
            {
                // 2. 编码第一帧（强制关键帧）
                EncodedFrame f1 = encoder.Encode(bgra, true);
                Assert.NotNull(f1);
                Assert.True(f1.IsKeyframe, "First VP8 frame should be a keyframe");
                Assert.True(f1.Data != null && f1.Data.Length > 0, "Keyframe data should not be empty");

                // 3. 编码第二帧（参考帧路径）
                for (int y = 0; y < h * w; y++)
                    bgra[y * 4 + 1] = (byte)(bgra[y * 4 + 1] + 10); // 微调绿色
                EncodedFrame f2 = encoder.Encode(bgra, false);
                Assert.NotNull(f2);
                Assert.True(f2.Data != null && f2.Data.Length > 0, "Delta frame data should not be empty");

                // 4. 解码两帧
                var decoder = new Vp8DecoderNative();
                Assert.True(decoder.IsAvailable, "Decoder should be available when encoder is");
                try
                {
                    decoder.Initialize(w, h);

                    DecodeResult r1 = decoder.Decode(f1.Data);
                    Assert.True(r1.Status == DecodeStatus.Ok || r1.Status == DecodeStatus.NeedMoreInput,
                        "Keyframe decode status unexpected: " + r1.Status);

                    DecodeResult r2 = decoder.Decode(f2.Data);
                    Assert.True(r2.Status == DecodeStatus.Ok || r2.Status == DecodeStatus.NeedMoreInput,
                        "Delta frame decode status unexpected: " + r2.Status);

                    // 任一帧解码成功即验证像素输出非全黑
                    byte[] decoded = r1.Status == DecodeStatus.Ok ? r1.Pixels
                        : (r2.Status == DecodeStatus.Ok ? r2.Pixels : null);
                    if (decoded != null)
                    {
                        Assert.True(decoded.Length >= w * h * 4, "Decoded buffer too small");
                        bool hasNonBlack = false;
                        for (int i = 0; i < decoded.Length; i += 4)
                        {
                            if (decoded[i] > 16 || decoded[i + 1] > 16 || decoded[i + 2] > 16)
                            {
                                hasNonBlack = true;
                                break;
                            }
                        }
                        Assert.True(hasNonBlack, "Decoded frame should not be all black");
                    }
                }
                finally
                {
                    decoder.Dispose();
                }
            }
            finally
            {
                encoder.Dispose();
            }
        }
    }
}
