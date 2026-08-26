namespace EasyRDP.Core.Protocol
{
    /// <summary>协议消息类型标识。</summary>
    public enum MessageType : byte
    {
        HandshakeReq    = 0x01,
        HandshakeRes    = 0x02,
        Keepalive       = 0x03,
        InputEvent      = 0x05,
        CursorUpdate    = 0x06,
        /// <summary>剪贴板同步（双向：客户端→服务端 或 服务端→客户端）。</summary>
        ClipboardSync   = 0x07,
        /// <summary>文件剪贴板格式广播（延迟渲染）：仅含文件元信息，不含文件内容。</summary>
        ClipFormatList       = 0x0E,
        /// <summary>文件内容请求（延迟渲染）：接收方按需请求文件内容分片。</summary>
        ClipFileContentsReq  = 0x0F,
        /// <summary>文件内容响应（延迟渲染）：发送方返回文件内容分片。</summary>
        ClipFileContentsRes  = 0x10,
        /// <summary>图片剪贴板传输开始：含 CF_DIB 总字节数。</summary>
        ImageClipboardStart = 0x0B,
        /// <summary>图片剪贴板数据块：携带 CF_DIB 内容分片。</summary>
        ImageClipboardData  = 0x0C,
        /// <summary>图片剪贴板传输完成：接收方可设置 CF_DIB。</summary>
        ImageClipboardEnd   = 0x0D,
        VideoFrame      = 0x50,
        /// <summary>客户端请求下一帧（流控）：服务端收到后编码发送一帧（ZRLE 模式）。</summary>
        FramebufferUpdateRequest = 0x51,
        /// <summary>客户端请求关键帧（解码脱同步恢复）：H264 解码器丢参考帧后请求 IDR，服务端强制生成关键帧。</summary>
        VideoKeyframeRequest = 0x52,
        /// <summary>诊断信息请求（客户端→服务端）：请求服务端系统信息（CPU/GPU/内存/OS/采集方式）。</summary>
        DiagnosticInfoRequest = 0x12,
        /// <summary>诊断信息（服务端→客户端）：携带服务端系统信息，供连接详情面板展示。</summary>
        DiagnosticInfo = 0x13
    }
}
