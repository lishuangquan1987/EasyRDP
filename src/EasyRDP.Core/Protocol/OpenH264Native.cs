namespace EasyRDP.Core.Protocol
{
#if NET8_0_OR_GREATER
    using System;
    using System.Runtime.InteropServices;

    internal static class OpenH264Native
    {
        private const string DllName = "openh264.dll";

        public const int WelsMaxLayerNum = 1;

        [StructLayout(LayoutKind.Sequential)]
        public struct SEncParamBase
        {
            public int iUsageType;
            public int iPicWidth;
            public int iPicHeight;
            public int iTargetBitrate;
            public int iMaxBitrate;
            public int iFrameRateNum;
            public int iFrameRateDenom;
            public int iLayerNum;
            public int uiIntraPeriod;
            public int iProfileIdc;
            public int bEnableDenoise;
            public int bEnableBackgroundDetection;
            public int bEnableAdaptiveQuant;
            public int bEnableFrameSkip;
            public int bEnableLongTermReference;
            public int iLtrMarkPeriod;
            public int uiMaxNalSize;
            public int iMultipleThreadIdc;
            public int iEntropyCodingModeFlag;
            public int bEnableCabac;
            public int bEnableSpsPpsIdAddition;
            public int uiSpsId;
            public int uiPpsId;
            public int iLevelIdc;
            public int bEnableSVC;
            public int bEnableFrameCroppingFlag;
            public int uiFrameCroppingLeftOffset;
            public int uiFrameCroppingRightOffset;
            public int uiFrameCroppingTopOffset;
            public int uiFrameCroppingBottomOffset;
            public int iRefFrameNum;
            public int iNumSlicePerFrame;
            public int iSliceMode;
            public int iSliceArgument;
            public int bEnableLoopFilter;
            public int iLoopFilterAlphaC0Offset;
            public int iLoopFilterBetaOffset;
            public int bEnableDeblockingFilter;
            public int iDeblockingFilterAlphaC0Offset;
            public int iDeblockingFilterBetaOffset;
            public int bEnableWeightedPrediction;
            public int bEnableWeightedBiprediction;
            public int bEnableConstrainedIntraPred;
            public int bEnableSignDataHiding;
            public int bEnableAQ;
            public int iAQMode;
            public int iAQStrength;
            public int bEnableTemporalSvc;
            public int bEnableSpatialSvc;
            public int iSpatialLayers;
            public int iTemporalLayers;
            public int iQualityLayers;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SSourcePicture
        {
            public int iPicWidth;
            public int iPicHeight;
            public int iColorFormat;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public IntPtr[] pData;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public int[] iStride;

            public SSourcePicture(int width, int height, int colorFormat, IntPtr yPtr, IntPtr uPtr, IntPtr vPtr, int yStride, int uvStride)
            {
                iPicWidth = width;
                iPicHeight = height;
                iColorFormat = colorFormat;
                pData = new IntPtr[4];
                pData[0] = yPtr;
                pData[1] = uPtr;
                pData[2] = vPtr;
                pData[3] = IntPtr.Zero;
                iStride = new int[4];
                iStride[0] = yStride;
                iStride[1] = uvStride;
                iStride[2] = uvStride;
                iStride[3] = 0;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SLayerBSInfo
        {
            public int iLayerId;
            public int iTemporalId;
            public int iQualityId;
            public int iSpatialId;
            public IntPtr pBsBuf;
            public int iBsLen;
            public int iBsLenIncludingHeaders;
            public int iNalCount;
            public IntPtr pNalLengthInByte;
            public int bFrameOnly;
            public int bHasGapsInFrameNumber;
            public int iFrameNum;
            public int iPOC;
            public int iTimeStamp;
            public int iForceIntraRefresh;
            public int iIdrInterval;
            public int iLayerFrameRateNum;
            public int iLayerFrameRateDenom;
            public int iMaxBitrate;
            public int iTargetBitrate;
            public int iMaxQp;
            public int iMinQp;
            public int iSpatialLayerNum;
            public int iTemporalLayerNum;
            public int iQualityLayerNum;
            public int iBitrateRatio;
            public int iWidth;
            public int iHeight;
            public int iSliceMode;
            public int iSliceArgument;
            public int iProfileIdc;
            public int iLevelIdc;
            public int bEnableDenoise;
            public int bEnableBackgroundDetection;
            public int bEnableAdaptiveQuant;
            public int bEnableFrameSkip;
            public int bEnableLongTermReference;
            public int iLtrMarkPeriod;
            public int uiMaxNalSize;
            public int iMultipleThreadIdc;
            public int iEntropyCodingModeFlag;
            public int bEnableCabac;
            public int bEnableSpsPpsIdAddition;
            public int uiSpsId;
            public int uiPpsId;
            public int bEnableFrameCroppingFlag;
            public int uiFrameCroppingLeftOffset;
            public int uiFrameCroppingRightOffset;
            public int uiFrameCroppingTopOffset;
            public int uiFrameCroppingBottomOffset;
            public int iRefFrameNum;
            public int iNumSlicePerFrame;
            public int bEnableLoopFilter;
            public int iLoopFilterAlphaC0Offset;
            public int iLoopFilterBetaOffset;
            public int bEnableDeblockingFilter;
            public int iDeblockingFilterAlphaC0Offset;
            public int iDeblockingFilterBetaOffset;
            public int bEnableWeightedPrediction;
            public int bEnableWeightedBiprediction;
            public int bEnableConstrainedIntraPred;
            public int bEnableSignDataHiding;
            public int bEnableAQ;
            public int iAQMode;
            public int iAQStrength;
            public int bEnableTemporalSvc;
            public int bEnableSpatialSvc;
            public int iSpatialLayers;
            public int iTemporalLayers;
            public int iQualityLayers;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SFrameBSInfo
        {
            public int iFrameType;
            public int iLayerNum;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = WelsMaxLayerNum)]
            public SLayerBSInfo[] sLayerInfo;
            public int bFrameOnly;
            public int bHasGapsInFrameNumber;
            public int iFrameNum;
            public int iPOC;
            public int iTimeStamp;
            public int iForceIntraRefresh;
            public int iIdrInterval;
            public int iLayerId;
            public int iTemporalId;
            public int iQualityId;
            public int iSpatialId;
            public IntPtr pBsBuf;
            public int iBsLen;
            public int iBsLenIncludingHeaders;
            public int iNalCount;
            public IntPtr pNalLengthInByte;
        }

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int WelsCreateSvcEncoder(ref IntPtr ppEncoder);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int WelsCreateDecoder(ref IntPtr ppDecoder);

        public const int COLOR_FORMAT_I420 = 0;
        public const int COLOR_FORMAT_YV12 = 1;
        public const int COLOR_FORMAT_NV12 = 2;
        public const int COLOR_FORMAT_NV21 = 3;
        public const int COLOR_FORMAT_YUY2 = 4;
        public const int COLOR_FORMAT_UYVY = 5;
        public const int COLOR_FORMAT_YVYU = 6;
        public const int COLOR_FORMAT_A8 = 7;
        public const int COLOR_FORMAT_RAW = 8;
        public const int COLOR_FORMAT_BGR24 = 9;
        public const int COLOR_FORMAT_RGB24 = 10;
        public const int COLOR_FORMAT_ARGB = 11;
        public const int COLOR_FORMAT_RGBA = 12;

        public const int ERROR_CODE_NONE = 0;

        public const int ENCODER_OPTION_CPU_CORE_NUM = 0x0001;
        public const int ENCODER_OPTION_SVC_ENC_PARAM = 0x0002;
        public const int ENCODER_OPTION_FORCE_INTRA_FRAME = 0x0003;
        public const int ENCODER_OPTION_MAX_INTRA_PERIOD = 0x0004;
        public const int ENCODER_OPTION_BRC_PARAM = 0x0005;
        public const int ENCODER_OPTION_STATUS_CALLBACK = 0x0006;
        public const int ENCODER_OPTION_ERROR_CONCEALMENT_IDC = 0x0007;
        public const int ENCODER_OPTION_FRAME_RATE = 0x0008;
        public const int ENCODER_OPTION_BITRATE = 0x0009;
        public const int ENCODER_OPTION_KEY_FRAME_INTERVAL = 0x000A;
        public const int ENCODER_OPTION_PROFILE_IDC = 0x000B;
        public const int ENCODER_OPTION_LEVEL_IDC = 0x000C;
        public const int ENCODER_OPTION_SLICEMODE = 0x000D;
        public const int ENCODER_OPTION_LOOP_FILTER = 0x000E;
        public const int ENCODER_OPTION_MAX_NAL_SIZE = 0x000F;
        public const int ENCODER_OPTION_ADAPTIVE_QP = 0x0010;
        public const int ENCODER_OPTION_AQ_MODE = 0x0011;
        public const int ENCODER_OPTION_AQ_STRENGTH = 0x0012;
        public const int ENCODER_OPTION_ENABLE_SVC = 0x0013;
        public const int ENCODER_OPTION_NUM_SPATIAL_LAYERS = 0x0014;
        public const int ENCODER_OPTION_NUM_TEMPORAL_LAYERS = 0x0015;
        public const int ENCODER_OPTION_NUM_QUALITY_LAYERS = 0x0016;
        public const int ENCODER_OPTION_CABAC = 0x0017;
        public const int ENCODER_OPTION_DEBLOCKING_FILTER = 0x0018;
        public const int ENCODER_OPTION_FRAME_SKIP = 0x0019;
        public const int ENCODER_OPTION_NOISE_REDUCTION = 0x001A;
        public const int ENCODER_OPTION_SIGN_DATA_HIDING = 0x001B;
        public const int ENCODER_OPTION_FRAME_CROPPING = 0x001C;
        public const int ENCODER_OPTION_LONG_TERM_REFERENCE = 0x001D;
        public const int ENCODER_OPTION_LTR_MARK_PERIOD = 0x001E;
        public const int ENCODER_OPTION_FORCE_CONSTR_INTRA_PRED = 0x001F;
        public const int ENCODER_OPTION_WEIGHTED_PRED = 0x0020;
        public const int ENCODER_OPTION_WEIGHTED_BIPRED = 0x0021;
        public const int ENCODER_OPTION_MAX_BITRATE = 0x0022;

        public static bool IsAvailable()
        {
            try
            {
                IntPtr encoder = IntPtr.Zero;
                int result = WelsCreateSvcEncoder(ref encoder);
                if (result == ERROR_CODE_NONE && encoder != IntPtr.Zero)
                {
                    DestroyEncoder(encoder);
                    return true;
                }
                return false;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
        }

        public static int EncoderInitialize(IntPtr encoder, ref SEncParamBase param)
        {
            IntPtr vtable = Marshal.ReadIntPtr(encoder);
            IntPtr initPtr = Marshal.ReadIntPtr(vtable, 8);
            EncoderInitializeDelegate init = (EncoderInitializeDelegate)Marshal.GetDelegateForFunctionPointer(initPtr, typeof(EncoderInitializeDelegate));
            return init(encoder, ref param);
        }

        public static int EncoderEncodeFrameNoDelay(IntPtr encoder, ref SSourcePicture srcPic, ref SFrameBSInfo bsInfo)
        {
            IntPtr vtable = Marshal.ReadIntPtr(encoder);
            IntPtr encodePtr = Marshal.ReadIntPtr(vtable, 16);
            EncoderEncodeDelegate encode = (EncoderEncodeDelegate)Marshal.GetDelegateForFunctionPointer(encodePtr, typeof(EncoderEncodeDelegate));
            return encode(encoder, ref srcPic, ref bsInfo);
        }

        public static int EncoderSetOption(IntPtr encoder, int optionId, IntPtr pOption, int optionLen)
        {
            IntPtr vtable = Marshal.ReadIntPtr(encoder);
            IntPtr setOptionPtr = Marshal.ReadIntPtr(vtable, 20);
            EncoderSetOptionDelegate setOption = (EncoderSetOptionDelegate)Marshal.GetDelegateForFunctionPointer(setOptionPtr, typeof(EncoderSetOptionDelegate));
            return setOption(encoder, optionId, pOption, optionLen);
        }

        public static void DestroyEncoder(IntPtr encoder)
        {
            if (encoder == IntPtr.Zero) return;
            IntPtr vtable = Marshal.ReadIntPtr(encoder);
            IntPtr destroyPtr = Marshal.ReadIntPtr(vtable, 0);
            EncoderDestroyDelegate destroy = (EncoderDestroyDelegate)Marshal.GetDelegateForFunctionPointer(destroyPtr, typeof(EncoderDestroyDelegate));
            destroy(encoder);
        }

        public static int DecoderInitialize(IntPtr decoder)
        {
            IntPtr vtable = Marshal.ReadIntPtr(decoder);
            IntPtr initPtr = Marshal.ReadIntPtr(vtable, 8);
            DecoderInitializeDelegate init = (DecoderInitializeDelegate)Marshal.GetDelegateForFunctionPointer(initPtr, typeof(DecoderInitializeDelegate));
            return init(decoder);
        }

        public static int DecoderDecodeFrame2(IntPtr decoder, byte[] pSrcBuf, int iSrcBufLen, ref SSourcePicture pDstPic, ref int piFrameStatus)
        {
            IntPtr vtable = Marshal.ReadIntPtr(decoder);
            IntPtr decodePtr = Marshal.ReadIntPtr(vtable, 12);
            DecoderDecodeDelegate decode = (DecoderDecodeDelegate)Marshal.GetDelegateForFunctionPointer(decodePtr, typeof(DecoderDecodeDelegate));
            return decode(decoder, pSrcBuf, iSrcBufLen, ref pDstPic, ref piFrameStatus);
        }

        public static void DestroyDecoder(IntPtr decoder)
        {
            if (decoder == IntPtr.Zero) return;
            IntPtr vtable = Marshal.ReadIntPtr(decoder);
            IntPtr destroyPtr = Marshal.ReadIntPtr(vtable, 0);
            DecoderDestroyDelegate destroy = (DecoderDestroyDelegate)Marshal.GetDelegateForFunctionPointer(destroyPtr, typeof(DecoderDestroyDelegate));
            destroy(decoder);
        }

        private delegate void EncoderDestroyDelegate(IntPtr pEncoder);
        private delegate int EncoderInitializeDelegate(IntPtr pEncoder, ref SEncParamBase pParam);
        private delegate int EncoderEncodeDelegate(IntPtr pEncoder, ref SSourcePicture pSrcPic, ref SFrameBSInfo pBsInfo);
        private delegate int EncoderSetOptionDelegate(IntPtr pEncoder, int optionId, IntPtr pOption, int optionLen);

        private delegate void DecoderDestroyDelegate(IntPtr pDecoder);
        private delegate int DecoderInitializeDelegate(IntPtr pDecoder);
        private delegate int DecoderDecodeDelegate(IntPtr pDecoder, byte[] pSrcBuf, int iSrcBufLen, ref SSourcePicture pDstPic, ref int piFrameStatus);
    }
#endif
}