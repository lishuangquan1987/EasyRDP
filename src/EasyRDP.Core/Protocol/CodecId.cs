namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 编码类型标识。
    /// </summary>
    public enum CodecId : byte
    {
        H264Software = 1,
        H264Hardware = 2,
        /// <summary>ZRLE 区域编码（64×64 瓦片 + Zlib，无运动估计，单核 CPU 友好）。</summary>
        Zrle = 3
    }
}
