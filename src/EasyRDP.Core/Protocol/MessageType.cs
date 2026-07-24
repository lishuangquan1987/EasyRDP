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

    /// <summary>输入事件类型。</summary>
    public enum InputEventType : byte
    {
        KeyDown    = 1,
        KeyUp      = 2,
        MouseMove  = 3,
        MouseDown  = 4,
        MouseUp    = 5,
        MouseWheel = 6
    }
}
