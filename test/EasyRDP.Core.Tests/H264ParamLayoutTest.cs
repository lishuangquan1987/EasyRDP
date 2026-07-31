using System;
using System.Runtime.InteropServices;
using EasyRDP.Core.Protocol;

namespace EasyRDP.Core.Tests
{
    /// <summary>
    /// SEncParamExt 原生内存布局诊断：调用 GetDefaultParams 后按推算偏移读取默认值，
    /// 验证偏移映射与 OpenH264 2.6.0 实际布局一致，防止 InitializeExt 写坏字段。
    /// </summary>
    public class H264ParamLayoutTest
    {
        [Fact]
        public void GetDefaultParams_Offsets_ReadExpectedDefaults()
        {
            var enc = new H264EncoderNative();
            Assert.True(enc.IsAvailable, "Encoder DLL not found");

            var fld = typeof(H264EncoderNative).GetField("_encoder",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            IntPtr pEnc = (IntPtr)fld.GetValue(enc);

            var getDefault = H264Native.GetVTableDelegate<H264Native.GetDefaultParamsDelegate>(
                pEnc, H264Native.VTABLE_SLOT_GET_DEFAULT_PARAMS);

            IntPtr pParam = Marshal.AllocHGlobal(H264Native.SEncParamExtOffsets.AllocSize);
            try
            {
                for (int off = 0; off < H264Native.SEncParamExtOffsets.AllocSize; off += 8)
                    Marshal.WriteInt64(pParam, off, 0);

                int ret = getDefault(pEnc, pParam);
                Assert.Equal(0, ret);

                // 顶层默认值（据 OpenH264 FillDefault）
                int usage = Marshal.ReadInt32(pParam, H264Native.SEncParamExtOffsets.IUsageType);
                int rcMode = Marshal.ReadInt32(pParam, H264Native.SEncParamExtOffsets.IRCMode);
                int temporal = Marshal.ReadInt32(pParam, H264Native.SEncParamExtOffsets.ITemporalLayerNum);
                int spatial = Marshal.ReadInt32(pParam, H264Native.SEncParamExtOffsets.ISpatialLayerNum);
                int intra = Marshal.ReadInt32(pParam, H264Native.SEncParamExtOffsets.UiIntraPeriod);
                int maxQp = Marshal.ReadInt32(pParam, H264Native.SEncParamExtOffsets.IMaxQp);
                int minQp = Marshal.ReadInt32(pParam, H264Native.SEncParamExtOffsets.IMinQp);
                int entropy = Marshal.ReadInt32(pParam, H264Native.SEncParamExtOffsets.IEntropyCodingModeFlag);
                int frameSkip = Marshal.ReadByte(pParam, H264Native.SEncParamExtOffsets.BEnableFrameSkip);
                int sceneChange = Marshal.ReadByte(pParam, H264Native.SEncParamExtOffsets.BEnableSceneChangeDetect);
                int denoise = Marshal.ReadByte(pParam, H264Native.SEncParamExtOffsets.BEnableDenoise);
                int bgDetect = Marshal.ReadByte(pParam, H264Native.SEncParamExtOffsets.BEnableBackgroundDetection);
                int adaptiveQ = Marshal.ReadByte(pParam, H264Native.SEncParamExtOffsets.BEnableAdaptiveQuant);

                // 第 0 层默认值
                int layer0 = H264Native.SEncParamExtOffsets.SSpatialLayers;
                int dlayerQp = Marshal.ReadInt32(pParam, layer0 + H264Native.SSpatialLayerConfigOffsets.IDLayerQp);
                int sliceMode = Marshal.ReadInt32(pParam, layer0 + 32); // sSliceArgument.uiSliceMode
                int fullRange = Marshal.ReadByte(pParam, layer0 + H264Native.SSpatialLayerConfigOffsets.BFullRange);

                Console.WriteLine("[H264ParamLayoutTest] usage={0} rcMode={1} temporal={2} spatial={3} intra={4}",
                    usage, rcMode, temporal, spatial, intra);
                Console.WriteLine("[H264ParamLayoutTest] maxQp={0} minQp={1} entropy={2} frameSkip={3} sceneChange={4}",
                    maxQp, minQp, entropy, frameSkip, sceneChange);
                Console.WriteLine("[H264ParamLayoutTest] denoise={0} bgDetect={1} adaptiveQ={2}",
                    denoise, bgDetect, adaptiveQ);
                Console.WriteLine("[H264ParamLayoutTest] layer0: dLayerQp={0} sliceMode={1} fullRange={2}",
                    dlayerQp, sliceMode, fullRange);

                // 依据 FillDefault：iUsageType=CAMERA_VIDEO_REAL_TIME(0), iSpatialLayerNum=1, iTemporalLayerNum=1,
                // iRCMode=RC_QUALITY_MODE(0), uiIntraPeriod=0, bEnableSceneChangeDetect=true,
                // bEnableFrameSkip=true, iDLayerQp=26, uiSliceMode=SM_SINGLE_SLICE(0)
                Assert.Equal(0, usage);
                Assert.Equal(1, spatial);
                Assert.Equal(1, temporal);
                Assert.Equal(0, intra);
                Assert.Equal(1, sceneChange);
                Assert.Equal(1, frameSkip);
                Assert.Equal(26, dlayerQp);
                Assert.Equal(0, sliceMode);
                Assert.Equal(0, fullRange);
            }
            finally
            {
                Marshal.FreeHGlobal(pParam);
                enc.Dispose();
            }
        }
    }
}
