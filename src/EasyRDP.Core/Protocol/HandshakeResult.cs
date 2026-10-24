namespace EasyRDP.Core.Protocol
{
    /// <summary>握手响应结果码。</summary>
    public enum HandshakeResult : byte
    {
        Success         = 0x00,
        AuthFailed      = 0x01,
        VersionMismatch = 0x02,
        ServerBusy      = 0x03,
        /// <summary>服务端处于审批模式，操作员拒绝（或审批超时）本次连接。</summary>
        Denied          = 0x04,
        NoCommonCodec   = 0x05,
        InternalError   = 0xFF
    }
}
