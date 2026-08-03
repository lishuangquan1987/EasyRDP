#nullable disable
using System;
using System.Threading;
using EasyRDP.Core.Protocol;
using EasyRDP.Core.Rendering;
using EasyRDP.Core.Session;
using EasyRDP.Core.Transport;
using NLog;

namespace EasyRDP.Client.Wpf
{
    /// <summary>
    /// 客户端视频流会话。双线程：接收线程解码→FrameBuffer，渲染线程→RenderTarget。
    /// </summary>
    public class ClientStreamSession : IClientStreamSession
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private ITransportClient _transport;
        private IVideoDecoder _decoder;
        private FrameBuffer _frameBuffer;
        private IRenderTarget _renderTarget;
        private MessageReassembler _reassembler;
        private volatile bool _running;
        // 0=未触发, 1=已触发；用 Interlocked 保证跨线程只触发一次
        private int _fatalRaisedFlag;
        private Thread _receiveThread;
        private Thread _renderThread;
        // 解码专用线程：视频帧解码不再占用 TCP 接收线程，
        // 避免 CursorUpdate/Clipboard 等控制消息被解码阻塞（否则鼠标回显延迟随解码耗时增长）。
        private Thread _decodeThread;
        private long _frameCount;
        private int _decodeFailures;
        // 握手竞态修复：服务端先启动视频流再发 HandshakeRes，客户端若在收到响应后才订阅
        // 数据事件，首帧/首个关键帧（seq=0）会丢失，解码器只能等下一个 IDR（最长约 1 秒黑屏）。
        // 因此在发送握手前就通过 BeginReceive 订阅，管线未就绪前把消息缓冲，InitPipeline 后回放。
        private volatile bool _receiving;
        // 跨线程访问（UI 线程写、接收线程读），必须 volatile 否则接收线程可能永远读到旧值
        private volatile bool _pipelineReady;
        private readonly object _pendingLock = new object();
        private readonly System.Collections.Generic.List<MessageReceivedEventArgs> _pendingMessages
            = new System.Collections.Generic.List<MessageReceivedEventArgs>();
        private const int MaxPendingMessages = 1024;
        private bool _pendingOverflowLogged;
        // 管线（RenderTarget）就绪前到达的光标消息：服务端在 HandshakeRes 之前就已启动光标会话，
        // 若直接丢弃，初始形状更新会在握手窗口丢失，之后只收到纯位置更新 → 客户端永远没有光标位图。
        private CursorUpdateMessage _pendingCursor;
        // 是否已记录过"首次收到含形状的光标更新"（诊断用，避免 60Hz 刷屏）
        private bool _cursorShapeLogged;
        // 解码信箱：单槽"最新帧优先"。接收线程入队时覆盖未解码的旧帧，
        // 解码线程始终处理最新帧 —— 积压上限固定为 1 帧，端到端延迟不会随解码慢而膨胀。
        // （H264 允许丢帧，解码器等下一个关键帧即可恢复，实时语义：丢帧优于延迟。）
        private readonly object _decodeLock = new object();
        private VideoFrameMessage _pendingDecodeFrame;
        private int _decodeFrameDrops;

        /// <summary>服务端分辨率变化事件（解码线程触发，用于同步客户端坐标映射与显示尺寸）。</summary>
        public event Action<int, int> ResolutionChanged;

        /// <summary>Gets the negotiated video codec used for decoding.</summary>
        public CodecId Codec { get; private set; }
        /// <summary>Gets the current frame width in pixels.</summary>
        public int FrameWidth { get { return _frameBuffer != null ? _frameBuffer.Width : 0; } }
        /// <summary>Gets the current frame height in pixels.</summary>
        public int FrameHeight { get { return _frameBuffer != null ? _frameBuffer.Height : 0; } }
        /// <summary>Gets the total number of frames received and processed.</summary>
        public long FrameCount { get { return _frameCount; } }

        /// <summary>Gets or sets the render target where decoded frames are displayed.</summary>
        public IRenderTarget RenderTarget
        {
            get { return _renderTarget; }
            set { _renderTarget = value; }
        }

