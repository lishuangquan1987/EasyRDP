namespace EasyRDP.Core.Protocol
{
    using System;

    /// <summary>解码状态枚举。</summary>
    public enum DecodeStatus : byte
    {
        /// <summary>解码成功，Pixels 有效。</summary>
        Ok = 0,

        /// <summary>解码器缓冲中，无输出（非错误，如 B 帧未就绪）。</summary>
        NeedMoreInput = 1,

        /// <summary>可恢复的解码错误。</summary>
        Failed = 2
    }

    /// <summary>解码结果。区分"无输出"与"失败"，避免把启动缓冲误判为故障。</summary>
    public struct DecodeResult
    {
        /// <summary>解码状态。</summary>
        public DecodeStatus Status;

        /// <summary>解码后的 BGRA32 像素。Status=Ok 时有效，其它状态为 null。</summary>
        public byte[] Pixels;
    }
}
