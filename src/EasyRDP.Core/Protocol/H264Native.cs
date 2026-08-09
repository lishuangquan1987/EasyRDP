using System;
using System.Runtime.InteropServices;

namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// OpenH264 原生 API P/Invoke 声明和结构体。
    /// 4 个原生方法根据 IntPtr.Size 自动分发到 x86 或 x64 实现。
    /// </summary>
    internal static class H264Native
    {
        // 工厂选择：进程启动时确定架构，后续调用走对应实现
        private static readonly bool Is64Bit = IntPtr.Size == 8;

        // ====== 编码器 ======

        internal static int WelsCreateSVCEncoder(out IntPtr ppEncoder)
        {
            return Is64Bit
                ? H264NativeArchX64.WelsCreateSVCEncoder(out ppEncoder)
                : H264NativeArchX86.WelsCreateSVCEncoder(out ppEncoder);
        }

        internal static void WelsDestroySVCEncoder(IntPtr pEncoder)
        {
            if (Is64Bit)
                H264NativeArchX64.WelsDestroySVCEncoder(pEncoder);
            else
                H264NativeArchX86.WelsDestroySVCEncoder(pEncoder);
        }

        // ====== 解码器 ======

        internal static int WelsCreateDecoder(out IntPtr ppDecoder)
        {
            return Is64Bit
                ? H264NativeArchX64.WelsCreateDecoder(out ppDecoder)
                : H264NativeArchX86.WelsCreateDecoder(out ppDecoder);
        }

        internal static void WelsDestroyDecoder(IntPtr pDecoder)
        {
            if (Is64Bit)
                H264NativeArchX64.WelsDestroyDecoder(pDecoder);
            else
                H264NativeArchX86.WelsDestroyDecoder(pDecoder);
        }

        // ====== 编码器参数 ======

        // EUsageType 枚举（openh264 codec_app_def.h）：
        //   CAMERA_VIDEO_REAL_TIME = 0, SCREEN_CONTENT_REAL_TIME = 1, CAMERA_VIDEO_NON_REAL_TIME = 2
        // 头文件注释 "1.CAMERA... 2.SCREEN..." 是文档编号不是枚举值，
        // 历史实现误把 SCREEN_CONTENT_REAL_TIME 写成 2（实际是 CAMERA_VIDEO_NON_REAL_TIME），
        // 导致屏幕内容模式从未真正生效（SEncParamBase 路径实际一直以 SCREEN=1 运行）。
        internal const int VIDEO_REAL_TIME = 0;
        internal const int SCREEN_CONTENT_REAL_TIME = 1;
        internal const int RC_QUALITY_MODE = 0;
        internal const int RC_BITRATE_MODE = 1;
        internal const int PROFILE_BASELINE = 66;
        internal const int PROFILE_MAIN = 77;
        internal const int VIDEO_FORMAT_BGRA = 23;       // 解码器输出颜色格式
        internal const int ENCODER_FORMAT_BGRA = 6;     // 编码器 BGRA（通常不被 encoder 支持）
        internal const int ENCODER_FORMAT_I420 = 23;     // 编码器 I420 — OpenH264 中 videoFormatI420 = 23
        internal const int FRAME_TYPE_IDR = 1;

        // ECOMPLEXITY_MODE 枚举（openh264 codec_app_def.h）：
        //   LOW_COMPLEXITY = 0   最快速度、最低复杂度（屏幕内容首选）
        //   MEDIUM_COMPLEXITY = 1 默认
        //   HIGH_COMPLEXITY = 2  最慢、最高质量
        // 单核 XP VM 上 LOW_COMPLEXITY 可显著减少运动估计搜索范围，
        // 实测编码时间可降低 30-50%。
        internal const int LOW_COMPLEXITY = 0;
        internal const int MEDIUM_COMPLEXITY = 1;
        internal const int HIGH_COMPLEXITY = 2;

        /// <summary>
        /// 编码器初始化参数（SEncParamBase 简版）。仅支持 VIDEO_REAL_TIME 用法。
        /// 注意：SCREEN_CONTENT_REAL_TIME 必须使用 SEncParamExt（InitializeExt），
        /// 本结构已不再用于生产路径，仅保留作参考/诊断。
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct SEncParamBase
        {
            public int iUsageType;       // SCREEN_CONTENT_REAL_TIME
            public int iPicWidth;
            public int iPicHeight;
            public int iTargetBitrate;
            public int iRCMode;          // RC_BITRATE_MODE
            public float fMaxFrameRate;

            public void Init(int w, int h, int bitrate)
            {
                iUsageType = VIDEO_REAL_TIME; // SEncParamBase 只支持 VIDEO_REAL_TIME；SCREEN_CONTENT_REAL_TIME 需 SEncParamExt
                iPicWidth = w;
                iPicHeight = h;
                iTargetBitrate = bitrate;
                iRCMode = RC_BITRATE_MODE;
                fMaxFrameRate = 30f;
            }
        }

        // ============================================================
        // SEncParamExt — 原生内存 + Marshal 偏移写入
        // ------------------------------------------------------------
        // OpenH264 的 SEncParamExt 有 37 个字段，其中包含 4 个嵌套的
        // SSpatialLayerConfig（各 200 字节）和多个 C++ bool（1 字节）。
        // 在 C# 中重建整个 struct 的二进制布局风险极高（bool 对齐、枚举大小等），
        // 因此采用"原生缓冲 + 按已知偏移写入"的方案：
        //   1. AllocHGlobal 分配足够大的原生缓冲（8KB）并清零
        //   2. 调用 ISVCEncoder::GetDefaultParams（vtable slot 2）让 DLL 按
        //      自身编译布局填充完整默认值（含 sSpatialLayers 内部字段）
        //   3. 用 Marshal.Write* 按本类定义的偏移覆盖需要修改的字段
        //   4. 调用 ISVCEncoder::InitializeExt（vtable slot 1）
        //
        // 偏移依据 openh264 2.6.0 codec_api.h / codec_app_def.h：
        // SEncParamExt 字段顺序（Rust openh264-sys2 repr(C) 可交叉验证）：
        //   iUsageType(0) iPicWidth(4) iPicHeight(8) iTargetBitrate(12)
        //   iRCMode(16) fMaxFrameRate(20) iTemporalLayerNum(24) iSpatialLayerNum(28)
        //   sSpatialLayers[4](32, 每层 200B) iComplexityMode(832) uiIntraPeriod(836)
        //   iNumRefFrame(840) eSpsPpsIdStrategy(844) bPrefixNalAddingCtrl(848)
        //   bEnableSSEI(849) bSimulcastAVC(850) [pad](851) iPaddingFlag(852)
        //   iEntropyCodingModeFlag(856) bEnableFrameSkip(860) [pad](861-863)
        //   iMaxBitrate(864) iMaxQp(868) iMinQp(872) uiMaxNalSize(876)
        //   bEnableLongTermReference(880) [pad](881-883) iLTRRefNum(884)
        //   iLtrMarkPeriod(888) iMultipleThreadIdc(892) bUseLoadBalancing(894)
        //   [pad](895) iLoopFilterDisableIdc(896) iLoopFilterAlphaC0Offset(900)
        //   iLoopFilterBetaOffset(904) bEnableDenoise(908) bEnableBackgroundDetection(909)
        //   bEnableAdaptiveQuant(910) bEnableFrameCroppingFlag(911)
        //   bEnableSceneChangeDetect(912) bIsLosslessLink(913)
        // ============================================================
        internal static class SEncParamExtOffsets
        {
            public const int AllocSize = 8192;

            public const int IUsageType = 0;
            public const int IPicWidth = 4;
            public const int IPicHeight = 8;
            public const int ITargetBitrate = 12;
            public const int IRCMode = 16;
            public const int FMaxFrameRate = 20;
            public const int ITemporalLayerNum = 24;
            public const int ISpatialLayerNum = 28;
            public const int SSpatialLayers = 32;

            // sSpatialLayers[4] 之后
            public const int IComplexityMode = 832;
            public const int UiIntraPeriod = 836;
            public const int INumRefFrame = 840;
            public const int ESpsPpsIdStrategy = 844;
            public const int BPrefixNalAddingCtrl = 848;
            public const int BEnableSSEI = 849;
            public const int BSimulcastAVC = 850;
            public const int IPaddingFlag = 852;
            public const int IEntropyCodingModeFlag = 856;
            public const int BEnableFrameSkip = 860;
            public const int IMaxBitrate = 864;
            public const int IMaxQp = 868;
            public const int IMinQp = 872;
            public const int UiMaxNalSize = 876;
            public const int BEnableLongTermReference = 880;
            public const int ILTRRefNum = 884;
            public const int ILtrMarkPeriod = 888;
            public const int IMultipleThreadIdc = 892;
            public const int BUseLoadBalancing = 894;
            public const int ILoopFilterDisableIdc = 896;
            public const int ILoopFilterAlphaC0Offset = 900;
            public const int ILoopFilterBetaOffset = 904;
            public const int BEnableDenoise = 908;
            public const int BEnableBackgroundDetection = 909;
            public const int BEnableAdaptiveQuant = 910;
            public const int BEnableFrameCroppingFlag = 911;
            public const int BEnableSceneChangeDetect = 912;
            public const int BIsLosslessLink = 913;
        }

        /// <summary>
        /// SEncParamExt.sSpatialLayers[i]（SSpatialLayerConfig，200 字节）字段偏移。
        /// 布局（openh264 codec_app_def.h）：
        ///   iVideoWidth(0) iVideoHeight(4) fFrameRate(8) iSpatialBitrate(12)
        ///   iMaxSpatialBitrate(16) uiProfileIdc(20) uiLevelIdc(24) iDLayerQp(28)
        ///   sSliceArgument(32, 152B：uiSliceMode+uiSliceNum+uiSliceMbNum[35]+uiSliceSizeConstraint)
        ///   bVideoSignalTypePresent(184) uiVideoFormat(185) bFullRange(186)
        ///   bColorDescriptionPresent(187) uiColorPrimaries(188)
        ///   uiTransferCharacteristics(189) uiColorMatrix(190) bAspectRatioPresent(191)
        ///   eAspectRatio(192) sAspectRatioExtWidth(196) sAspectRatioExtHeight(198)
        ///   总大小 200 字节（4 字节对齐）。
        /// </summary>
        internal static class SSpatialLayerConfigOffsets
        {
            public const int LayerSize = 200;

            public const int IVideoWidth = 0;
            public const int IVideoHeight = 4;
            public const int FFrameRate = 8;
            public const int ISpatialBitrate = 12;
            public const int IMaxSpatialBitrate = 16;
            public const int UiProfileIdc = 20;
            public const int UiLevelIdc = 24;
            public const int IDLayerQp = 28;
            public const int BFullRange = 186;
        }

        /// <summary>
        /// 输入图像。字段顺序严格按 OpenH264 2.6.0 codec_app_def.h 中 Source_Picture_s 定义：
        /// <code>
        /// typedef struct Source_Picture_s {
        ///   int       iColorFormat;       // offset 0
        ///   int       iStride[4];         // offset 4 (16 bytes)
        ///   unsigned char*  pData[4];     // offset 24 (64-bit) / 20 (32-bit, no pad)
        ///   int       iPicWidth;          // offset 56 (64-bit) / 36 (32-bit)
        ///   int       iPicHeight;         // offset 60 / 40
        ///   long long uiTimeStamp;       // offset 64 (64-bit) / 48 (32-bit, 4B pad before)
        /// } SSourcePicture;
        /// </code>
        /// 旧定义字段顺序错（iPicWidth/iPicHeight 在 iStride 之前），且多了 iFrameType 字段
        /// （OpenH264 中不存在该字段）。强制 IDR 用 ISVCEncoder::ForceIntraFrame(true)，
        /// 对应 vtable slot 7。
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct SSourcePicture
        {
            public int iColorFormat;
            public int iStride0;
            public int iStride1;
            public int iStride2;
            public int iStride3;
            public IntPtr pData0;
            public IntPtr pData1;
            public IntPtr pData2;
            public IntPtr pData3;
            public int iPicWidth;
            public int iPicHeight;
            public long uiTimeStamp;

            /// <summary>初始化 I420 输入图像。强制 IDR 用 ForceIntraFrame，不再通过 iFrameType。</summary>
            public void Init(IntPtr yData, IntPtr uData, IntPtr vData, int w, int h)
            {
                iColorFormat = ENCODER_FORMAT_I420; // videoFormatI420 = 23
                iStride0 = w;         // Y stride
                // 用向上取整，奇数宽度时 U/V 平面 stride 仍覆盖 ceil(w/2) 列
                iStride1 = (w + 1) / 2;     // U stride
                iStride2 = (w + 1) / 2;     // V stride
                iStride3 = 0;
                pData0 = yData;
                pData1 = uData;
                pData2 = vData;
                pData3 = IntPtr.Zero;
                iPicWidth = w;
                iPicHeight = h;
                uiTimeStamp = 0;
            }
        }

        // ============================================================
        // SFrameBSInfo / SLayerBSInfo — 改用原生内存 + Marshal 读取
        // ------------------------------------------------------------
        // 历史问题：C# struct 定义与 OpenH264 2.6.0 实际布局严重不匹配：
        //   1. C# 只放 8 个 SLayerBSInfo 槽位，实际 MAX_LAYER_NUM_OF_FRAME = 128
        //   2. C# SLayerBSInfo 多了 rPsnr[3] (12 字节) — OpenH264 中根本不存在
        //   3. C# SFrameBSInfo 字段顺序错：sLayerInfo 应紧跟 iLayerNum，
        //      且缺少 eFrameType / iFrameSizeInBytes / uiTimeStamp 字段
        //   结果：C# struct = 472 字节，OpenH264 期望 5144 字节（64 位）
        //   → EncodeFrame 内部 memset 越界，栈损坏，返回 error 5
        //
        // 修复策略：完全删除 struct 定义，EncodeFrame 第三参数改为 IntPtr，
        // 调用方用 AllocHGlobal 分配 8KB 原生内存，编码后用 Marshal.ReadXxx 读取字段。
        // ============================================================

        /// <summary>
        /// OpenH264 SFrameBSInfo / SLayerBSInfo 字段读取器。
        /// 通过 Marshal 在原生内存上按 C/C++ 编译器布局规则读取字段。
        ///
        /// 实际 C 结构体（来自 openh264/codec/api/wels/codec_app_def.h）：
        /// <code>
        /// typedef struct {
        ///   unsigned char uiTemporalId;     // 1B
        ///   unsigned char uiSpatialId;      // 1B
        ///   unsigned char uiQualityId;      // 1B
        ///   EVideoFrameType eFrameType;     // 4B (枚举 = int)
        ///   unsigned char uiLayerType;      // 1B
        ///   int   iSubSeqId;                // 4B
        ///   int   iNalCount;                // 4B
        ///   int*  pNalLengthInByte;         // ptr
        ///   unsigned char*  pBsBuf;         // ptr
        ///   float rPsnr[3];                 // 12B — OpenH264 2.6.0 实际存在此字段！
        /// } SLayerBSInfo;
        ///
        /// typedef struct {
        ///   int           iLayerNum;
        ///   SLayerBSInfo  sLayerInfo[MAX_LAYER_NUM_OF_FRAME]; // 128 elements
        ///   EVideoFrameType eFrameType;
        ///   int iFrameSizeInBytes;
        ///   long long uiTimeStamp;
        /// } SFrameBSInfo;
        /// </code>
        ///
        /// 内存布局（基于自然对齐，MSVC 默认规则）：
        /// 64 位 SLayerBSInfo = 56 字节，整体 8 字节对齐
        ///   off 0: uiTemporalId, off 1: uiSpatialId, off 2: uiQualityId
        ///   off 3: pad, off 4-7: eFrameType, off 8: uiLayerType
        ///   off 9-11: pad, off 12-15: iSubSeqId
        ///   off 16-19: iNalCount, off 20-23: pad (8B 对齐 pNalLengthInByte)
        ///   off 24-31: pNalLengthInByte, off 32-39: pBsBuf
        ///   off 40-51: rPsnr[3] (12B), off 52-55: pad (尾部 8B 对齐)
        /// 32 位 SLayerBSInfo = 40 字节，整体 4 字节对齐
        ///   off 0-2: 三个 unsigned char, off 3: pad
        ///   off 4-7: eFrameType, off 8: uiLayerType, off 9-11: pad
        ///   off 12-15: iSubSeqId, off 16-19: iNalCount
        ///   off 20-23: pNalLengthInByte, off 24-27: pBsBuf
        ///   off 28-39: rPsnr[3] (12B)
        ///
        /// 64 位 SFrameBSInfo = 7192 字节
        ///   off 0-3: iLayerNum, off 4-7: pad (SLayerBSInfo 8B 对齐)
        ///   off 8: sLayerInfo[0] 起，128 * 56 = 7168 字节
        ///   off 7176-7179: eFrameType（顶层）
        ///   off 7180-7183: iFrameSizeInBytes（顶层）
        ///   off 7184-7191: uiTimeStamp (8B 对齐已满足)
        /// 32 位 SFrameBSInfo = 5144 字节
        ///   off 0-3: iLayerNum
        ///   off 4: sLayerInfo[0] 起，128 * 40 = 5120 字节
        ///   off 5124-5127: eFrameType
        ///   off 5128-5131: iFrameSizeInBytes
        ///   off 5132-5135: pad (long long 8B 对齐)
        ///   off 5136-5143: uiTimeStamp
        ///
        /// 重要：OpenH264 2.6.0 实际不填充顶层 eFrameType 和 iFrameSizeInBytes 字段
        /// （测试验证读取均为 0）。WebRTC 和 FFmpeg 也都只用 per-layer 字段。
        /// 因此本类的 GetFrameType/GetFrameSizeInBytes 仅供诊断使用，生产代码应使用：
        ///   - GetLayerFrameType(pBsInfo, 0)  获取第 0 层帧类型
        ///   - ComputeTotalLayerBytes(pBsInfo, layerNum)  获取编码后总字节数
        /// </summary>
        internal static class SFrameBSInfoAccess
        {
            private static readonly bool Is64Bit = IntPtr.Size == 8;

            /// <summary>SLayerBSInfo 单元大小（字节）。
            /// x64=56（含 float rPsnr[3] 12B + 4B 尾部对齐），x86=40。
            /// 旧值 x64=40 漏算 rPsnr[3]，导致 layer[1+] 偏移错误、SFrameBSInfo 整体大小不足 → x64 解码失败。</summary>
            public static readonly int LayerInfoStride = Is64Bit ? 56 : 40;

            /// <summary>sLayerInfo[0] 相对 SFrameBSInfo 起点的偏移。64位=8, 32位=4。</summary>
            public static readonly int LayerInfoOffset = Is64Bit ? 8 : 4;

            /// <summary>SFrameBSInfo 中 eFrameType 字段的偏移（紧跟 sLayerInfo[128] 之后）。
            /// 注意：OpenH264 2.6.0 不填充此字段，读取值始终为 0。</summary>
            public static readonly int FrameTypeOffset = LayerInfoOffset + 128 * LayerInfoStride;

            /// <summary>SFrameBSInfo 中 iFrameSizeInBytes 字段的偏移（紧跟 eFrameType）。
            /// 注意：OpenH264 2.6.0 不填充此字段，读取值始终为 0。</summary>
            public static readonly int FrameSizeOffset = FrameTypeOffset + 4;

            /// <summary>SLayerBSInfo 中 eFrameType 字段的偏移（紧随 3 个 unsigned char + 1B pad 之后）。</summary>
            public const int LayerFrameTypeOffset = 4;

            /// <summary>SLayerBSInfo 中 pNalLengthInByte 字段的偏移。64位=24, 32位=20。</summary>
            public static readonly int LayerNalLengthInByteOffset = Is64Bit ? 24 : 20;

            /// <summary>SLayerBSInfo 中 pBsBuf 字段的偏移。64位=32, 32位=24。</summary>
            public static readonly int LayerBsBufOffset = Is64Bit ? 32 : 24;

            /// <summary>SLayerBSInfo 中 iNalCount 字段的偏移（两种架构都是 16）。</summary>
            public const int LayerNalCountOffset = 16;

            /// <summary>建议分配的 SFrameBSInfo 内存大小。
            /// x64 实际 7192 字节，x86 实际 5144 字节，给 8KB 余量覆盖两种架构。</summary>
            public const int AllocSize = 8192;

            /// <summary>读取 iLayerNum。</summary>
            public static int GetLayerNum(IntPtr pBsInfo)
            {
                return Marshal.ReadInt32(pBsInfo, 0);
            }

            /// <summary>读取顶层 eFrameType（4 字节枚举）。1=IDR 关键帧。
            /// 警告：OpenH264 2.6.0 实际不填充此字段，始终返回 0。
            /// 生产代码请用 GetLayerFrameType(pBsInfo, 0)。</summary>
            public static int GetFrameType(IntPtr pBsInfo)
            {
                return Marshal.ReadInt32(pBsInfo, FrameTypeOffset);
            }

            /// <summary>读取顶层 iFrameSizeInBytes。
            /// 警告：OpenH264 2.6.0 实际不填充此字段，始终返回 0。
            /// 生产代码请用 ComputeTotalLayerBytes。</summary>
            public static int GetFrameSizeInBytes(IntPtr pBsInfo)
            {
                return Marshal.ReadInt32(pBsInfo, FrameSizeOffset);
            }

            /// <summary>读取第 i 层的 eFrameType（4 字节枚举）。
            /// OpenH264 会填充每层的 eFrameType，1=IDR, 2=I, 3=P。</summary>
            public static int GetLayerFrameType(IntPtr pBsInfo, int layerIdx)
            {
                int layerOffset = LayerInfoOffset + layerIdx * LayerInfoStride;
                return Marshal.ReadInt32(pBsInfo, layerOffset + LayerFrameTypeOffset);
            }

            /// <summary>读取第 i 层的 iNalCount。</summary>
            public static int GetLayerNalCount(IntPtr pBsInfo, int layerIdx)
            {
                int layerOffset = LayerInfoOffset + layerIdx * LayerInfoStride;
                return Marshal.ReadInt32(pBsInfo, layerOffset + LayerNalCountOffset);
            }

            /// <summary>读取第 i 层的 pNalLengthInByte 指针。</summary>
            public static IntPtr GetLayerNalLengthInByte(IntPtr pBsInfo, int layerIdx)
            {
                int layerOffset = LayerInfoOffset + layerIdx * LayerInfoStride;
                return Marshal.ReadIntPtr(pBsInfo, layerOffset + LayerNalLengthInByteOffset);
            }

            /// <summary>读取第 i 层的 pBsBuf 指针（该层码流数据起点）。</summary>
            public static IntPtr GetLayerBsBuf(IntPtr pBsInfo, int layerIdx)
            {
                int layerOffset = LayerInfoOffset + layerIdx * LayerInfoStride;
                return Marshal.ReadIntPtr(pBsInfo, layerOffset + LayerBsBufOffset);
            }

            /// <summary>判断是否关键帧（第 0 层 eFrameType == videoFrameTypeIDR == 1）。
            /// 使用 per-layer 字段而非顶层（OpenH264 2.6.0 不填充顶层）。</summary>
            public static bool IsKeyFrame(IntPtr pBsInfo)
            {
                return GetLayerFrameType(pBsInfo, 0) == FRAME_TYPE_IDR;
            }

            /// <summary>累计所有层的 NAL 字节数。OpenH264 通常在 iFrameSizeInBytes 给出总和，此方法用于交叉校验。</summary>
            public static int ComputeTotalLayerBytes(IntPtr pBsInfo, int layerNum)
            {
                int total = 0;
                int cap = layerNum < 0 ? 0 : (layerNum > 128 ? 128 : layerNum);
                for (int i = 0; i < cap; i++)
                {
                    int nals = GetLayerNalCount(pBsInfo, i);
                    IntPtr pNalLen = GetLayerNalLengthInByte(pBsInfo, i);
                    if (nals <= 0) continue;
                    if (pNalLen == IntPtr.Zero) continue;
                    for (int n = 0; n < nals; n++)
                        total += Marshal.ReadInt32(pNalLen, n * 4);
                }
                return total;
            }
        }

        // ====== 解码器参数 ======

        internal const int VIDEO_FORMAT_I420 = 0;

        [StructLayout(LayoutKind.Sequential)]
        internal struct SDecodingParam
        {
            public uint uiTargetDqLayer;      // C 中此字段在第一位！
            public int iOutputColorFormat;    // C 中此字段在第二位
            public int eEcActiveIdc;
            public int bParseOnly;
            public int sVideoPropertySize;
            public IntPtr pVideoProperty;

            public void Init()
            {
                iOutputColorFormat = VIDEO_FORMAT_BGRA;
                uiTargetDqLayer = 0xFF;
                eEcActiveIdc = 1; // error concealment on
                bParseOnly = 0;
                sVideoPropertySize = 0;
                pVideoProperty = IntPtr.Zero;
            }
        }

        /// <summary>
        /// 解码后单帧的系统内存缓冲信息（嵌入在 SBufferInfo.UsrData 中）。
        /// 来自 openh264/codec/api/wels/codec_def.h TagSysMemBuffer。
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct SSysMEMBuffer
        {
            public int iWidth;       // 解码后图像宽度
            public int iHeight;      // 解码后图像高度
            public int iFormat;      // EVideoFormatType
            public int iStride0;     // stride[0]
            public int iStride1;     // stride[1]
        }

        /// <summary>
        /// 解码器输出缓冲信息。来自 openh264/codec/api/wels/codec_def.h TagBufferInfo：
        /// <code>
        /// typedef struct TagBufferInfo {
        ///   int iBufferStatus;                // offset 0
        ///   unsigned long long uiInBsTimeStamp;   // offset 8 (4B pad before, 8B aligned)
        ///   unsigned long long uiOutYuvTimeStamp; // offset 16
        ///   union { SSysMEMBuffer sSystemBuffer; } UsrData;  // offset 24, 20B
        ///   unsigned char* pDst[3];           // offset 48 (x64) / 44 (x86)
        /// } SBufferInfo;
        /// </code>
        /// 大小：x64=72B, x86=56B。
        /// 旧定义把 iWidth/iHeight/iFormat/iStride 平铺在顶层，且时间戳放在 pDst 之后，
        /// 导致 x64 下 C# 结构体 64B 而 C 期望 72B，越界 8B 写入破坏栈 → 解码失败。
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct SBufferInfo
        {
            public int iBufferStatus;          // 0=未就绪, 1=数据就绪
            public long uiInBsTimeStamp;       // 输入码流时间戳（4B 隐式 padding 在前）
            public long uiOutYuvTimeStamp;     // 输出 YUV 时间戳
            public SSysMEMBuffer UsrData;      // 含 iWidth/iHeight/iFormat/iStride
            public IntPtr pDst0;               // Y 平面指针
            public IntPtr pDst1;               // U 平面指针
            public IntPtr pDst2;               // V 平面指针
        }

        // ====== VTable 调用辅助 ======

        /// <summary>从 vtable 获取指定槽位的委托。</summary>
        internal static T GetVTableDelegate<T>(IntPtr pInterface, int slot) where T : class
        {
            IntPtr vtable = Marshal.ReadIntPtr(pInterface);
            IntPtr method = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
#if NET40
            return (T)(object)Marshal.GetDelegateForFunctionPointer(method, typeof(T));
#else
            return Marshal.GetDelegateForFunctionPointer<T>(method);
#endif
        }

        internal const int ENCODER_OPTION_DATAFORMAT = 0;   // SetOption: tell encoder input color format
        internal const int ENCODER_OPTION_BITRATE = 5;      // SetOption: 运行时调整目标码率（int bps），无需重建编码器

        // ====== 编码器 VTable 槽位映射（ISVCEncoder 接口，无虚析构函数） ======
        // OpenH264 2.6.0 实际接口声明顺序（openh264/codec/api/svc/codec_api.h，
        // 已通过 welsEncoderExt.cpp 实现文件验证 — 2.6.0 中没有 PauseFrame 方法）：
        //   slot 0: Initialize(const SEncParamBase*)
        //   slot 1: InitializeExt(const SEncParamExt*)
        //   slot 2: GetDefaultParams(SEncParamExt*)
        //   slot 3: Uninitialize()
        //   slot 4: EncodeFrame(const SSourcePicture*, SFrameBSInfo*)
        //   slot 5: EncodeParameterSets(SFrameBSInfo*)
        //   slot 6: ForceIntraFrame(bool bIDR, int iLayerId = -1)
        //   slot 7: SetOption(ENCODER_OPTION, void*)
        //   slot 8: GetOption(ENCODER_OPTION, void*)
        //
        // 历史错误：曾以为 2.6.0 接口包含 PauseFrame（旧的 1.x 文档列出了它，
        // 但 2.6.0 实现文件 welsEncoderExt.cpp 中没有该方法），导致 vtable 槽位
        // 整体偏移 1：把 slot 7 当作 ForceIntraFrame（实际是 SetOption），
        // 把 slot 8 当作 SetOption（实际是 GetOption）。
        // AV 原因：调用 slot 7 当 ForceIntraFrame(encoder, true) 时，实际调用
        // SetOption(encoder, eOptionId=true=1, pOption=垃圾)，pOption 被解引用 → AV。
        // 现已修正为正确的槽位。
        //
        // 屏幕内容模式（SCREEN_CONTENT_REAL_TIME）必须用 InitializeExt（slot 1）
        // + GetDefaultParams（slot 2）初始化 SEncParamExt；SEncParamBase 路径
        // （slot 0 Initialize）只接受 VIDEO_REAL_TIME，无法开启屏幕内容优化。

        internal const int VTABLE_SLOT_INITIALIZE = 0;
        internal const int VTABLE_SLOT_INITIALIZE_EXT = 1;
        internal const int VTABLE_SLOT_GET_DEFAULT_PARAMS = 2;
        internal const int VTABLE_SLOT_ENCODE_FRAME = 4;
        internal const int VTABLE_SLOT_FORCE_INTRA_FRAME = 6;
        internal const int VTABLE_SLOT_SET_OPTION = 7;

        // ====== 编码器 VTable 委托类型 ======
        // pBsInfo 改为 IntPtr：调用方需用 Marshal.AllocHGlobal 分配原生内存，
        // 大小至少 SFrameBSInfoAccess.AllocSize，编码后用 SFrameBSInfoAccess 读取字段。

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int EncodeFrameDelegate(
            IntPtr pEncoder, ref SSourcePicture pPic, IntPtr pBsInfo);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int InitializeEncoderDelegate(
            IntPtr pEncoder, ref SEncParamBase pParam);

        /// <summary>ISVCEncoder::InitializeExt(const SEncParamExt*) — 扩展参数初始化（屏幕内容模式）。</summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int InitializeExtDelegate(
            IntPtr pEncoder, IntPtr pParam);

        /// <summary>ISVCEncoder::GetDefaultParams(SEncParamExt*) — 让 DLL 填充默认参数。</summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int GetDefaultParamsDelegate(
            IntPtr pEncoder, IntPtr pParam);

        /// <summary>ForceIntraFrame(bool bIDR, int iLayerId = -1) — 强制下一帧为 IDR 关键帧。
        /// 注意：OpenH264 2.6.0 接口实际有 2 个参数（iLayerId 有默认值 -1），
        /// C++ 调用方可省略 iLayerId 由编译器填默认值，但通过 vtable 调用必须显式传递。
        /// 旧定义只有 1 个参数，导致 iLayerId 寄存器（R8）含垃圾值被函数解引用 → AV。
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int ForceIntraFrameDelegate(
            IntPtr pEncoder, [MarshalAs(UnmanagedType.Bool)] bool bIdr, int iLayerId);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int SetOptionDelegate(
            IntPtr pEncoder, int eOptionId, IntPtr pOption);

        // ====== 解码器 VTable 委托类型 ======
        // ISVCDecoder 接口方法顺序（无虚析构函数，已通过 Initialize slot 0 成功调用验证）：
        //   slot 0: Initialize(const SDecodingParam*)
        //   slot 1: Uninitialize()
        //   slot 2: DecodeFrame(pSrc, iSrcLen, ppDst, pStride, iWidth, iHeight)  ← 6 参数
        //   slot 3: DecodeFrameNoDelay(pSrc, iSrcLen, ppDst, SBufferInfo*)       ← 4 参数，推荐
        //   slot 4: DecodeFrame2(pSrc, iSrcLen, ppDst, SBufferInfo*)             ← 4 参数
        //   slot 5: FlushFrame(ppDst, SBufferInfo*)
        //   ...
        // 旧代码用 slot 2 但签名是 4 参数（DecodeFrameNoDelay 的签名），
        // DecodeFrame 期望 6 参数，多出的 pStride/iWidth/iHeight 寄存器含垃圾值 → AV。

        internal const int VTABLE_SLOT_DEC_INITIALIZE = 0;
        internal const int VTABLE_SLOT_DEC_DECODE_FRAME_NO_DELAY = 3;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int InitializeDecoderDelegate(
            IntPtr pDecoder, IntPtr pParam);

        /// <summary>DecodeFrameNoDelay(pSrc, iSrcLen, ppDst, SBufferInfo*) — 推荐用于 H.264/AVC 解码。
        /// ppDst 是 unsigned char** — 指向 3 元素指针数组的指针（Y/U/V 平面）。
        /// 调用方需分配 IntPtr[3] 数组并固定后传其指针。OpenH264 会把 YUV 平面指针写入 ppDst[0/1/2]，
        /// 但实际像素数据在 OpenH264 内部缓冲区，需通过 bufInfo.pDst0/1/2 + stride 读取。
        /// 输出格式始终为 I420 (YUV 4:2:0)，需要做 I420→BGRA 颜色空间转换。</summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int DecodeFrameDelegate(
            IntPtr pDecoder, IntPtr pData, int iSize, IntPtr ppDst, ref SBufferInfo pDstInfo);
    }
}
