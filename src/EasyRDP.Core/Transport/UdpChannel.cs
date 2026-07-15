using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace EasyRDP.Core.Transport
{
    /// <summary>
    /// UDP 通道——封装 UdpClient 的收发操作。
    /// 每个数据报即一条完整消息，无需分包处理。
    /// </summary>
    public class UdpChannel : ITransportChannel
    {
        private UdpClient _udpClient;
        private Thread _receiveThread;
        private volatile bool _running;
        private IPEndPoint _remoteEndPoint;

        /// <summary>收到消息时触发</summary>
        public event EventHandler<MessageReceivedEventArgs> MessageReceived;

        /// <summary>连接断开时触发</summary>
        public event EventHandler Disconnected;

        /// <summary>日志回调</summary>
        public LogCallback OnLog { get; set; }

        public UdpChannel()
        {
        }

        /// <summary>绑定本地端口开始接收（服务端调用）</summary>
        public void Bind(int port)
        {
            _udpClient = new UdpClient(port);
            // 允许接收任意来源
            _udpClient.Client.ReceiveBufferSize = 262144; // 256 KB
            _running = true;
            _receiveThread = new Thread(ReceiveLoop);
            _receiveThread.IsBackground = true;
            _receiveThread.Name = "EasyRDP-UDP-Recv";
            _receiveThread.Start();
        }

        /// <summary>连接到远端（客户端调用）</summary>
        public bool Connect(string host, int port, int timeoutMs)
        {
            try
            {
                _udpClient = new UdpClient();
                _udpClient.Connect(host, port);
                _remoteEndPoint = new IPEndPoint(IPAddress.Parse(host), port);
                _running = true;
                _receiveThread = new Thread(ReceiveLoop);
                _receiveThread.IsBackground = true;
                _receiveThread.Name = "EasyRDP-UDP-Recv";
                _receiveThread.Start();
                return true;
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, string.Format("UDP connect failed: {0}", ex.Message));
                return false;
            }
        }

        /// <summary>使用已有客户端启动（UDP 客户端模式下等效于 Bind）</summary>
        public void StartWithClient(object nativeClient)
        {
            // UDP doesn't have a "client per connection" model like TCP
            var existing = nativeClient as UdpClient;
            if (existing != null)
            {
                _udpClient = existing;
                _udpClient.Client.ReceiveBufferSize = 262144;
                _running = true;
                _receiveThread = new Thread(ReceiveLoop);
                _receiveThread.IsBackground = true;
                _receiveThread.Name = "EasyRDP-UDP-Recv";
                _receiveThread.Start();
            }
        }

        /// <summary>是否已连接</summary>
        public bool IsConnected
        {
            get { return _udpClient != null && _running; }
        }
        public bool Send(byte[] data)
        {
            if (_udpClient == null || !_running)
                return false;

            try
            {
                _udpClient.Send(data, data.Length);
                return true;
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, string.Format("UDP send failed: {0}", ex.Message));
                return false;
            }
        }

        /// <summary>关闭通道</summary>
        public void Close()
        {
            _running = false;

            try { _udpClient.Close(); } catch { }

            if (_receiveThread != null && _receiveThread.IsAlive)
            {
                _receiveThread.Join(1000);
            }
        }

        private void ReceiveLoop()
        {
            while (_running)
            {
                try
                {
                    IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data = _udpClient.Receive(ref remote);

                    var msg = Protocol.MessageCodec.Decode(data);
                    if (msg != null)
                    {
                        var handler = MessageReceived;
                        if (handler != null)
                        {
                            var args = new MessageReceivedEventArgs { Message = msg };
                            handler(this, args);
                        }
                    }
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (_running)
                        Log(LogLevel.Error, string.Format("UDP receive error: {0}", ex.Message));
                    break;
                }
            }
        }

        private void Log(LogLevel level, string message)
        {
            var handler = OnLog;
            if (handler != null)
                handler(level, message);
        }

        public void Dispose()
        {
            Close();
        }
    }
}