        /// <summary>Raised when a non-recoverable error occurs during the stream session.</summary>
        public event EventHandler<ErrorEventArgs> FatalError;

        /// <summary>初始化渲染管线（在收到 HandshakeRes 后调用）。</summary>
        public void InitPipeline(CodecId codec, int width, int height)
        {
            Codec = codec;
            Logger.Info("InitPipeline: codec={0} resolution={1}x{2}", codec, width, height);
            _decoder = DecoderFactory.Create(codec);
            if (_decoder != null)
                _decoder.Initialize(width, height);
            else
                Logger.Error("InitPipeline: decoder not available for codec {0} — H264 decoding is mandatory", codec);
            _frameBuffer = new FrameBuffer();
            if (_renderTarget != null)
                _renderTarget.Resize(width, height);
            _pipelineReady = true;
            FlushPendingMessages();
        }

        /// <summary>
        /// 在发送 HandshakeReq 之前调用：提前订阅传输层数据事件并创建重组器，
        /// 使服务端在 HandshakeRes 之后立刻发来的视频帧不会因为客户端尚未 Start 而丢失。
        /// 管线未就绪（InitPipeline 未完成）时收到的消息会先缓冲，就绪后按序回放。
        /// </summary>
        public void BeginReceive(ITransportClient transport)
        {
            lock (_pendingLock)
            {
                if (_receiving) return;
                _transport = transport;
                _receiving = true;
                _reassembler = new MessageReassembler();
                _reassembler.MessageReceived += OnMessageReceived;
                _transport.DataReceived += OnDataReceived;
            }
        }

        /// <summary>Starts the stream session: begins receiving, decoding, and rendering frames.</summary>
        public void Start(ITransportClient transport)
        {
            if (_running) return;
            if (!_receiving)
                BeginReceive(transport);
            else
                _transport = transport;
            _running = true;

            _receiveThread = new Thread(ReceiveLoop);
            _receiveThread.IsBackground = true;
            _receiveThread.Start();

            _decodeThread = new Thread(DecodeLoop);
            _decodeThread.IsBackground = true;
            _decodeThread.Start();

            _renderThread = new Thread(RenderLoop);
            _renderThread.IsBackground = true;
            _renderThread.Start();

            // 防御性回放：万一 InitPipeline 未先于 Start 完成，缓冲的消息也要能及时处理
            FlushPendingMessages();
        }

        /// <summary>Stops the stream session, terminates background threads, and cleans up resources.</summary>
        public void Stop()
        {
            Logger.Info("ClientStreamSession stopping, frames received: {0} decodeFailures: {1}", _frameCount, _decodeFailures);
            _running = false;
            _receiving = false;
            _pipelineReady = false;
            Interlocked.Exchange(ref _fatalRaisedFlag, 0);
            // 退订分辨率变化事件：避免停止期间解码线程触发回调访问已释放/断开的资源
            ResolutionChanged = null;
            // 唤醒解码线程使其退出
            lock (_decodeLock)
            {
                Monitor.PulseAll(_decodeLock);
            }
            if (_transport != null)
                _transport.DataReceived -= OnDataReceived;
            if (_reassembler != null)
            {
                _reassembler.MessageReceived -= OnMessageReceived;
                _reassembler = null;
            }
            lock (_pendingLock)
            {
                _pendingMessages.Clear();
                _pendingCursor = null;
            }

            // 取消所有正在进行的文件剪贴板下载，避免断连后后台线程继续向已关闭的 transport 发送请求
            // Cancel 只设置标志位，in-flight 的请求仍会等超时退出，但不会发新请求
            foreach (var kv in _clipConsumers)
            {
                try { kv.Value.Cancel(); } catch { }
            }
            _clipConsumers.Clear();

            _receiveThread?.Join(3000);
            _decodeThread?.Join(3000);
            _decodeThread = null;
            _renderThread?.Join(3000);
            lock (_decodeLock)
            {
                _pendingDecodeFrame = null;
            }

            _decoder?.Dispose();
            _decoder = null;
            _frameBuffer?.Reset();
            _frameBuffer = null;
            Logger.Info("ClientStreamSession stopped");
        }

