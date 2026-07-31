using System;
using System.Runtime.InteropServices;
using EasyRDP.Core.Protocol;
using Xunit;

namespace EasyRDP.Core.Tests
{
    /// <summary>
    /// 直接通过 vtable 调用 OpenH264 EncodeFrame，验证原生内存 + Marshal 读取路径。
    /// 此测试覆盖 H264EncoderNative.Encode 内部的核心调用，但不依赖该类的封装。
    /// </summary>
    public class H264DirectTest
    {
        [Fact]
        public void Direct_I420_Works()
        {
            var enc = new H264EncoderNative();
            Assert.True(enc.IsAvailable);
            enc.Initialize(320, 240, 500000);

            int w = 320, h = 240, y = w * h, uv = y / 4;
            var buf = new byte[y + uv * 2];
            for (int i = 0; i < buf.Length; i++) buf[i] = 128;

            var pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
            // 分配原生 SFrameBSInfo 内存（OpenH264 实际结构体 5144 字节，给 8KB 余量）
            IntPtr pBsInfo = Marshal.AllocHGlobal(H264Native.SFrameBSInfoAccess.AllocSize);
            try
            {
                // 清零缓冲区，避免脏数据干扰
                for (int off = 0; off < H264Native.SFrameBSInfoAccess.AllocSize; off += 8)
                    Marshal.WriteInt64(pBsInfo, off, 0);

                var pic = new H264Native.SSourcePicture();
                pic.iColorFormat = 23;
                pic.iStride0 = w;
                pic.iStride1 = w / 2;
                pic.iStride2 = w / 2;
                pic.pData0 = pin.AddrOfPinnedObject();
                pic.pData1 = pic.pData0 + y;
                pic.pData2 = pic.pData1 + uv;
                pic.iPicWidth = w;
                pic.iPicHeight = h;

                // 第一帧 OpenH264 默认就是 IDR，无需 ForceIntraFrame。
                // 如需强制 IDR，调用 vtable slot 6 = ForceIntraFrame(true, iLayerId=-1)。
                // 注意：必须显式传 iLayerId（默认值 -1），否则 R8 寄存器含垃圾值导致 AV。

                var fld = typeof(H264EncoderNative).GetField("_encoder",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                IntPtr pEnc = (IntPtr)fld!.GetValue(enc)!;

                var fn = H264Native.GetVTableDelegate<H264Native.EncodeFrameDelegate>(
                    pEnc, H264Native.VTABLE_SLOT_ENCODE_FRAME);
                int r = fn(pEnc, ref pic, pBsInfo);
                Assert.Equal(0, r);

                int layerNum = H264Native.SFrameBSInfoAccess.GetLayerNum(pBsInfo);
                Assert.True(layerNum > 0, "iLayerNum should be > 0 after successful encode");

                // 诊断：dump bitness 和 stride，验证 x86/x64 布局
                Console.WriteLine($"[H264DirectTest] bitness={IntPtr.Size * 8}, layerNum={layerNum}, stride={H264Native.SFrameBSInfoAccess.LayerInfoStride}");

                // OpenH264 2.6.0 不填充顶层 eFrameType / iFrameSizeInBytes（始终为 0），
                // 必须使用 per-layer 字段计算帧大小并判断帧类型。
                int layerCount = layerNum > 128 ? 128 : layerNum;

                // 逐层 dump 字段（诊断多层数据）
                for (int li = 0; li < layerCount; li++)
                {
                    int ft = H264Native.SFrameBSInfoAccess.GetLayerFrameType(pBsInfo, li);
                    int nc = H264Native.SFrameBSInfoAccess.GetLayerNalCount(pBsInfo, li);
                    IntPtr pnl = H264Native.SFrameBSInfoAccess.GetLayerNalLengthInByte(pBsInfo, li);
                    IntPtr pbb = H264Native.SFrameBSInfoAccess.GetLayerBsBuf(pBsInfo, li);
                    Console.WriteLine($"[H264DirectTest] layer[{li}]: frameType={ft} nalCount={nc} pNalLen=0x{pnl.ToInt64():X} pBsBuf=0x{pbb.ToInt64():X}");
                }

                int computedSize = H264Native.SFrameBSInfoAccess.ComputeTotalLayerBytes(pBsInfo, layerCount);
                Console.WriteLine($"[H264DirectTest] computedSize={computedSize}");

                Assert.True(computedSize > 0, "Computed total layer bytes should be > 0");

                // 找到第一个非空 pBsBuf 并拷贝数据
                IntPtr pBsBuf = IntPtr.Zero;
                for (int i = 0; i < layerCount; i++)
                {
                    pBsBuf = H264Native.SFrameBSInfoAccess.GetLayerBsBuf(pBsInfo, i);
                    if (pBsBuf != IntPtr.Zero) break;
                }
                Assert.NotEqual(IntPtr.Zero, pBsBuf);

                byte[] data = new byte[computedSize];
                Marshal.Copy(pBsBuf, data, 0, data.Length);
                Assert.True(data.Length > 0);

                bool isKey = H264Native.SFrameBSInfoAccess.IsKeyFrame(pBsInfo);
                Assert.True(isKey, "First frame should be IDR keyframe (layer0FrameType==1)");
            }
            finally
            {
                pin.Free();
                Marshal.FreeHGlobal(pBsInfo);
                enc.Dispose();
            }
        }
    }
}
