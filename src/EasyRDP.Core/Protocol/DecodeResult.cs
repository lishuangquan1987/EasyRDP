namespace EasyRDP.Core.Protocol
{
    using System;

    /// <summary>解码结果。区分"无输出"与"失败"，避免把启动缓冲误判为故障。</summary>
    public struct DecodeResult
    {
        /// <summary>解码状态。</summary>
        public DecodeStatus Status;

        /// <summary>解码后的 BGRA32 像素。Status=Ok 时有效，其它状态为 null。</summary>
        public byte[] Pixels;
    }
}