        /// <summary>Disposes the session by stopping all activity and releasing resources.</summary>
        public void Dispose()
        {
            Stop();
        }

        private void OnDataReceived(object sender, FragmentReceivedEventArgs e)
        {
            if (e == null || e.Data == null || e.Data.Length < 16) return;

            // 探测消息类型：wire[0]=Magic, wire[1]=MessageType
            // 光标更新独立处理，不与视频帧共享 MessageReassembler 的 FrameId 命名空间
            byte msgType = e.Data[1];
            if (msgType == (byte)MessageType.CursorUpdate)
            {
                ProcessCursorFragment(e.Data);
                return;
            }

            // 其他消息类型走标准重组路径
            _reassembler?.OnFragment(e);
        }

        private void ProcessCursorFragment(byte[] wire)
        {
            // wire format: Magic(1)+Type(1)+PayloadLen(4)+FrameId(4)+FragIdx(2)+FragCount(2)+CRC16(2)+FragData
            // Minimum cursor payload: Visible(1)+X(4)+Y(4)+Width(4)+Height(4)+HotX(4)+HotY(4)+RgbaLen(4) = 29 bytes
            const int WireHeaderSize = 16;
            const int MinCursorPayload = 29;
            const int MaxCursorPayload = 1024 * 1024; // 1MB cursor data is generous

            // 验证分片参数：光标消息始终为单分片
            ushort fragIdx = (ushort)(wire[10] | (wire[11] << 8));
            ushort fragCount = (ushort)(wire[12] | (wire[13] << 8));
            if (fragIdx != 0 || fragCount != 1)
                return; // Multi-fragment cursor — discard

            int fragDataLen = wire.Length - WireHeaderSize;
            if (fragDataLen < MinCursorPayload || fragDataLen > MaxCursorPayload)
                return; // Payload out of bounds — discard

            // Verify CRC16
            ushort expectedCrc = (ushort)(wire[14] | (wire[15] << 8));
            ushort actualCrc = MessageReassembler.ComputeCrc16(wire, WireHeaderSize, fragDataLen);
            if (actualCrc != expectedCrc)
                return; // CRC mismatch — discard

            // Parse cursor payload
            byte[] cursorPayload = new byte[fragDataLen];
            Buffer.BlockCopy(wire, WireHeaderSize, cursorPayload, 0, fragDataLen);
            try
            {
                var msg = CursorUpdateMessage.Unpack(cursorPayload);
                if (!_pipelineReady)
                {
                    // 管线就绪前缓冲光标消息，由 FlushPendingCursor 在握手完成后回放，
                    // 避免初始形状更新被 ProcessCursorUpdate 的 _renderTarget==null 检查丢弃。
                    // 注意：移动光标产生的纯位置更新（RgbaPixels=null）不能覆盖未回放的形状消息，
                    // 否则回放后客户端仍无位图可渲染；位置只刷新坐标，形状保留。
                    lock (_pendingLock)
                    {
                        var pending = _pendingCursor;
                        if (pending != null && pending.RgbaPixels != null && msg.RgbaPixels == null)
                        {
                            pending.X = msg.X;
                            pending.Y = msg.Y;
                        }
                        else
                        {
                            _pendingCursor = msg;
                        }
                    }
                    return;
                }
                ProcessCursorUpdate(msg);
            }
            catch (Exception)
            {
                // Malformed cursor data — discard silently
            }
        }

        /// <summary>
        /// 收到服务端发来的剪贴板同步文本时触发。订阅者必须在 UI 线程（STA）调用 Clipboard.SetText。
        /// 参数为剪贴板文本内容。
        /// </summary>
        public event Action<string> ClipboardReceived;

        /// <summary>
        /// 收到服务端发来的文件剪贴板传输完成事件。订阅者必须在 UI 线程（STA）调用 Clipboard.SetFiles。
        /// 参数为客户端本地临时文件的路径数组。
        /// </summary>
        public event Action<string[]> FileClipboardReceived;

