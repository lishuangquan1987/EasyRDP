namespace EasyRDP.Core.Transport
{
    using System;
    using System.Net.Sockets;
    using System.Threading;
    using EasyRDP.Core.Protocol;
    using NLog;

    /// <summary>
    /// TCP 传输连接。一条已连接的 TCP 通道，实现 ITransport。
    /// 与服务端/客户端角色无关：客户端 Connector 建连与服务端 Accept 都产出本类实例。
    /// </summary>
    public class TcpTransport : ITransport
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private TcpClient _client;
        private readonly string _remoteEndPoint;
        private Thread _receiveThread;
        private volatile bool _running;
        private volatile bool _disconnected;
        private int _disconnectGuard; // 0=未断开 1=已断开（Interlocked 保证 Disconnect 清理只执行一次，避免 Disconnected 重复触发）
        private int _started; // 0=未启动 1=已启动（Interlocked 防重复 Start）
        private readonly object _sendLock = new object();
        // InputEvent 收发日志降频计数：鼠标移动 ~120Hz，逐条 DEBUG 落盘在弱机单核上
        // 拖慢链路（文件 IO + 字符串格式化），每 20 条记录一次即可定位丢包问题。
        private int _inputSendLogCounter;
        private int _inputRecvLogCounter;

        public event EventHandler<MessageReceivedEventArgs> MessageReceived;
        public event EventHandler Disconnected;
        public LogCallback OnLog { get; set; }

        /// <summary>
        /// 包装一条已连接的 TCP 连接。构造时只设 NoDelay，不启动接收线程；
        /// 调用方订阅 MessageReceived/Disconnected 后需调 Start()（避免首包竞态）。
        /// </summary>
        public TcpTransport(TcpClient client, string remoteEndPoint)
        {
            _client = client;
            _remoteEndPoint = remoteEndPoint;
            try { _client.NoDelay = true; } catch { }
        }

        public bool IsConnected
        {
            get { return _client != null && _client.Connected; }
        }

        /// <summary>开始接收循环（幂等）。</summary>
        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0)
                return;
            if (_client == null || _disconnected)
                return;

            _running = true;
            _receiveThread = new Thread(ReceiveLoop);
            _receiveThread.IsBackground = true;
            // 弱机 CPU 饱和时保证输入/帧数据及时处理（对齐旧 TcpTransportServer 的 AboveNormal）
            _receiveThread.Priority = ThreadPriority.AboveNormal;
            _receiveThread.Start();
        }

        public void Send(byte[] message)
        {
            if (message == null)
                return;
            lock (_sendLock)
            {
                if (_client == null || !_client.Connected)
                    return;
                try
                {
                    NetworkStream stream = _client.GetStream();
                    stream.Write(message, 0, message.Length);
                    // 调试日志：记录发送的完整消息（type + 总长度），供排障追踪。
                    // InputEvent（~120Hz 鼠标流）降频为每 20 条记录一次。
                    bool isInputEvent = message.Length > 1
                        && message[1] == (byte)MessageType.InputEvent;
                    if (!isInputEvent || Interlocked.Increment(ref _inputSendLogCounter) % 20 == 0)
                    {
                        Logger.Debug("TcpTransport.Send: type=0x{0:X2} bytes={1}",
                            message.Length > 1 ? message[1] : 0, message.Length);
                    }
                }
                catch (Exception ex)
                {
                    if (_client == null || !_client.Connected)
                    {
                        // 对端断开导致的写入失败是正常断连竞态，限频记录并触发清理
                        Logger.Warn("Send failed: client disconnected ({0})", ex.Message);
                        try { _client.Close(); } catch { }
                        ThreadPool.QueueUserWorkItem(s => Disconnect());
                        return;
                    }
                    Logger.Error(ex, "Send failed: {0}", ex.Message);
                    Log("Send failed: " + ex.Message);
                }
            }
        }

        public void Disconnect()
        {
            // 原子守卫：接收线程 finally 与 Send 失败路径可能并发调用 Disconnect，
            // 检查-赋值非原子会导致 Disconnected 事件触发两次；用 Interlocked 保证清理只执行一次。
            if (Interlocked.Exchange(ref _disconnectGuard, 1) != 0)
                return;
            _disconnected = true;
            _running = false;

            if (_client != null)
            {
                try { _client.Close(); } catch { }
                _client = null;
            }

            var handler = Disconnected;
            if (handler != null)
                handler(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            Disconnect();
        }

        private void ReceiveLoop()
        {
            byte[] buffer = new byte[65536];
            var framing = new MessageFramingBuffer();
            framing.MessageReady += (wire) =>
            {
                // 防御性 try-catch：单个坏消息不杀死接收线程（导致连接断开）
                try
                {
                    byte messageType;
                    byte[] payload;
                    if (!Framing.TryParse(wire, out messageType, out payload))
                    {
                        Logger.Warn("ReceiveLoop: invalid message dropped ({0} bytes)", wire.Length);
                        return;
                    }
                    // InputEvent（~120Hz 鼠标流）降频为每 20 条记录一次
                    bool isInputEvent = messageType == (byte)MessageType.InputEvent;
                    if (!isInputEvent || Interlocked.Increment(ref _inputRecvLogCounter) % 20 == 0)
                    {
                        Logger.Debug("TcpTransport.Receive: type=0x{0:X2} payloadLen={1}",
                            messageType, payload != null ? payload.Length : 0);
                    }
                    var handler = MessageReceived;
                    if (handler != null)
                        handler(this, new MessageReceivedEventArgs(messageType, payload));
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "MessageReady handler threw");
                }
            };

            try
            {
                NetworkStream stream = _client.GetStream();
                while (_running && _client.Connected)
                {
                    int bytesRead = stream.Read(buffer, 0, buffer.Length);
                    if (bytesRead <= 0)
                    {
                        Logger.Info("Receive loop: stream.Read returned {0} — peer closed connection", bytesRead);
                        break;
                    }
                    framing.Feed(buffer, 0, bytesRead);
                }
            }
            catch (Exception ex)
            {
                if (_client == null || !_client.Connected)
                    Logger.Info("Receive loop ended: socket closed");
                else
                {
                    Logger.Error(ex, "Receive error");
                    Log("Receive error: " + ex.Message);
                }
            }
            finally
            {
                Disconnect();
            }
        }

        private void Log(string message)
        {
            var cb = OnLog;
            if (cb != null)
                cb(message);
        }
    }
}
