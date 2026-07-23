namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 消息类型码
    /// </summary>
    public enum MessageType : byte
    {
        /// <summary>握手请求 C→S</summary>
        HandshakeReq = 0x01,

        /// <summary>握手响应 S→C</summary>
        HandshakeRes = 0x02,

        /// <summary>屏幕帧数据 S→C</summary>
        ScreenFrame = 0x10,

        /// <summary>光标状态更新 S→C</summary>
        CursorUpdate = 0x11,

        /// <summary>屏幕区域复制指令 S→C（零像素传输，客户端自行复制已有区域）</summary>
        CopyRect = 0x12,

        /// <summary>视频帧数据 S→C（H.264 等）</summary>
        VideoFrame = 0x50,

        /// <summary>键鼠输入事件 C→S</summary>
        InputEvent = 0x20,

        /// <summary>剪贴板同步 (双向)</summary>
        ClipboardData = 0x21,

        /// <summary>心跳请求 C→S</summary>
        KeepAlive = 0x30,

        /// <summary>心跳应答 S→C</summary>
        KeepAliveAck = 0x31,

        /// <summary>文件传输请求 (双向)</summary>
        FileTransferReq = 0x40,

        /// <summary>文件数据块 (双向)</summary>
        FileTransferData = 0x41,

        /// <summary>断开连接 (双向)</summary>
        Disconnect = 0xFF
    }

    /// <summary>
    /// 握手结果码
    /// </summary>
    public enum HandshakeResult : byte
    {
        /// <summary>成功</summary>
        Success = 0x00,

        /// <summary>认证失败</summary>
        AuthFailed = 0x01,

        /// <summary>协议版本不兼容</summary>
        VersionMismatch = 0x02,

        /// <summary>服务端繁忙</summary>
        ServerBusy = 0x03,

        /// <summary>不支持的压缩类型</summary>
        UnsupportedCompress = 0x04,

        /// <summary>内部错误</summary>
        InternalError = 0xFF
    }

    /// <summary>
    /// 断开原因码
    /// </summary>
    public enum DisconnectReason : byte
    {
        /// <summary>用户主动断开</summary>
        UserDisconnect = 0x00,

        /// <summary>超时断开</summary>
        Timeout = 0x01,

        /// <summary>协议错误</summary>
        ProtocolError = 0x02,

        /// <summary>服务端关闭</summary>
        ServerShutdown = 0x03,

        /// <summary>未知原因</summary>
        Unknown = 0xFF
    }

    /// <summary>
    /// 屏幕帧类型
    /// </summary>
    public enum FrameType : byte
    {
        /// <summary>全帧（关键帧）</summary>
        Full = 0,

        /// <summary>增量帧（脏矩形）</summary>
        Delta = 1
    }

    /// <summary>
    /// 压缩类型
    /// </summary>
    public enum CompressType : byte
    {
        /// <summary>无压缩</summary>
        None = 0,

        /// <summary>ZLIB 压缩</summary>
        Zlib = 1,

        /// <summary>LZ4 压缩</summary>
        Lz4 = 2,

        /// <summary>JPEG 有损压缩（适用于全帧/大脏矩形）</summary>
        JPEG = 3
    }

    /// <summary>
    /// 输入事件类型
    /// </summary>
    public enum InputEventType : byte
    {
        /// <summary>鼠标移动</summary>
        MouseMove = 0,

        /// <summary>鼠标按下</summary>
        MouseDown = 1,

        /// <summary>鼠标释放</summary>
        MouseUp = 2,

        /// <summary>鼠标滚轮</summary>
        MouseWheel = 3,

        /// <summary>键盘按下</summary>
        KeyDown = 4,

        /// <summary>键盘释放</summary>
        KeyUp = 5,

        /// <summary>Unicode 文本</summary>
        UnicodeText = 6
    }

    /// <summary>
    /// 剪贴板数据格式
    /// </summary>
    public enum ClipboardFormat : byte
    {
        /// <summary>Unicode 文本 (CF_UNICODETEXT)</summary>
        UnicodeText = 0
    }

    /// <summary>
    /// 文件传输动作
    /// </summary>
    public enum FileTransferAction : byte
    {
        /// <summary>发起传输</summary>
        Send = 0,

        /// <summary>接受传输</summary>
        Accept = 1,

        /// <summary>拒绝传输</summary>
        Reject = 2,

        /// <summary>取消传输</summary>
        Cancel = 3
    }
}
