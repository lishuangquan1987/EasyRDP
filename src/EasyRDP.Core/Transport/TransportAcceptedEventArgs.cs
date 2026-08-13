namespace EasyRDP.Core.Transport
{
    using System;

    /// <summary>新连接事件参数。Transport 处于「已连接但未开始接收」状态，调用方订阅后需调 Start()。</summary>
    public class TransportAcceptedEventArgs : EventArgs
    {
        public ITransport Transport;
        public string RemoteEndPoint;

        public TransportAcceptedEventArgs(ITransport transport, string remoteEndPoint)
        {
            Transport = transport;
            RemoteEndPoint = remoteEndPoint;
        }
    }
}
