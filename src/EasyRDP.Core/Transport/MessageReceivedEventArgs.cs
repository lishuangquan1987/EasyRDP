namespace EasyRDP.Core.Transport
{
    using System;
    /// <summary>
    /// 完整消息事件参数。与连接/会话角色无关，不携带 SessionId——
    /// 服务端多会话路由由 TransportHost 在订阅闭包中捕获 sessionId 完成，与传输层解耦。
    /// </summary>
    public class MessageReceivedEventArgs : EventArgs
    {
        public byte MessageType;
        public byte[] Data;

        public MessageReceivedEventArgs(byte messageType, byte[] data)
        {
            MessageType = messageType;
            Data = data;
        }
    }
}
