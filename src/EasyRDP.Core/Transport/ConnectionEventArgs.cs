namespace EasyRDP.Core.Transport
{
    using System;
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
