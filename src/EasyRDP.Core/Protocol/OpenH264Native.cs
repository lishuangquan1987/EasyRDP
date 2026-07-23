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
        public struct SFrameBSInfo
        {
            public int iFrameType;
            public int iLayerNum;
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
        }

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int WelsCreateSvcEncoder(ref IntPtr ppEncoder);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int WelsInitializeEncoder(IntPtr pEncoder, ref SEncParamBase pParam);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int WelsEncodeFrameNoDelay(IntPtr pEncoder, ref SSourcePicture pSrcPic, ref SFrameBSInfo pBsInfo);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int WelsSetOption(IntPtr pEncoder, int optionId, IntPtr pOption, int optionLen);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int WelsDestroySvcEncoder(IntPtr pEncoder);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int WelsCreateDecoder(ref IntPtr ppDecoder);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int WelsInitializeDecoder(IntPtr pDecoder);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int WelsDecodeFrame2(IntPtr pDecoder, byte[] pSrcBuf, int iSrcBufLen, ref SSourcePicture pDstPic, ref int piFrameStatus);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int WelsDestroyDecoder(IntPtr pDecoder);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int WelsGetDecoderVersion(ref int pMajor, ref int pMinor);

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
        public const int ERROR_CODE_INVALID_PARAM = -1;
        public const int ERROR_CODE_MEMORY_ALLOCATION = -2;
        public const int ERROR_CODE_INIT_FAILED = -3;
        public const int ERROR_CODE_ENCODE_FAILED = -4;
        public const int ERROR_CODE_DECODE_FAILED = -5;

        public static bool IsAvailable()
        {
            try
            {
                IntPtr encoder = IntPtr.Zero;
                int result = WelsCreateSvcEncoder(ref encoder);
                if (result == ERROR_CODE_NONE && encoder != IntPtr.Zero)
                {
                    WelsDestroySvcEncoder(encoder);
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
    }
#endif
}