        /// <summary>
        /// 文件剪贴板下载进度事件：(downloadedBytes, totalBytes)。
        /// 在下载线程触发，订阅者需自行 marshal 到 UI 线程。
        /// 用于 UI 进度条显示。
        /// </summary>
        public event Action<long, long> FileClipboardProgress;

        /// <summary>
        /// 收到服务端发来的图片剪贴板传输完成事件。订阅者必须在 UI 线程（STA）调用 Clipboard.SetImage。
        /// 参数为完整的 CF_DIB 字节数组。
        /// </summary>
        public event Action<byte[]> ImageClipboardReceived;

        // 文件剪贴板延迟渲染 — 发送方：客户端复制文件后创建，响应服务端的 FileContentsReq
        private FileClipboardProvider _clipProvider;
        private readonly object _clipProviderLock = new object();

        // 文件剪贴板延迟渲染 — 接收方：收到服务端的 ClipFormatList 后创建，按需下载文件
        private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, FileClipboardConsumer> _clipConsumers
            = new System.Collections.Concurrent.ConcurrentDictionary<uint, FileClipboardConsumer>();

        // 图片剪贴板接收状态：transferId → 接收器
        private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, ImageClipboardReceiver> _imageReceivers
            = new System.Collections.Concurrent.ConcurrentDictionary<uint, ImageClipboardReceiver>();

        private void OnMessageReceived(object sender, MessageReceivedEventArgs e)
        {
            if (!_pipelineReady)
            {
                // 管线未就绪（握手响应处理中）：缓冲消息，InitPipeline 完成后按序回放，
                // 避免首个关键帧被丢弃导致解码器等待下一个 IDR。
                lock (_pendingLock)
                {
                    if (_pendingMessages.Count < MaxPendingMessages)
                    {
                        _pendingMessages.Add(e);
                    }
                    else if (!_pendingOverflowLogged)
                    {
                        // 仅记录一次，避免满缓冲期间刷屏；此类丢弃只发生在握手窗口异常拉长时
                        Logger.Warn("Pending message buffer full ({0}), discarding messages before pipeline ready",
                            MaxPendingMessages);
                        _pendingOverflowLogged = true;
                    }
                }
                return;
            }
            RouteMessage(e);
        }

        /// <summary>把一条完整消息路由到对应处理器。</summary>
        private void RouteMessage(MessageReceivedEventArgs e)
        {
            if (e.MessageType == (byte)MessageType.VideoFrame)
            {
                var msg = VideoFrameMessage.Unpack(e.Data);
                EnqueueVideoFrame(msg);
            }
            else if (e.MessageType == (byte)MessageType.ClipboardSync)
            {
                HandleClipboardSync(e.Data);
            }
            else if (e.MessageType == (byte)MessageType.ClipFormatList)
            {
                HandleClipFormatList(e.Data);
            }
            else if (e.MessageType == (byte)MessageType.ClipFileContentsReq)
            {
                HandleClipFileContentsReq(e.Data);
            }
            else if (e.MessageType == (byte)MessageType.ClipFileContentsRes)
            {
                HandleClipFileContentsRes(e.Data);
            }
            else if (e.MessageType == (byte)MessageType.ImageClipboardStart)
            {
                HandleImageClipboardStart(e.Data);
            }
            else if (e.MessageType == (byte)MessageType.ImageClipboardData)
            {
                HandleImageClipboardData(e.Data);
            }
            else if (e.MessageType == (byte)MessageType.ImageClipboardEnd)
            {
                HandleImageClipboardEnd(e.Data);
            }
        }

