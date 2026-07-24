namespace EasyRDP.Core.Transport
{
    using System;
    /// <summary>日志回调委托。传输层内部日志通过它回传。</summary>
    public delegate void LogCallback(string message);

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

    /// <summary>连接事件参数。</summary>
    public class ConnectionEventArgs : EventArgs
    {
        public uint SessionId;
        public string RemoteEndPoint;

        public ConnectionEventArgs(uint sessionId, string remoteEndPoint)
        {
            SessionId = sessionId;
            RemoteEndPoint = remoteEndPoint;
        }
    }
}
