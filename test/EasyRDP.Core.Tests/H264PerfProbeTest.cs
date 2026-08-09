using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using EasyRDP.Core.Protocol;

namespace EasyRDP.Core.Tests
{
    /// <summary>
    /// 性能探针：分解 1920x1080 下 BGRA→I420 转换耗时与 OpenH264 EncodeFrame 耗时，
    /// 定位 8-10 FPS 瓶颈（转换 or 编码）。
    /// </summary>
    public class H264PerfProbeTest
    {
        [Fact]
        public void Probe_1080p_ConvertVsEncodeTime()
        {
            int w = 1920, h = 1080;
            var bgra = new byte[w * h * 4];
            var rand = new Random(7);
            // 模拟屏幕内容：随机像素 + 局部文字区域，避免纯色帧（纯色编码太快失真）
            for (int i = 0; i < bgra.Length; i += 4)
            {
                bgra[i] = (byte)(128 + (i / 4) % 64);
                bgra[i + 1] = (byte)(120 + ((i / 4) / w) % 64);
                bgra[i + 2] = (byte)(130 + ((i / 4) / (w * 8)) % 64);
                bgra[i + 3] = 255;
            }

            var enc = new H264EncoderNative();
            Assert.True(enc.IsAvailable);
            enc.Initialize(w, h, 12000000);

            // 颜色转换已抽为 ColorConverter 公共方法（H264/VP8 共享）
            var mi = typeof(ColorConverter).GetMethod("BgraToI420",
                BindingFlags.Public | BindingFlags.Static);

            int ySize = w * h;
            int uvSize = ((w + 1) / 2) * ((h + 1) / 2);
            byte[] i420 = new byte[ySize + uvSize * 2];

            var hBgra = GCHandle.Alloc(bgra, GCHandleType.Pinned);
            var hI420 = GCHandle.Alloc(i420, GCHandleType.Pinned);
            IntPtr pBgra = hBgra.AddrOfPinnedObject();
            IntPtr pY = hI420.AddrOfPinnedObject();
            IntPtr pU = pY + ySize;
            IntPtr pV = pU + uvSize;
            object[] args = { pBgra, pY, pU, pV, w, h };

            // 预热
            mi.Invoke(null, args);

            const int N = 5;
            long convertTotal = 0;
            for (int i = 0; i < N; i++)
            {
                var sw = Stopwatch.StartNew();
                mi.Invoke(null, args);
                convertTotal += sw.ElapsedMilliseconds;
            }
            Console.WriteLine("[PerfProbe] BGRA->I420 convert: {0:F1} ms/frame (avg of {1})",
                convertTotal / (double)N, N);

            // 用编码器完整 Encode（含转换），对比
            long encodeTotal = 0;
            EncodedFrame first = default(EncodedFrame);
            int firstLen = -1;
            for (int i = 0; i < N; i++)
            {
                var sw = Stopwatch.StartNew();
                first = enc.Encode(bgra, i == 0);
                if (first.Data != null) firstLen = first.Data.Length;
                encodeTotal += sw.ElapsedMilliseconds;
            }
            Console.WriteLine("[PerfProbe] Full Encode (convert+openh264): {0:F1} ms/frame (avg of {1}), outLen={2}",
                encodeTotal / (double)N, N, firstLen);

            hBgra.Free();
            hI420.Free();
            enc.Dispose();
        }

        /// <summary>验证 SIMD 转换与标量公式结果一致（net8 目标走 SIMD 路径）。</summary>
        [Fact]
        public void Convert_Simd_MatchesScalarFormula()
        {
            int w = 1920, h = 1080;
            var bgra = new byte[w * h * 4];
            var rand = new Random(99);
            rand.NextBytes(bgra);
            for (int i = 3; i < bgra.Length; i += 4) bgra[i] = 255;

            var mi = typeof(ColorConverter).GetMethod("BgraToI420",
                BindingFlags.Public | BindingFlags.Static);

            int ySize = w * h;
            int uvSize = ((w + 1) / 2) * ((h + 1) / 2);
            byte[] i420 = new byte[ySize + uvSize * 2];

            var hBgra = GCHandle.Alloc(bgra, GCHandleType.Pinned);
            var hI420 = GCHandle.Alloc(i420, GCHandleType.Pinned);
            IntPtr pBgra = hBgra.AddrOfPinnedObject();
            IntPtr pY = hI420.AddrOfPinnedObject();
            IntPtr pU = pY + ySize;
            IntPtr pV = pU + uvSize;

            mi.Invoke(null, new object[] { pBgra, pY, pU, pV, w, h });

            // 抽样校验（每 16 像素一个 Y + 每 32 块一个 U/V）
            for (int p = 0; p < w * h; p += 16)
            {
                int r = Marshal.ReadByte(pBgra, p * 4 + 2);
                int g = Marshal.ReadByte(pBgra, p * 4 + 1);
                int b = Marshal.ReadByte(pBgra, p * 4);
                int expectedY = Clamp(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
                Assert.Equal(expectedY, Marshal.ReadByte(pY, p));
            }
            for (int c = 0; c < uvSize; c += 32)
            {
                // 每 2x2 块对应源像素 (j*2, i*2)
                int yy = (c / ((w + 1) / 2)) * 2;
                int xx = (c % ((w + 1) / 2)) * 2;
                int off = (yy * w + xx) * 4;
                int r = Marshal.ReadByte(pBgra, off + 2);
                int g = Marshal.ReadByte(pBgra, off + 1);
                int b = Marshal.ReadByte(pBgra, off);
                int expectedU = Clamp(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
                int expectedV = Clamp(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
                Assert.Equal(expectedU, Marshal.ReadByte(pU, c));
                Assert.Equal(expectedV, Marshal.ReadByte(pV, c));
            }

            hBgra.Free();
            hI420.Free();
        }

        private static int Clamp(int val)
        {
            return val < 0 ? 0 : (val > 255 ? 255 : val);
        }
    }
}