        /// <summary>
        /// 视频帧入队（接收线程）。单槽覆盖式：只保留最新未解码帧，
        /// 避免解码积压导致端到端延迟增长；待解码的关键帧不被普通帧覆盖，
        /// 防止解码器因丢关键帧而等到下一个 IDR 才恢复。
        /// </summary>
        private void EnqueueVideoFrame(VideoFrameMessage msg)
        {
            lock (_decodeLock)
            {
                // 覆盖规则：无待解码帧直接入槽；新帧是关键帧则必入；
                // 否则仅在待解码帧不是关键帧时覆盖（保留关键帧，避免解码器等下一个 IDR）
                bool replace = _pendingDecodeFrame == null
                    || msg.IsKeyframe
                    || !_pendingDecodeFrame.IsKeyframe;
                if (!replace)
                    return; // 待解码关键帧不因普通帧被覆盖，直接跳过新帧
                if (_pendingDecodeFrame != null)
                {
                    _decodeFrameDrops++;
                    if (_decodeFrameDrops == 1 || _decodeFrameDrops % 60 == 0)
                        Logger.Warn("Decode mailbox overwritten, stale frame dropped (total drops={0})", _decodeFrameDrops);
                }
                _pendingDecodeFrame = msg;
                Monitor.Pulse(_decodeLock);
            }
        }

        /// <summary>
        /// 解码线程主循环：始终处理最新帧，保持接收线程对控制消息的低延迟响应。
        /// 单消费者 + 顺序覆盖保证解码顺序单调递增。
        /// </summary>
        private void DecodeLoop()
        {
            while (_running)
            {
                VideoFrameMessage msg;
                lock (_decodeLock)
                {
                    while (_pendingDecodeFrame == null && _running)
                        Monitor.Wait(_decodeLock, 100);
                    if (!_running) break;
                    if (_pendingDecodeFrame == null) continue;
                    msg = _pendingDecodeFrame;
                    _pendingDecodeFrame = null;
                }

                try
                {
                    ProcessVideoFrame(msg);
                }
                catch (Exception ex)
                {
                    // 单帧解码异常不应杀死解码线程
                    Logger.Warn(ex, "ProcessVideoFrame failed on decode thread: seq={0}", msg.SequenceNumber);
                }
            }
        }

        /// <summary>
        /// 回放管线就绪前缓冲的消息（按到达顺序）。在 InitPipeline/Start 时调用，
        /// 此时位于 UI 线程（ConnectAsync 流程），仅回放握手窗口内的少量消息，开销可忽略。
        /// </summary>
        private void FlushPendingMessages()
        {
            System.Collections.Generic.List<MessageReceivedEventArgs> batch = null;
            lock (_pendingLock)
            {
                if (_pendingMessages.Count == 0) return;
                batch = new System.Collections.Generic.List<MessageReceivedEventArgs>(_pendingMessages);
                _pendingMessages.Clear();
            }
            if (batch == null) return;
            foreach (var e in batch)
            {
                RouteMessage(e);
            }
        }

        /// <summary>
        /// 回放管线就绪前缓冲的最新光标消息（含初始形状位图）。
        /// 由 ViewModel 在 IsConnected=true 后调用，确保 MainWindow 的光标处理在已连接状态下执行。
        /// </summary>
        public void FlushPendingCursor()
        {
            CursorUpdateMessage pending;
            lock (_pendingLock)
            {
                pending = _pendingCursor;
                _pendingCursor = null;
            }
            if (pending != null && _pipelineReady)
            {
                ProcessCursorUpdate(pending);
            }
        }

