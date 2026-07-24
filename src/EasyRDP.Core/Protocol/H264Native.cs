using System;
using System.Runtime.InteropServices;

namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// OpenH264 原生 API P/Invoke 声明和结构体。
    /// </summary>
    internal static class H264Native
    {
        /// <summary>DLL 名称。（部署时放入 exe 同目录）。</summary>
        internal const string DllName = "openh264.dll";

        // ====== 编码器 ======

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int WelsCreateSVCEncoder(out IntPtr ppEncoder);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void WelsDestroySVCEncoder(IntPtr pEncoder);

        // ====== 解码器 ======

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int WelsCreateDecoder(out IntPtr ppDecoder);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void WelsDestroyDecoder(IntPtr pDecoder);

        // ====== 编码器参数 ======

        internal const int VIDEO_REAL_TIME = 1;
        internal const int SCREEN_CONTENT_REAL_TIME = 2;
        internal const int RC_QUALITY_MODE = 0;
        internal const int RC_BITRATE_MODE = 1;
        internal const int VIDEO_FORMAT_BGRA = 23;
        internal const int FRAME_TYPE_IDR = 1;

        /// <summary>编码器初始化参数。</summary>
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
                iUsageType = SCREEN_CONTENT_REAL_TIME;
                iPicWidth = w;
                iPicHeight = h;
                iTargetBitrate = bitrate;
                iRCMode = RC_BITRATE_MODE;
                fMaxFrameRate = 30f;
            }
        }

        /// <summary>输入图像。</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct SSourcePicture
        {
            public int iColorFormat;
            public int iPicWidth;
            public int iPicHeight;
            public int iStride0;
            public int iStride1;
            public int iStride2;
            public int iStride3;
            public IntPtr pData0;
            public IntPtr pData1;
            public IntPtr pData2;
            public IntPtr pData3;
            public long uiTimeStamp;
            public int iFrameType;

            public void Init(IntPtr bgraData, int w, int h, bool forceKey)
            {
                iColorFormat = VIDEO_FORMAT_BGRA;
                iPicWidth = w;
                iPicHeight = h;
                iStride0 = w * 4;
                iStride1 = iStride2 = iStride3 = 0;
                pData0 = bgraData;
                pData1 = pData2 = pData3 = IntPtr.Zero;
                uiTimeStamp = 0;
                iFrameType = forceKey ? FRAME_TYPE_IDR : 0;
            }
        }

        /// <summary>输出帧信息。</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct SFrameBSInfo
        {
            public int iTemporalId;
            public int iSpatialId;
            public int iQualityId;
            public int eFrameType;       // 1=IDR, 2=P
            public int iSubSeqId;
            public int iLayerNum;
            public SLayerBSInfo sLayerInfo0;
            public SLayerBSInfo sLayerInfo1;

            public int iFrameSizeInBytes
            {
                get { return sLayerInfo0.iNalCount > 0 ? sLayerInfo0.iFrameSizeInBytes : 0; }
            }

            public IntPtr pBsBuf
            {
                get { return sLayerInfo0.iNalCount > 0 ? sLayerInfo0.pBsBuf : IntPtr.Zero; }
            }

            public bool IsKeyFrame
            {
                get { return eFrameType == FRAME_TYPE_IDR; }
            }
        }

        /// <summary>层码流信息。</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct SLayerBSInfo
        {
            public byte iTemporalId;
            public byte iSpatialId;
            public byte iQualityId;
            public byte eFrameType;
            public byte iSubSeqId;
            public int iNalCount;
            public IntPtr pBsBuf;
            public int iFrameSizeInBytes;
        }

        // ====== 解码器参数 ======

        internal const int VIDEO_FORMAT_I420 = 0;

        [StructLayout(LayoutKind.Sequential)]
        internal struct SDecodingParam
        {
            public int iOutputColorFormat;
            public int uiTargetDqLayer;
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

        [StructLayout(LayoutKind.Sequential)]
        internal struct SBufferInfo
        {
            public int iBufferStatus;    // 0=not ready, 1=data ready
            public int iWidth;
            public int iHeight;
            public int iFormat;
            public int iStride0;
            public int iStride1;
            public IntPtr pDst0;
            public IntPtr pDst1;
            public IntPtr pDst2;
            public long uiInBsTimeStamp;
            public long uiOutYuvTimeStamp;
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

        // ====== 编码器 VTable 委托类型（4号槽位：EncodeFrame） ======

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int EncodeFrameDelegate(
            IntPtr pEncoder, ref SSourcePicture pPic, ref SFrameBSInfo pBsInfo);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int InitializeEncoderDelegate(
            IntPtr pEncoder, ref SEncParamBase pParam);

        // ====== 解码器 VTable 委托类型 ======

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int InitializeDecoderDelegate(
            IntPtr pDecoder, ref SDecodingParam pParam);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int DecodeFrameDelegate(
            IntPtr pDecoder, IntPtr pData, int iSize, ref IntPtr ppDst, ref SBufferInfo pDstInfo);
    }
}
