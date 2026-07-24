namespace EasyRDP.Core.Transport
{
    using System;
    /// <summary>传输层收到一个线格式分片字节的事件参数。</summary>
    public class FragmentReceivedEventArgs : EventArgs
    {
        public uint SessionId;
        public byte[] Data;

        public FragmentReceivedEventArgs(uint sessionId, byte[] data)
        {
            SessionId = sessionId;
            Data = data;
        }
    }
}
