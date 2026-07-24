namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 编解码能力位掩码，用于握手时声明支持的编码器集合。
    /// </summary>
    [System.Flags]
    public enum CodecCapabilities : byte
    {
        None         = 0,
        H264Software = 1 << 0,  // = 1
        H264Hardware = 1 << 1   // = 2
    }
}
