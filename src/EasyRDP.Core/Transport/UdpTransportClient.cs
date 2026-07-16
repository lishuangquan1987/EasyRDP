using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace EasyRDP.Core.Transport
{
    /// <summary>
    /// UDP 客户端传输实现。
    /// 无连接、尽力交付，适合屏幕帧和光标等实时数据。
    /// 通过 <see cref="UdpTransportOptions"/> 配置所有通讯参数。
    /// 每个 Send 调用对应一个完整的 UDP 数据报。
    /// </summary>
    public class UdpTransportClient : ITransportClient
    {
        private readonly UdpTransportOptions _options;
        private UdpClient _udpClient;
        private Thread _receiveThread;
        private volatile bool _running;
        private volatile bool _disconnectedFired;
        private readonly object _sendLock = new object();

        /// <inheritdoc />
        public event EventHandler<MessageReceivedEventArgs> MessageReceived;

        /// <inheritdoc />
        public event EventHandler Disconnected;

        /// <inheritdoc />
        public LogCallback OnLog { get; set; }

        /// <summary>
        /// 使用默认配置创建 UDP 客户端传输实例。
        /// </summary>
        public UdpTransportClient()
            : this(UdpTransportOptions.Default)
        {
        }

        /// <summary>
        /// 使用自定义配置创建 UDP 客户端传输实例。
        /// 传入 null 等同使用默认配置。
        /// </summary>
        public UdpTransportClient(UdpTransportOptions options)
        {
            _options = options ?? UdpTransportOptions.Default;
        }

        /// <inheritdoc />
        public bool IsConnected
        {
            get { return _udpClient != null && _running; }
        }

        /// <inheritdoc />
        public bool Connect(string host, int port, int timeoutMs)
        {
            UdpClient client = null;
            try
            {
                IPAddress[] addresses = Dns.GetHostAddresses(host);
                if (addresses.Length == 0)
                {
                    Log(LogLevel.Error, string.Format("UDP: cannot resolve host {0}", host));
                    return false;
                }

                client = new UdpClient();
                client.Client.SendTimeout = _options.SendTimeoutMs;
                client.Client.ReceiveTimeout = _options.ReceiveTimeoutMs;
                client.Client.ReceiveBufferSize = _options.ReceiveBufferSize;

                client.Connect(new IPEndPoint(addresses[0], port));
                _udpClient = client;
                _running = true;

                // 发送注册探测（单字节 0x00），让服务端感知此客户端。可选重试
                for (int i = 0; i < _options.ProbeRetries; i++)
                {
                    try { client.Send(new byte[] { 0x00 }, 1); break; }
                    catch { if (i == _options.ProbeRetries - 1) Log(LogLevel.Warning, "UDP probe send failed after retries"); }
                }

                _receiveThread = new Thread(ReceiveLoop);
                _receiveThread.IsBackground = true;
                _receiveThread.Name = "EasyRDP-UDP-Recv";
                _receiveThread.Start();

                Log(LogLevel.Info, string.Format("UDP connected to {0}:{1}", host, port));
                return true;
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, string.Format("UDP connect failed: {0}", ex.Message));
                if (client != null)
                {
                    try { client.Close(); } catch { }
                }
                return false;
            }
        }

        /// <inheritdoc />
        public void Disconnect()
        {
            Close();
        }

        /// <inheritdoc />
        public bool Send(byte[] data)
        {
            if (_udpClient == null || !_running)
                return false;

            lock (_sendLock)
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
        }

        private void Close()
        {
            _running = false;

            lock (_sendLock)
            {
                try { _udpClient.Close(); } catch { }
            }

            if (_receiveThread != null && _receiveThread.IsAlive)
            {
                _receiveThread.Join(1000);
            }

            FireDisconnected();
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
                catch (SocketException ex)
                {
                    if (ex.SocketErrorCode == SocketError.TimedOut)
                    {
                        if (_running)
                            continue;
                        break;
                    }
                    if (_running)
                        Log(LogLevel.Error, string.Format("UDP receive socket error: {0}", ex.SocketErrorCode));
                    break;
                }
                catch (ObjectDisposedException)
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
