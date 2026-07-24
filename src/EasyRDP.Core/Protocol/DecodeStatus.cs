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
}
