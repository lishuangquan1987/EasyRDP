using System;
using System.Net.Sockets;
using System.Threading;

namespace EasyRDP.Core.Transport
{
    /// <summary>
    /// TCP 客户端传输实现。
    /// 使用 TcpClient + PacketFramer，后台线程接收数据。
    /// 通过 <see cref="TcpTransportOptions"/> 配置所有通讯参数。
    /// </summary>
    public class TcpTransportClient : ITransportClient
    {
        private readonly TcpTransportOptions _options;
        private TcpClient _tcpClient;
        private NetworkStream _stream;
        private PacketFramer _framer;
        private Thread _receiveThread;
        private volatile bool _running;
        private volatile bool _disconnectedFired;
        private readonly byte[] _recvBuffer;
        private readonly object _sendLock = new object();

        /// <inheritdoc />
        public event EventHandler<MessageReceivedEventArgs> MessageReceived;

        /// <inheritdoc />
        public event EventHandler Disconnected;

        /// <inheritdoc />
        public LogCallback OnLog { get; set; }

        /// <summary>
        /// 使用默认配置创建 TCP 客户端传输实例。
        /// </summary>
        public TcpTransportClient()
            : this(TcpTransportOptions.Default)
        {
        }

        /// <summary>
        /// 使用自定义配置创建 TCP 客户端传输实例。
        /// 传入 null 等同使用默认配置。
        /// </summary>
        public TcpTransportClient(TcpTransportOptions options)
        {
            _options = options ?? TcpTransportOptions.Default;
            _framer = new PacketFramer();
            _recvBuffer = new byte[_options.ReceiveBufferSize];
        }

        /// <inheritdoc />
        public bool IsConnected
        {
            get { return _tcpClient != null && _tcpClient.Connected && _running; }
        }

        /// <inheritdoc />
        public bool Connect(string host, int port, int timeoutMs)
        {
            TcpClient client = null;
            try
            {
                client = new TcpClient();
                // 应用 Options 中的 Socket 配置
                client.NoDelay = _options.NoDelay;
                client.SendTimeout = _options.SendTimeoutMs;
                client.ReceiveTimeout = _options.ReceiveTimeoutMs;
                client.SendBufferSize = _options.SendBufferSize;
                client.ReceiveBufferSize = _options.ReceiveBufferSize;

                int effectiveTimeout = timeoutMs > 0 ? timeoutMs : _options.ConnectTimeoutMs;
                IAsyncResult result = client.BeginConnect(host, port, null, null);
                bool connected = result.AsyncWaitHandle.WaitOne(effectiveTimeout);
                if (!connected)
                {
                    client.Close();
                    Log(LogLevel.Error, string.Format("TCP connect timeout to {0}:{1}", host, port));
                    return false;
                }
                client.EndConnect(result);

                _tcpClient = client;
                _stream = client.GetStream();
                _stream.WriteTimeout = _options.SendTimeoutMs;
                _stream.ReadTimeout = _options.ReceiveTimeoutMs;
                _running = true;
                _receiveThread = new Thread(ReceiveLoop);
                _receiveThread.IsBackground = true;
                _receiveThread.Name = "EasyRDP-TCP-Recv";
                _receiveThread.Start();

                Log(LogLevel.Info, string.Format("TCP connected to {0}:{1}", host, port));
                return true;
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, string.Format("TCP connect failed: {0}", ex.Message));
                if (client != null)
                {
                    try { client.Close(); } catch { }
                }
                return false;
            }
        }

        /// <summary>
        /// 使用已有 TcpClient 启动接收（服务端 accept 后调用）。
        /// 继承服务端 Options 中的 NoDelay/SendTimeout/ReceiveTimeout 配置。
        /// </summary>
        /// <param name="client">已接受的 TcpClient</param>
        internal void StartWithClient(TcpClient client)
        {
            client.NoDelay = _options.NoDelay;
            client.SendTimeout = _options.SendTimeoutMs;
            client.ReceiveTimeout = _options.ReceiveTimeoutMs;
            client.SendBufferSize = _options.SendBufferSize;
            client.ReceiveBufferSize = _options.ReceiveBufferSize;

            _tcpClient = client;
            _stream = client.GetStream();
            _stream.WriteTimeout = _options.SendTimeoutMs;
            _stream.ReadTimeout = _options.ReceiveTimeoutMs;
            _running = true;
            _receiveThread = new Thread(ReceiveLoop);
            _receiveThread.IsBackground = true;
            _receiveThread.Name = "EasyRDP-TCP-Recv";
            _receiveThread.Start();
        }

        /// <inheritdoc />
        public void Disconnect()
        {
            Close();
        }

        /// <inheritdoc />
        public bool Send(byte[] data)
        {
            if (_stream == null || !_running)
                return false;

            lock (_sendLock)
            {
                if (_stream == null || !_running)
                    return false;

                try
                {
                    _stream.Write(data, 0, data.Length);
                    _stream.Flush();
                    return true;
                }
                catch (Exception ex)
                {
                    Log(LogLevel.Error, string.Format("TCP send failed: {0}", ex.Message));
                    return false;
                }
            }
        }

        private void Close()
        {
            _running = false;

            lock (_sendLock)
            {
                try { _stream.Close(); } catch { }
                try { _tcpClient.Close(); } catch { }
            }

            if (_receiveThread != null && _receiveThread.IsAlive)
            {
                _receiveThread.Join(1000);
            }

            _framer.Reset();
            FireDisconnected();
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
            FireDisconnected();
        }

        private void FireDisconnected()
        {
            if (_disconnectedFired)
                return;
            _disconnectedFired = true;

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

        /// <inheritdoc />
        public void Dispose()
        {
            Close();
        }
    }
}
