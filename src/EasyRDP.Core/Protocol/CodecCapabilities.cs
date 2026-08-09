namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 编解码能力位掩码，用于握手时声明支持的编码器集合。
    /// </summary>
    [System.Flags]
    public enum CodecCapabilities : byte
    {
        /// <summary>No codec capability.</summary>
        None         = 0,
        /// <summary>Software H.264 encoding support.</summary>
        H264Software = 1 << 0,
        /// <summary>Hardware-accelerated H.264 encoding support.</summary>
        H264Hardware = 1 << 1,
        /// <summary>ZRLE 区域编码支持（纯 C#，无原生依赖）。</summary>
        Zrle         = 1 << 2,
        /// <summary>VP8 软件编码支持（libvpx P/Invoke）。</summary>
        Vp8Software  = 1 << 3,
        /// <summary>VP9 软件编码支持。预留，尚未实现。</summary>
        Vp9Software  = 1 << 4
    }
}
