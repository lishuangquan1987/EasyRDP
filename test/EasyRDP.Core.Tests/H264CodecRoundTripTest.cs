using System;
using System.Runtime.InteropServices;
using EasyRDP.Core.Protocol;
using Xunit;

namespace EasyRDP.Core.Tests
{
    /// <summary>
    /// 编解码交叉测试：编码一帧 → 解码 → 验证。
    /// 在 x86 和 x64 下都运行，验证结构体布局在两种架构下都正确。
    /// </summary>
    public class H264CodecRoundTripTest
    {
        /// <summary>
        /// 编码一帧 I420 数据，然后用解码器解码，验证完整流程。
        /// 此测试覆盖 4 种交叉情况中的同架构部分（x86→x86, x64→x64）。
        /// H264 码流是架构无关的，所以同架构测试通过即保证交叉架构也兼容。
        /// 解码器输出 I420，本测试额外验证 I420→BGRA 转换后像素非全黑（避免回归）。
        /// </summary>
        [Fact]
        public void EncodeThenDecode_Works()
        {
            int w = 320, h = 240;
            int ySize = w * h;
            int uvSize = ySize / 4;
            int totalSize = ySize + uvSize * 2;

            // 1. 编码一帧 I420 数据
            var encoder = new H264EncoderNative();
            Assert.True(encoder.IsAvailable, "Encoder should be available");
            encoder.Initialize(w, h, 500000);

            var i420 = new byte[totalSize];
            // Y: limited range 渐变 16..235（模拟服务端 ConvertBgraToI420 的 +16 偏移输出）
            // 服务端公式：Y = (66*R + 129*G + 25*B + 128) >> 8 + 16，范围 16-235
            for (int i = 0; i < ySize; i++) i420[i] = (byte)(16 + (i % 220));  // Y: 16..235
            for (int i = ySize; i < totalSize; i++) i420[i] = 128;              // U/V: 中性灰

            var i420Pin = GCHandle.Alloc(i420, GCHandleType.Pinned);
            IntPtr pBsInfo = Marshal.AllocHGlobal(H264Native.SFrameBSInfoAccess.AllocSize);
            try
            {
                for (int off = 0; off < H264Native.SFrameBSInfoAccess.AllocSize; off += 8)
                    Marshal.WriteInt64(pBsInfo, off, 0);

                var pic = new H264Native.SSourcePicture();
                pic.iColorFormat = H264Native.ENCODER_FORMAT_I420;
                pic.iStride0 = w;
                pic.iStride1 = w / 2;
                pic.iStride2 = w / 2;
                pic.pData0 = i420Pin.AddrOfPinnedObject();
                pic.pData1 = pic.pData0 + ySize;
                pic.pData2 = pic.pData1 + uvSize;
                pic.iPicWidth = w;
                pic.iPicHeight = h;

                var fld = typeof(H264EncoderNative).GetField("_encoder",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                IntPtr pEnc = (IntPtr)fld!.GetValue(encoder)!;

                var encodeFn = H264Native.GetVTableDelegate<H264Native.EncodeFrameDelegate>(
                    pEnc, H264Native.VTABLE_SLOT_ENCODE_FRAME);
                int ret = encodeFn(pEnc, ref pic, pBsInfo);
                Assert.Equal(0, ret);

                int layerNum = H264Native.SFrameBSInfoAccess.GetLayerNum(pBsInfo);
                Assert.True(layerNum > 0, "iLayerNum should be > 0");

                int layerCount = layerNum > 128 ? 128 : layerNum;
                int encodedSize = H264Native.SFrameBSInfoAccess.ComputeTotalLayerBytes(pBsInfo, layerCount);
                Assert.True(encodedSize > 0, "Encoded size should be > 0");

                // 收集所有层的码流数据（多层 NAL 拼接）
                byte[] h264Data = new byte[encodedSize];
                int offset = 0;
                for (int li = 0; li < layerCount; li++)
                {
                    int nals = H264Native.SFrameBSInfoAccess.GetLayerNalCount(pBsInfo, li);
                    IntPtr pBsBuf = H264Native.SFrameBSInfoAccess.GetLayerBsBuf(pBsInfo, li);
                    IntPtr pNalLen = H264Native.SFrameBSInfoAccess.GetLayerNalLengthInByte(pBsInfo, li);
                    if (nals <= 0 || pBsBuf == IntPtr.Zero) continue;
                    for (int n = 0; n < nals; n++)
                    {
                        int nalLen = Marshal.ReadInt32(pNalLen, n * 4);
                        if (nalLen <= 0) continue;
                        Marshal.Copy(pBsBuf, h264Data, offset, nalLen);
                        offset += nalLen;
                        pBsBuf += nalLen;
                    }
                }
                Assert.Equal(encodedSize, offset);

                Console.WriteLine($"[RoundTrip] bitness={IntPtr.Size * 8}, encoded={encodedSize} bytes, layerNum={layerNum}");

                // 2. 用解码器解码（通过 H264DecoderNative.Decode API，内部已处理 I420→BGRA 转换）
                encoder.Dispose();
                encoder = null!;

                var decoder = new H264DecoderNative();
                Assert.True(decoder.IsAvailable, "Decoder should be available");
                decoder.Initialize(w, h);

                // DecodeFrameNoDelay 可能需要多次调用才能输出（缓冲一帧）
                DecodeResult result = new DecodeResult { Status = DecodeStatus.NeedMoreInput };
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    byte[] input = attempt == 0 ? h264Data : Array.Empty<byte>();
                    result = decoder.Decode(input);
                    Console.WriteLine($"[RoundTrip] Decode attempt {attempt + 1}: status={result.Status}");
                    if (result.Status == DecodeStatus.Ok) break;
                }

                Assert.Equal(DecodeStatus.Ok, result.Status);
                Assert.NotNull(result.Pixels);
                Assert.True(result.Pixels!.Length == w * h * 4,
                    $"Decoded pixels length should be {w * h * 4}, got {result.Pixels.Length}");

                // 验证非全黑：原始 Y limited range 渐变 (16..235) + U/V=128（中性灰），
                // 解码后 BGRA 应该有非零像素。统计非零像素数量。
                // limited range 公式：Y=16 → R=0（黑），Y=235 → R=254（接近白）
                int nonZeroCount = 0;
                int nonWhiteCount = 0;
                int blackCount = 0;     // Y=16 → R=0 的像素
                int nearWhiteCount = 0; // Y>=230 → R>=240 的像素
                byte[] pixels = result.Pixels;
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    // BGRA: 检查任意 B/G/R 通道非零（A 固定 255）
                    byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                    if (b != 0 || g != 0 || r != 0) nonZeroCount++;
                    // 检查非全白（防止 >> 8 缺失 bug 回归：该 bug 会让所有 Y>=1 的像素变白）
                    if (b < 250 || g < 250 || r < 250) nonWhiteCount++;
                    // limited range 转换正确性：Y=16 应该 → R=0（黑色）
                    if (b == 0 && g == 0 && r == 0) blackCount++;
                    // Y=235 应该 → R=254（接近白色，但不是 255）
                    if (b >= 240 && g >= 240 && r >= 240) nearWhiteCount++;
                }
                Console.WriteLine($"[RoundTrip] nonZero={nonZeroCount}/{w * h}, nonWhite={nonWhiteCount}/{w * h}, black={blackCount}, nearWhite={nearWhiteCount}");
                Assert.True(nonZeroCount > 0,
                    "Decoded frame should have non-zero pixels (limited range Y gradient 16..235 should produce color)");
                Assert.True(nonWhiteCount > w * h / 2,
                    "Decoded frame should have >50% non-white pixels (Y gradient 16..235 should produce varied gray levels, not all white)");
                // limited range 转换应该产生黑色像素（Y=16 → R=0）
                Assert.True(blackCount > 0,
                    "Decoded frame should have black pixels (Y=16 in limited range should map to R=0)");

                decoder.Dispose();
            }
            finally
            {
                i420Pin.Free();
                Marshal.FreeHGlobal(pBsInfo);
                encoder?.Dispose();
            }
        }
    }
}