        /// <summary>
        /// 处理服务端发来的剪贴板同步消息，触发 ClipboardReceived 事件。
        /// 实际的 Clipboard.SetText 由 MainWindowViewModel 在 UI 线程执行。
        /// </summary>
        private void HandleClipboardSync(byte[] data)
        {
            try
            {
                var msg = ClipboardSyncMessage.Unpack(data);
                if (msg.Format == ClipboardSyncMessage.FormatText)
                {
                    string text = msg.Text;
                    if (!string.IsNullOrEmpty(text))
                    {
                        var handler = ClipboardReceived;
                        if (handler != null)
                        {
                            Logger.Info("Clipboard received from server: len={0}", text.Length);
                            handler(text);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "HandleClipboardSync unpack failed");
            }
        }

        /// <summary>
        /// 设置文件剪贴板延迟渲染发送方。客户端复制文件后调用，用于响应服务端的 FileContentsReq。
        /// </summary>
        public void SetFileClipboardProvider(FileClipboardProvider provider)
        {
            lock (_clipProviderLock)
            {
                if (_clipProvider != null)
                    _clipProvider.Dispose();
                _clipProvider = provider;
            }
        }

        /// <summary>
        /// 处理 ClipFormatList（延迟渲染）：服务端发来的文件元信息广播。
        /// 创建 FileClipboardConsumer 并启动按需下载，下载完成后触发 FileClipboardReceived 事件。
        /// </summary>
        private void HandleClipFormatList(byte[] data)
        {
            try
            {
                var msg = ClipFormatListMessage.Unpack(data);
                Logger.Info("ClipFormatList received: transferId={0} fileCount={1}",
                    msg.TransferId, msg.Files.Count);

                // 捕获 transport 局部变量，避免 Stop() 把 _transport 置 null 后，
                // Consumer 后台线程调用 sendAction 时出现 NullReferenceException
                var transport = _transport;
                if (transport == null)
                {
                    Logger.Warn("HandleClipFormatList: transport is null, cannot start download");
                    return;
                }

                var consumer = new FileClipboardConsumer(msg.TransferId, msg.Files, "client",
                    (sid, payload) =>
                    {
                        // 用局部变量 transport，而非字段 _transport
                        MessageReassembler.FragAndSend(0, (byte)MessageType.ClipFileContentsReq, payload,
                            (s, d) => transport.Send(d), 0);
                    },
                    localPaths =>
                    {
                        // 下载完成（无论成功失败）后从字典移除，避免长期运行内存累积
                        FileClipboardConsumer removed;
                        _clipConsumers.TryRemove(msg.TransferId, out removed);

                        Logger.Info("File clipboard download complete: transferId={0} files={1}",
                            msg.TransferId, localPaths != null ? localPaths.Length : 0);
                        if (localPaths != null && localPaths.Length > 0)
                        {
                            var handler = FileClipboardReceived;
                            if (handler != null)
                                handler(localPaths);
                        }
                    });

                _clipConsumers[msg.TransferId] = consumer;

                // 转发 Consumer 进度事件到本 Session 的 FileClipboardProgress 事件
                // 订阅者（如 MainWindowViewModel）用于更新 UI 进度条
                consumer.ProgressChanged += (downloaded, total) =>
                {
                    var handler = FileClipboardProgress;
                    if (handler != null)
                        handler(downloaded, total);
                };

                consumer.StartDownload();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "HandleClipFormatList failed");
            }
        }

        /// <summary>
        /// 处理 ClipFileContentsReq（延迟渲染）：服务端请求文件内容。
        /// 转发给 FileClipboardProvider 读取文件并响应。
        /// </summary>
        private void HandleClipFileContentsReq(byte[] data)
        {
            try
            {
                var msg = ClipFileContentsReqMessage.Unpack(data);
                FileClipboardProvider provider;
                lock (_clipProviderLock)
                {
                    provider = _clipProvider;
                    if (provider == null)
                    {
                        Logger.Warn("ClipFileContentsReq received but no provider: transferId={0}", msg.TransferId);
                        return;
                    }
                }
                // 文件读取与响应发送放到线程池：接收线程不应被磁盘 IO 阻塞
                // （否则同一 socket 上的视频帧/输入事件处理会被拖慢）
                System.Threading.ThreadPool.QueueUserWorkItem(state =>
                {
                    try { provider.HandleFileContentsReq(msg); }
                    catch (Exception ex) { Logger.Warn(ex, "HandleFileContentsReq failed on worker"); }
                });
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "HandleClipFileContentsReq failed");
            }
        }

