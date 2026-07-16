using System;
using EasyRDP.Core.Transport;

namespace EasyRDP.Server.Wpf.Services
{
    /// <summary>
    /// TcpTransportServer 薄封装。
    /// </summary>
    public class ServerEngine : IDisposable
    {
        private TcpTransportServer _server;

        public event EventHandler<ConnectionEventArgs> ClientConnected;
        public event EventHandler<ConnectionEventArgs> ClientDisconnected;
        public event EventHandler<MessageReceivedEventArgs> MessageReceived;

        public void Start(int port)
        {
            _server = new TcpTransportServer();
            _server.ClientConnected += (s, e) => { var h = ClientConnected; if (h != null) h(s, e); };
            _server.ClientDisconnected += (s, e) => { var h = ClientDisconnected; if (h != null) h(s, e); };
            _server.MessageReceived += (s, e) => { var h = MessageReceived; if (h != null) h(s, e); };
            _server.Start(port);
        }

        public void Stop()
        {
            if (_server != null) { _server.Stop(); _server.Dispose(); _server = null; }
        }

        public void SendTo(uint sessionId, byte[] data)
        {
            if (_server != null) _server.SendTo(sessionId, data);
        }

        public void Disconnect(uint sessionId)
        {
            if (_server != null) _server.Disconnect(sessionId);
        }

        public void Dispose() { Stop(); }
    }
}
