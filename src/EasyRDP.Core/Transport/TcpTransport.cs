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

        /// <summary>
        /// 大消息分块阈值（字节）。>此值的消息分块写、块间释放发送锁让小消息插队。
        /// 64KB：覆盖绝大多数单条控制消息（<1KB）与中小视频帧；1MB 级大帧分 ~16 块，
        /// 每块间隙都可插入 35B 光标更新 / 剪贴板 / 输入反馈。
        /// </summary>
        internal const int LargeMessageChunkSize = 64 * 1024;

        public void Send(byte[] message)
        {
            if (message == null)
                return;
            // 手动 Monitor.Enter/Exit（不能用 lock 语法糖）：大消息分块写需要在中途
            // Exit/Enter 让小消息插队；lock 块内手动 Exit 会导致块尾隐式 Exit 抛
            // SynchronizationLockException（lock 展开为 try/finally+Exit，重复释放）。
            Monitor.Enter(_sendLock);
            try
            {
                if (_client == null || !_client.Connected)
                    return;
                NetworkStream stream = _client.GetStream();
                if (message.Length <= LargeMessageChunkSize)
                {
                    stream.Write(message, 0, message.Length);
                }
                else
                {
                    // 队头阻塞消除：1MB 视频帧一次 Write 会长时间持有 _sendLock，
                    // 期间 35B 光标更新/输入事件/剪贴板只能排队（表现为鼠标回显卡顿）。
                    // 分块写 + 块间释放锁重取，等待中的小消息发送可在块间隙插队；
                    // 无等待者时 Exit+Enter 仅为两次原子操作（纳秒级），无额外开销。
                    // TCP 是字节流，接收端 MessageFramingBuffer 按完整消息解析，
                    // 分块写不影响协议正确性。
                    for (int offset = 0; offset < message.Length; offset += LargeMessageChunkSize)
                    {
                        int len = Math.Min(LargeMessageChunkSize, message.Length - offset);
                        stream.Write(message, offset, len);
                        if (offset + len < message.Length)
                        {
                            Monitor.Exit(_sendLock);
                            Thread.Sleep(0); // 让出时间片给等待中的发送线程（若有）
                            Monitor.Enter(_sendLock);
                            // 重取锁期间连接可能已被其他线程关闭（Disconnected 清理置 null）
                            if (_client == null || !_client.Connected)
                                return;
                            stream = _client.GetStream();
                        }
                    }
                }
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
            finally
            {
                Monitor.Exit(_sendLock);
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