        /// <summary>
        /// 处理 ClipFileContentsRes（延迟渲染）：服务端返回的文件内容块。
        /// 按 transferId 路由到对应的 FileClipboardConsumer。
        /// </summary>
        private void HandleClipFileContentsRes(byte[] data)
        {
            try
            {
                var msg = ClipFileContentsResMessage.Unpack(data);
                FileClipboardConsumer consumer;
                if (_clipConsumers.TryGetValue(msg.TransferId, out consumer))
                {
                    consumer.HandleFileContentsRes(msg);
                }
                else
                {
                    Logger.Warn("ClipFileContentsRes for unknown transferId={0}", msg.TransferId);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "HandleClipFileContentsRes failed");
            }
        }

        /// <summary>
        /// 处理 ImageClipboardStart：创建 ImageClipboardReceiver，准备接收 CF_DIB 数据块。
        /// </summary>
        private void HandleImageClipboardStart(byte[] data)
        {
            try
            {
                var msg = ImageClipboardStartMessage.Unpack(data);
                var receiver = new ImageClipboardReceiver(msg.TransferId, msg.TotalSize);
                _imageReceivers[msg.TransferId] = receiver;
                Logger.Info("ImageClipboardStart received: transferId={0} totalSize={1}",
                    msg.TransferId, msg.TotalSize);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "HandleImageClipboardStart failed");
            }
        }

        /// <summary>
        /// 处理 ImageClipboardData：写入 CF_DIB 数据块到指定偏移。
        /// </summary>
        private void HandleImageClipboardData(byte[] data)
        {
            try
            {
                var msg = ImageClipboardDataMessage.Unpack(data);
                ImageClipboardReceiver receiver;
                if (!_imageReceivers.TryGetValue(msg.TransferId, out receiver))
                {
                    Logger.Warn("ImageClipboardData for unknown transferId={0}", msg.TransferId);
                    return;
                }
                receiver.WriteChunk(msg.Offset, msg.Data, msg.DataLen);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "HandleImageClipboardData failed");
            }
        }

        /// <summary>
        /// 处理 ImageClipboardEnd：CF_DIB 数据接收完毕，触发 ImageClipboardReceived 事件。
        /// 订阅者（MainWindowViewModel）在 UI 线程调用 Clipboard.SetImage 设置 CF_DIB。
        /// </summary>
        private void HandleImageClipboardEnd(byte[] data)
        {
            try
            {
                var msg = ImageClipboardEndMessage.Unpack(data);
                ImageClipboardReceiver receiver;
                if (!_imageReceivers.TryRemove(msg.TransferId, out receiver))
                {
                    Logger.Warn("ImageClipboardEnd for unknown transferId={0}", msg.TransferId);
                    return;
                }
                byte[] dibBytes = receiver.Finish();
                Logger.Info("ImageClipboardEnd received: transferId={0} dibSize={1}",
                    msg.TransferId, dibBytes != null ? dibBytes.Length : 0);

                if (dibBytes != null && dibBytes.Length > 0)
                {
                    var handler = ImageClipboardReceived;
                    if (handler != null)
                        handler(dibBytes);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "HandleImageClipboardEnd failed");
            }
        }

        private void ProcessVideoFrame(VideoFrameMessage msg)
        {
            if (_frameBuffer == null) return;
            if (msg.Data == null || msg.Data.Length == 0)
            {
                Logger.Warn("VideoFrame empty data: seq={0} size={1}x{2} — skipped", msg.SequenceNumber, msg.Width, msg.Height);
                return;
            }

            // Resolution change
            if (_decoder != null && (msg.Width != FrameWidth || msg.Height != FrameHeight))
            {
                Logger.Info("Resolution changed: {0}x{1} -> {2}x{3}",
                    FrameWidth, FrameHeight, msg.Width, msg.Height);
                _decoder.Reset();
                _decoder.Initialize(msg.Width, msg.Height);
                _renderTarget?.Resize(msg.Width, msg.Height);
                // 通知上层同步坐标映射（输入会话与显示尺寸）
                // 注意：此处由解码线程触发，订阅者如需更新 UI 状态必须自行调度到 UI 线程。
                var resHandler = ResolutionChanged;
                if (resHandler != null)
                    resHandler(msg.Width, msg.Height);
            }

            int frameSize = msg.Width * msg.Height * 4;
            byte[] writeSlot = _frameBuffer.BorrowWriteBuffer(frameSize);
            if (writeSlot == null) return;

            if (_decoder == null)
            {
                // 解码器不可用 — 无法处理 H264 数据，丢弃此帧
                if (_frameCount == 0)
                {
                    Logger.Error("No decoder available, cannot decode H264 frame seq={0}", msg.SequenceNumber);
                    RaiseFatal("No video decoder available (codec: " + Codec + ")");
                }
                return;
            }

            var result = _decoder.Decode(msg.Data, writeSlot);
            if (result.Status != DecodeStatus.Ok)
            {
                _decodeFailures++;
                if (_decodeFailures <= 3 || _decodeFailures % 50 == 0)
                    Logger.Warn("Decode failed: status={0} seq={1} keyframe={2} dataLen={3} (total failures={4})",
                        result.Status, msg.SequenceNumber, msg.IsKeyframe, msg.Data.Length, _decodeFailures);
                if (_decodeFailures == 100)
                    RaiseFatal("Video decode failed repeatedly (" + _decodeFailures + " frames) - connection unusable");
                return;
            }

            _frameBuffer.CommitFrame(msg.Width, msg.Height);
            Interlocked.Increment(ref _frameCount);

            if (_frameCount == 1)
                Logger.Info("FIRST frame decoded: seq={0} size={1}x{2} keyframe={3} dataLen={4}",
                    msg.SequenceNumber, msg.Width, msg.Height, msg.IsKeyframe, msg.Data.Length);
            else if (_frameCount % 100 == 0)
                Logger.Debug("Frames decoded: {0}, last seq={1} dataLen={2}", _frameCount, msg.SequenceNumber, msg.Data.Length);
        }

        /// <summary>触发一次 FatalError（不可恢复故障，UI 层据此提示并断开）。</summary>
        private void RaiseFatal(string message)
        {
            if (Interlocked.CompareExchange(ref _fatalRaisedFlag, 1, 0) != 0) return;
            Logger.Error("FatalError: {0}", message);
            var handler = FatalError;
            if (handler != null)
            {
                try
                {
                    handler(this, new ErrorEventArgs(message, null));
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "FatalError handler threw");
                }
            }
        }

        private void ProcessCursorUpdate(CursorUpdateMessage msg)
        {
            if (_renderTarget == null) return;
            // 首次收到含形状的光标更新时记录一次，便于诊断"客户端看不到光标"类问题
            if (msg.RgbaPixels != null && msg.Width > 0 && msg.Height > 0 && !_cursorShapeLogged)
            {
                _cursorShapeLogged = true;
                Logger.Info("First cursor shape received: {0}x{1} hotspot={2},{3} pos={4},{5} pixels={6}",
                    msg.Width, msg.Height, msg.HotX, msg.HotY, msg.X, msg.Y, msg.RgbaPixels.Length);
            }
            _renderTarget.UpdateCursor(new CursorInfo
            {
                Visible = msg.Visible,
                X = msg.X,
                Y = msg.Y,
                Width = msg.Width,
                Height = msg.Height,
                HotX = msg.HotX,
                HotY = msg.HotY,
                RgbaPixels = msg.RgbaPixels
            });
        }

        // null op — actual receiving is event-driven
        private void ReceiveLoop()
        {
            while (_running)
            {
                Thread.Sleep(100);
            }
        }

        private void RenderLoop()
        {
            while (_running)
            {
                ReadFrameRef frame;
                if (_frameBuffer != null && _frameBuffer.TryBorrowReadFrame(out frame))
                {
                    try
                    {
                        _renderTarget?.RenderFrame(frame.Pixels, frame.Width, frame.Height);
                    }
                    finally
                    {
                        _frameBuffer.ReleaseReadFrame();
                    }
                }
                else
                {
                    Thread.Sleep(5); // 无帧时等待，降低 CPU 占用
                }
            }
        }
    }
}
