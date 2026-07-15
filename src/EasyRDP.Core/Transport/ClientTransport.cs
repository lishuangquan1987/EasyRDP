using System;

namespace EasyRDP.Core.Transport
{
    /// <summary>
    /// 客户端传输层——按 TransportMode 建立连接，统一事件接口。
    /// </summary>
    public class ClientTransport : IClientTransport
    {
        private TransportMode _mode;
        private TcpChannel _tcpChannel;
        private ITransportChannel _udpChannel;
        private volatile bool _connected;

        public event EventHandler<MessageReceivedEventArgs> MessageReceived;
        public event EventHandler Disconnected;
        public LogCallback OnLog { get; set; }

        public bool IsConnected
        {
            get { return _connected && _tcpChannel != null && _tcpChannel.IsConnected; }
        }

        public ClientTransport()
        {
        }

        public bool Connect(string host, int port, TransportMode mode, int timeoutMs)
        {
            _mode = mode;

            // TCP is always required
            _tcpChannel = new TcpChannel();
            _tcpChannel.OnLog = OnLog;
            _tcpChannel.MessageReceived += OnMessage;
            _tcpChannel.Disconnected += OnTcpDisconnected;

            if (!_tcpChannel.Connect(host, port, timeoutMs))
            {
                Log(LogLevel.Error, string.Format("Failed to connect TCP to {0}:{1}", host, port));
                return false;
            }

            // Optional UDP
            if (mode == TransportMode.TcpAndUdp)
            {
                _udpChannel = new UdpChannel();
                _udpChannel.OnLog = OnLog;
                _udpChannel.MessageReceived += OnMessage;
                _udpChannel.Connect(host, port + 1, timeoutMs);
            }

            _connected = true;
            Log(LogLevel.Info, string.Format("Connected to {0}:{1}, mode={2}", host, port, mode));
            return true;
        }

        public void Disconnect()
        {
            _connected = false;
            if (_tcpChannel != null) _tcpChannel.Close();
            if (_udpChannel != null) _udpChannel.Close();
        }

        public bool Send(byte[] data)
        {
            if (_tcpChannel != null && _connected)
                return _tcpChannel.Send(data);
            return false;
        }

        /// <summary>通过 UDP 发送数据（仅 TcpAndUdp 模式有效）</summary>
        public bool SendUdp(byte[] data)
        {
            if (_udpChannel != null && _connected)
                return _udpChannel.Send(data);
            return false;
        }

        private void OnMessage(object sender, MessageReceivedEventArgs e)
        {
            var handler = MessageReceived;
            if (handler != null) handler(this, e);
        }

        private void OnTcpDisconnected(object sender, EventArgs e)
        {
            _connected = false;
            if (_udpChannel != null) _udpChannel.Close();

            var handler = Disconnected;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void Log(LogLevel level, string message)
        {
            var handler = OnLog;
            if (handler != null) handler(level, message);
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}
