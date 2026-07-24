namespace EasyRDP.Core.Protocol
{
    /// <summary>握手响应结果码。</summary>
    public enum HandshakeResult : byte
    {
        Success         = 0x00,
        AuthFailed      = 0x01,
        VersionMismatch = 0x02,
        ServerBusy      = 0x03,
        NoCommonCodec   = 0x05,
        InternalError   = 0xFF
    }
}
