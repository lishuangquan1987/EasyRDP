using System;
using EasyRDP.Core.Logging;
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

            // 桥接传输层日志到 LogHelper
            _server.OnLog = (level, msg) =>
            {
                if (level == LogLevel.Error || level == LogLevel.Warning)
                    LogHelper.Warn(string.Format("[Transport] {0}", msg));
                else
                    LogHelper.Info(string.Format("[Transport] {0}", msg));
            };

            _server.Start(port);
            LogHelper.Info(string.Format("ServerEngine 已启动 端口={0}", port));
        }

        public void Stop()
        {
            if (_server != null)
            {
                _server.Stop();
                _server.Dispose();
                _server = null;
                LogHelper.Info("ServerEngine 已停止");
            }
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
