namespace EasyRDP.Core.Transport
{
    using System;
    /// <summary>
    /// 完整消息事件参数。MessageReassembler 组装完毕后抛出。
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
