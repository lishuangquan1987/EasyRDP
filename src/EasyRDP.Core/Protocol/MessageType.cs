namespace EasyRDP.Core.Protocol
{
    /// <summary>协议消息类型标识。</summary>
    public enum MessageType : byte
    {
        HandshakeReq  = 0x01,
        HandshakeRes  = 0x02,
        Keepalive     = 0x03,
        InputEvent    = 0x05,
        CursorUpdate  = 0x06,
        VideoFrame    = 0x50
    }

}
