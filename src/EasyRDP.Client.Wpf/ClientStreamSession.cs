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

        private ITransport _transport;
        private IVideoDecoder _decoder;
        private FrameBuffer _frameBuffer;
        private IRenderTarget _renderTarget;
        private volatile bool _running;
        // 0=未触发, 1=已触发；用 Interlocked 保证跨线程只触发一次
        private int _fatalRaisedFlag;
        private Thread _renderThread;
        // 解码专用线程：视频帧解码不再占用 TCP 接收线程，
        // 避免 CursorUpdate/Clipboard 等控制消息被解码阻塞（否则鼠标回显延迟随解码耗时增长）。
        private Thread _decodeThread;
        private long _frameCount;
        // 内容坐标空间尺寸（物理屏幕，鼠标映射基准）。与服务端 SetCursorPos 坐标空间一致，
        // 不随编码分辨率（D11 降采样）变化。InitPipeline 以握手分辨率初始化，
        // 之后由每帧 ContentWidth/ContentHeight 校正（旧服务端缺字段时回退到帧尺寸）。
        private int _contentWidth;
        private int _contentHeight;
        // 已接收视频帧压缩数据总字节（诊断码率统计用，Interlocked 累加）
        private long _receivedBytes;
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
        private bool _pendingOverflowLogged;        // 管线（RenderTarget）就绪前到达的光标消息：服务端在 HandshakeRes 之前就已启动光标会话，
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
        // 解码脱同步恢复：连续解码失败时向服务端请求关键帧（IDR）。
        // 时间戳限速——out-of-sync 状态下每帧都失败，不能每帧都发请求（刷爆接收线程），
        // 仅在距上次请求超过冷却期时才再发，服务端收到即强制 IDR。
        private long _lastKeyframeRequestTicks;
        private const long KeyframeRequestCooldownMs = 500;

        // 最近一次 RTT 测量值（毫秒）。由接收线程在 Keepalive 回显到达时写入，
        // 诊断/流控线程读取；volatile 保证可见性。未测到为 -1。
        private volatile int _lastRttMs = -1;

        /// <summary>最近一次 Keepalive 往返时延（毫秒），-1 表示尚未测到。</summary>
        public int LastRttMs { get { return _lastRttMs; } }

        /// <summary>收到服务端诊断信息时触发（接收线程）。供连接详情面板展示。</summary>
        public event Action<DiagnosticInfoMessage> DiagnosticInfoReceived;

        /// <summary>服务端分辨率变化事件（解码线程触发，用于同步客户端坐标映射与显示尺寸）。</summary>
        public event Action<int, int> ResolutionChanged;

        /// <summary>Gets the negotiated video codec used for decoding.</summary>
        public CodecId Codec { get; private set; }
        // 阶段三：客户端请求驱动流控标志（仅 ZRLE 模式启用）。
        // InitPipeline 按协商 codec 设置；启用后渲染线程每渲染完一帧即发送
        // FramebufferUpdateRequest，服务端等请求才编码发送下一帧。
        private volatile bool _flowControlEnabled;
        // 上次发送帧请求的时间戳（Stopwatch ticks）：静态场景心跳限速用。
        // 注：C# 不允许 volatile long，故不加 volatile；InitPipeline（接收线程）写、
        // RenderLoop（渲染线程）读的撕裂后果仅是首帧前后多/少一个心跳请求，
        // 服务端首帧无条件推送兜底，实际无害。
        private long _lastFrameRequestTicks;
        /// <summary>静态场景帧请求心跳间隔（毫秒）：画面无变化（0 区域空帧）时降频请求，
        /// 避免服务端满速编码空帧空转占单核 CPU；画面变化时仍每帧立即请求保持流畅。</summary>
        private const int FrameRequestHeartbeatMs = 250;
        /// <summary>Gets the current frame width in pixels.</summary>
        public int FrameWidth { get { return _frameBuffer != null ? _frameBuffer.Width : 0; } }
        /// <summary>Gets the current frame height in pixels.</summary>
        public int FrameHeight { get { return _frameBuffer != null ? _frameBuffer.Height : 0; } }
        /// <summary>Gets the total number of frames received and processed.</summary>
        public long FrameCount { get { return _frameCount; } }

        /// <summary>已接收视频帧压缩数据总字节（码率统计数据源）。</summary>
        public long ReceivedBytes { get { return Interlocked.Read(ref _receivedBytes); } }

        /// <summary>传输层丢帧率（0~1）。重构后 TCP 传输无应用层分片重组，恒为 0（UDP 后端实现后由实现层上报）。</summary>
        public double PacketLossRate
        {
            get { return 0.0; }
        }

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
            // 阶段三：仅 ZRLE 模式启用请求驱动流控（H264 保持服务端推送）
            _flowControlEnabled = (codec == CodecId.Zrle);
            // 初始化心跳时间戳：RenderLoop 首轮即可按需发请求（首帧前多一次请求无害）
            _lastFrameRequestTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            // 版本诊断标识：确认运行的客户端二进制包含 1 字节请求 payload 修复。
            // 若日志无此行或 requestPayloadFix != v2-1byte-payload，说明是旧构建。
            Logger.Info("=== EasyRDP Client build: {0} requestPayloadFix={1} keyframeFix={2} ===",
                EasyRDP.Core.Diagnostics.BuildInfo.Describe(),
                EasyRDP.Core.Diagnostics.BuildInfo.RequestPayloadFixVersion,
                EasyRDP.Core.Diagnostics.BuildInfo.KeyframeRequestFixVersion);
            Logger.Info("InitPipeline: codec={0} resolution={1}x{2}", codec, width, height);
            _decoder = DecoderFactory.Create(codec);
            if (_decoder != null)
                _decoder.Initialize(width, height);
            else
                Logger.Error("InitPipeline: decoder not available for codec {0} — H264 decoding is mandatory", codec);
            _frameBuffer = new FrameBuffer();
            // 预设置帧尺寸：否则 FrameWidth=0，首帧必触发一次无谓的解码器重建
            _frameBuffer.SetSize(width, height);
            // 内容坐标空间初始化为握手分辨率（全分辨率下与物理屏幕一致，仅偶对齐 1px 差）
            _contentWidth = width;
            _contentHeight = height;
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
        public void BeginReceive(ITransport transport)
        {
            lock (_pendingLock)
            {
                if (_receiving) return;
                _transport = transport;
                _receiving = true;
                _transport.MessageReceived += OnMessageReceived;
            }
        }

        /// <summary>Starts the stream session: begins receiving, decoding, and rendering frames.</summary>
        public void Start(ITransport transport)
        {
            if (_running) return;
            if (!_receiving)
                BeginReceive(transport);
            else
                _transport = transport;
            _running = true;

            // 接收已由 ITransport 内部接收线程事件驱动（MessageReceived），
            // 本会话无需自建接收线程，只保留解码 + 渲染两个线程。
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
                _transport.MessageReceived -= OnMessageReceived;
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

        /// <summary>处理光标更新消息（完整 payload，无分片头/CRC）。</summary>
        private void HandleCursorUpdate(byte[] payload)
        {
            try
            {
                var msg = CursorUpdateMessage.Unpack(payload);
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
            else if (e.MessageType == (byte)MessageType.CursorUpdate)
            {
                HandleCursorUpdate(e.Data);
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
            else if (e.MessageType == (byte)MessageType.Keepalive)
            {
                // RTT 测量：客户端发出的 Keepalive 携带发送时刻时间戳（8 字节 UtcNow.Ticks），
                // 服务端原样回显。收到即计算往返时延。服务端自身 30s 心跳探测的
                // 空 payload Keepalive 不在此列（长度 < 8 直接忽略）。
                if (e.Data != null && e.Data.Length >= 8)
                {
                    long sentTicks = BitConverter.ToInt64(e.Data, 0);
                    long rttTicks = DateTime.UtcNow.Ticks - sentTicks;
                    if (rttTicks >= 0)
                        _lastRttMs = (int)(rttTicks / TimeSpan.TicksPerMillisecond);
                }
            }
            else if (e.MessageType == (byte)MessageType.DiagnosticInfo)
            {
                // 连接详情面板：服务端响应 DiagnosticInfoRequest，携带系统信息。
                try
                {
                    DiagnosticInfoMessage diag = DiagnosticInfoMessage.Unpack(e.Data);
                    Action<DiagnosticInfoMessage> handler = DiagnosticInfoReceived;
                    if (handler != null)
                        handler(diag);
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "DiagnosticInfo unpack failed");
                }
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
                        transport.Send(Framing.BuildMessage((byte)MessageType.ClipFileContentsReq, payload));
                    },
                    localPaths =>
                    {
                        // 下载完成（无论成功失败）后从字典移除，避免长期运行内存累积
                        FileClipboardConsumer removed;
                        _clipConsumers.TryRemove(msg.TransferId, out removed);

                        Logger.Info("File clipboard download complete: transferId={0} files={1}",
                            msg.TransferId, localPaths != null ? localPaths.Length : 0);
                        // 无论成功失败都触发（空数组=全部失败）：VM 据此清除"传输中"状态
                        var handler = FileClipboardReceived;
                        if (handler != null)
                            handler(localPaths);
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

            // 编码/显示分辨率变化：重建解码器与渲染目标（D11 降采样后帧尺寸变化）。
            // 只影响显示尺寸，不影响鼠标映射（映射用内容坐标空间）。
            if (_decoder != null && (msg.Width != FrameWidth || msg.Height != FrameHeight))
            {
                Logger.Info("Resolution changed: {0}x{1} -> {2}x{3}",
                    FrameWidth, FrameHeight, msg.Width, msg.Height);
                _decoder.Reset();
                _decoder.Initialize(msg.Width, msg.Height);
                _renderTarget?.Resize(msg.Width, msg.Height);
            }

            // 内容坐标空间变化（物理屏幕尺寸）：通知上层更新鼠标映射空间。
            // 必须用 Content*（服务端 SetCursorPos 坐标空间），不能随编码分辨率降采样而变，
            // 否则 D11 降档后鼠标落点整体缩放偏移。旧服务端无 Content* 字段时回退到帧尺寸。
            int contentW = msg.ContentWidth > 0 ? msg.ContentWidth : msg.Width;
            int contentH = msg.ContentHeight > 0 ? msg.ContentHeight : msg.Height;
            if (contentW != _contentWidth || contentH != _contentHeight)
            {
                Logger.Info("Content resolution changed: {0}x{1} -> {2}x{3}",
                    _contentWidth, _contentHeight, contentW, contentH);
                _contentWidth = contentW;
                _contentHeight = contentH;
                var resHandler = ResolutionChanged;
                if (resHandler != null)
                    resHandler(contentW, contentH);
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
                if (result.Status == DecodeStatus.Failed)
                {
                    _decodeFailures++;
                    if (_decodeFailures <= 3 || _decodeFailures % 50 == 0)
                        Logger.Warn("Decode failed: status={0} seq={1} keyframe={2} dataLen={3} (total failures={4})",
                            result.Status, msg.SequenceNumber, msg.IsKeyframe, msg.Data.Length, _decodeFailures);
                    // 解码脱同步恢复：P 帧丢失参考帧（dsRefLost/dsNoParamSets）后后续 P 帧持续失败，
                    // 只能等周期性 IDR 恢复（低帧率下 10~15s，长时间黑屏）。立即请求 IDR，
                    // 服务端收到后强制生成关键帧，1~2 帧内恢复画面。
                    if (Codec == CodecId.H264Software || Codec == CodecId.H264Hardware)
                        RequestDecoderKeyframe();
                    if (_decodeFailures == 100)
                        RaiseFatal("Video decode failed repeatedly (" + _decodeFailures + " frames) - connection unusable");
                }
                // NeedMoreInput：解码成功但本帧无输出（H264 参考帧缓冲），非错误，不计失败不请求 IDR。
                return;
            }

            // 解码成功：重置连续失败计数（单次成功可能已恢复，避免累计误报 fatal）
            if (_decodeFailures > 0)
                _decodeFailures = 0;

            // 阶段二：ZRLE 帧提取脏矩形列表随帧提交（渲染层据此局部更新）。
            // ExtractRects 只解析区域头部（不解压数据），开销可忽略。
            // H264 帧（Codec != Zrle）保持 dirtyRects=null → 渲染层回退全帧渲染。
            ScreenRect[] dirtyRects = null;
            if (Codec == CodecId.Zrle && msg.Data != null)
            {
                dirtyRects = ZrleRegionCodec.ExtractRects(msg.Data);
            }

            _frameBuffer.CommitFrame(msg.Width, msg.Height, dirtyRects);
            Interlocked.Increment(ref _frameCount);
            Interlocked.Add(ref _receivedBytes, msg.Data != null ? msg.Data.Length : 0);

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

        private void RenderLoop()
        {
            while (_running)
            {
                ReadFrameRef frame;
                if (_frameBuffer != null && _frameBuffer.TryBorrowReadFrame(out frame))
                {
                    try
                    {
                        // 阶段二：携带 DirtyRects 调用局部更新重载。
                        // ZRLE 无变化帧（0 区域）→ 渲染层跳过；H264（null）→ 全帧渲染。
                        _renderTarget?.RenderFrame(frame.Pixels, frame.Width, frame.Height, frame.DirtyRects);
                        // 阶段三：画面有变化 → 渲染完成后立即请求下一帧（保持流畅）。
                        // 服务端等请求才编码发送 → 帧率 = 客户端消费能力，不积压不丢帧。
                        if (_flowControlEnabled)
                        {
                            bool hasChanges = frame.DirtyRects != null && frame.DirtyRects.Length > 0;
                            if (hasChanges)
                            {
                                SendFramebufferUpdateRequest();
                                _lastFrameRequestTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                            }
                        }
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

                // 阶段三：心跳请求（每轮检查，与是否借到帧无关）。
                // 静帧（服务端回 0 区域空帧、画面无变化）时维持低帧率空帧轮询：
                // 若把心跳放进借帧分支，静帧时客户端不再发请求 → 服务端 1s 超时后
                // 不再推帧 → 客户端永远等不到新帧 → 画面变化永久不更新（僵局）。
                // 心跳保证任何时刻画面变化在 ≤250ms 内被客户端感知并请求更新。
                if (_flowControlEnabled)
                {
                    long now = System.Diagnostics.Stopwatch.GetTimestamp();
                    long heartbeatTicks = FrameRequestHeartbeatMs * System.Diagnostics.Stopwatch.Frequency / 1000;
                    if (now - _lastFrameRequestTicks >= heartbeatTicks)
                    {
                        SendFramebufferUpdateRequest();
                        _lastFrameRequestTicks = now;
                    }
                }
            }
        }

        /// <summary>
        /// 发送帧请求消息（阶段三流控）：通知服务端客户端已消费完上一帧、可以编码下一帧。
        /// 在渲染线程调用。
        /// payload 内容无意义（服务端不解析）。重构后空 payload 也支持，保留 1 字节占位保守起见。
        /// </summary>
        private void SendFramebufferUpdateRequest()
        {
            try
            {
                var transport = _transport;
                if (transport == null) return;
                // 1 字节占位 payload（服务端不解析内容；重构后空 payload 也支持，保留占位保守起见）
                transport.Send(Framing.BuildMessage((byte)MessageType.FramebufferUpdateRequest, new byte[] { 0 }));
            }
            catch (Exception ex)
            {
                // 发送失败（如连接已断开）：静默记录，Stop 流程会清理会话
                Logger.Warn(ex, "SendFramebufferUpdateRequest failed");
            }
        }

        /// <summary>
        /// 解码脱同步恢复：向服务端请求关键帧（IDR）。（解码线程调用）
        /// H264 解码器丢参考帧（P 帧链断裂）后除非收到新 IDR 否则持续失败，
        /// 而周期性 IDR（服务端 KeyframeInterval/静态跳过）在低帧率下间隔可达 10~15s，
        /// 表现为长时间黑屏/卡帧。收到本请求后服务端下次编码强制 IDR，1~2 帧内恢复画面。
        /// 限速：out-of-sync 时每帧都失败，若每帧都发请求会刷爆接收线程，冷却期内不重复发。
        /// </summary>
        private void RequestDecoderKeyframe()
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            long cooldown = KeyframeRequestCooldownMs * System.Diagnostics.Stopwatch.Frequency / 1000;
            if (now - _lastKeyframeRequestTicks < cooldown)
                return;
            _lastKeyframeRequestTicks = now;

            var transport = _transport;
            if (transport == null) return;
            Logger.Info("Video decode out of sync (failures={0}), requesting keyframe IDR", _decodeFailures);
            try
            {
                // 占位 payload（服务端不解析内容；1 字节绕过空分片保护丢弃，与 FramebufferUpdateRequest 一致）
                transport.Send(Framing.BuildMessage((byte)MessageType.VideoKeyframeRequest, new byte[] { 0 }));
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "RequestDecoderKeyframe send failed");
            }
        }
    }
}
