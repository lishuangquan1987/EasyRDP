namespace EasyRDP.Core.Transport
{
    using System;
    /// <summary>
    /// 完整消息事件参数。SessionId 为服务端路由辅助字段：TcpTransport 抛出时恒为 0，
    /// 由 TransportHost 在订阅闭包中填充真实 sessionId 完成路由；客户端侧恒为 0。
    /// </summary>
    public class MessageReceivedEventArgs : EventArgs
    {
        public uint SessionId;
        public byte MessageType;
        public byte[] Data;

        public MessageReceivedEventArgs(uint sessionId, byte messageType, byte[] data)
        {
            SessionId = sessionId;
            MessageType = messageType;
            Data = data;
        }
    }
}
