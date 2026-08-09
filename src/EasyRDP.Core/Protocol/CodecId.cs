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
        Zrle = 3,
        /// <summary>VP8 软件编码（libvpx P/Invoke，实时低延时，弱 CPU 友好）。</summary>
        Vp8Software = 4,
        /// <summary>VP9 软件编码。预留，尚未实现。</summary>
        Vp9Software = 5
    }
}
