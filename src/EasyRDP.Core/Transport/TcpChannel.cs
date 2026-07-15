using System;
using System.Net.Sockets;
using System.Threading;

namespace EasyRDP.Core.Transport
{
    /// <summary>
    /// TCP 通道——封装 TcpClient 的收发操作。
    /// 接收线程自动运行，通过事件回调通知。
    /// </summary>
    public class TcpChannel : ITransportChannel
    {
        private TcpClient _tcpClient;
        private NetworkStream _stream;
        private PacketFramer _framer;
        private Thread _receiveThread;
        private volatile bool _running;
        private readonly byte[] _recvBuffer = new byte[8192];
        private readonly object _sendLock = new object();

        /// <summary>收到完整消息时触发</summary>
        public event EventHandler<MessageReceivedEventArgs> MessageReceived;

        /// <summary>连接断开时触发</summary>
        public event EventHandler Disconnected;

        /// <summary>日志回调</summary>
        public LogCallback OnLog { get; set; }

        public TcpChannel()
        {
            _framer = new PacketFramer();
        }

        /// <summary>绑定端口——TCP 通道不支持单独绑定，由 ServerTransport.TcpListener 管理</summary>
        public void Bind(int port)
        {
            // TCP channels are per-connection; listening is handled by ServerTransport
        }

        /// <summary>使用已有 TcpClient 启动接收（服务端 accept 后调用）</summary>
        public void StartWithClient(object nativeClient)
        {
            var client = nativeClient as TcpClient;
            if (client == null)
                throw new ArgumentException("TcpChannel requires a TcpClient", "nativeClient");

            StartWithClient(client);
        }

        /// <summary>使用已有 TcpClient 启动接收（强类型版本）</summary>
        public void StartWithClient(TcpClient client)
        {
            _tcpClient = client;
            _stream = client.GetStream();
            _running = true;
            _receiveThread = new Thread(ReceiveLoop);
            _receiveThread.IsBackground = true;
            _receiveThread.Name = "EasyRDP-TCP-Recv";
            _receiveThread.Start();
        }

        /// <summary>连接到远程服务端（客户端调用）</summary>
        public bool Connect(string host, int port, int timeoutMs)
        {
            try
            {
                _tcpClient = new TcpClient();
                IAsyncResult result = _tcpClient.BeginConnect(host, port, null, null);
                bool connected = result.AsyncWaitHandle.WaitOne(timeoutMs);
                if (!connected)
                {
                    _tcpClient.Close();
                    return false;
                }
                _tcpClient.EndConnect(result);
                _stream = _tcpClient.GetStream();
                _running = true;
                _receiveThread = new Thread(ReceiveLoop);
                _receiveThread.IsBackground = true;
                _receiveThread.Name = "EasyRDP-TCP-Recv";
                _receiveThread.Start();
                return true;
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, string.Format("TCP connect failed: {0}", ex.Message));
                return false;
            }
        }

        /// <summary>发送数据</summary>
        public bool Send(byte[] data)
        {
            if (_stream == null || !_running)
                return false;

            try
            {
                lock (_sendLock)
                {
                    _stream.Write(data, 0, data.Length);
                    _stream.Flush();
                }
                return true;
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, string.Format("TCP send failed: {0}", ex.Message));
                return false;
            }
        }

        /// <summary>关闭通道</summary>
        public void Close()
        {
            _running = false;

            try { _stream.Close(); } catch { }
            try { _tcpClient.Close(); } catch { }

            if (_receiveThread != null && _receiveThread.IsAlive)
            {
                _receiveThread.Join(1000);
            }

            _framer.Reset();
        }

        public bool IsConnected
        {
            get { return _tcpClient != null && _tcpClient.Connected && _running; }
        }

        private void ReceiveLoop()
        {
            while (_running)
            {
                try
                {
                    int bytesRead = _stream.Read(_recvBuffer, 0, _recvBuffer.Length);
                    if (bytesRead == 0)
                    {
                        // 对端正常关闭
                        Log(LogLevel.Debug, "TCP stream closed by remote");
                        break;
                    }

                    var messages = _framer.Feed(_recvBuffer, 0, bytesRead);
                    foreach (var msgBytes in messages)
                    {
                        var msg = Protocol.MessageCodec.Decode(msgBytes);
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
                }
                catch (System.IO.IOException)
                {
                    Log(LogLevel.Debug, "TCP read interrupted");
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (_running)
                        Log(LogLevel.Error, string.Format("TCP receive error: {0}", ex.Message));
                    break;
                }
            }

            _running = false;

            var disconnectedHandler = Disconnected;
            if (disconnectedHandler != null)
            {
                disconnectedHandler(this, EventArgs.Empty);
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
