#nullable disable
using System;
using System.Threading;
using EasyRDP.Client.Wpf.Rendering;
using EasyRDP.Core.Protocol;
using EasyRDP.Core.Rendering;
using EasyRDP.Core.Session;
using EasyRDP.Core.Transport;
using NLog;

namespace EasyRDP.Client.Wpf.Services
{
    /// <summary>
    /// 客户端视频流会话。双线程：接收线程解码→FrameBuffer，渲染线程→RenderTarget。
    /// </summary>
    public class ClientStreamSession : IClientStreamSession
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private ITransport _transport;
        private IVideoDecoder _decoder;
        // D14 混合解码：服务端会话内按内容在 ZRLE/H264 间动态切换（帧头 Codec 字段），
        // 客户端收到首个 H264 帧时懒创建 H264 解码器；-1 表示未初始化/需重建。
        private IVideoDecoder _h264Decoder;
        private int _h264DecW = -1, _h264DecH = -1;
        private bool _h264DecoderUnavailable;   // H264 解码器创建失败（DLL 缺失），防每帧重试刷日志
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
        // 无法借到写槽导致帧未解码的次数（解码基线断裂，需请求全量帧）
        private int _frameBufferDrops;
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
        // 解码队列（接收线程入队、解码线程出队）。
        // ZRLE 是帧间增量编码：每帧只传相对上一帧的变化瓦片，中间丢任何一帧都会让
        // 后续增量叠到错误基线 —— 花屏会一直持续到下一个全量帧（约 1 分钟）。
        // 因此 ZRLE 帧必须按序全部解码；积压到上限时清空队列并请求全量帧重建基线，
        // 绝不能在增量链中间开口子。H264 可丢帧（参考链断了等 IDR 即可恢复），
        // 丢帧时同样主动请求 IDR，缩短花屏窗口。
        private readonly object _decodeLock = new object();
        private readonly System.Collections.Generic.Queue<VideoFrameMessage> _decodeQueue
            = new System.Collections.Generic.Queue<VideoFrameMessage>();
        private const int MaxQueuedZrleFrames = 6;
        private const int MaxQueuedH264Frames = 2;
        private int _decodeFrameDrops;
        private long _decodeQueueOverflows;
        // 服务端每个成功编码的帧 SequenceNumber 单调 +1。TCP 可靠传输下出现缺口
        // 只可能是服务端发送队列或本端解码队列丢帧 —— 增量链/参考链已断，
        // 必须立即请求全量帧，不能等周期性刷新。
        private long _expectedSequenceNumber = -1;
        private long _sequenceGaps;
        // 重同步等待：序列缺口/队列溢出后，增量帧的基线已不可信（队列里被丢掉的帧
        // 不会再解码），此时继续解码只会把错误像素提交给渲染层。因此在收到全量帧
        // （ZRLE 全帧 / H264 IDR）前丢弃增量帧；超时后放弃等待，避免服务端不响应
        // 全量请求时画面永久冻结（旧版本服务端）。
        private bool _awaitingKeyframe;
        private long _awaitingKeyframeSinceTicks;
        private long _framesDroppedAwaitingKeyframe;
        private const int KeyframeWaitTimeoutMs = 3000;
        // 解码脱同步恢复：连续解码失败时向服务端请求关键帧（IDR）。
        // 时间戳限速——out-of-sync 状态下每帧都失败，不能每帧都发请求（刷爆接收线程），
        // 仅在距上次请求超过冷却期时才再发，服务端收到即强制 IDR。
        private long _lastKeyframeRequestTicks;
        private const long KeyframeRequestCooldownMs = 500;

        // D16 残留花屏防护：渲染层若因 UI 线程繁忙未能及时借走上一帧（CommitFrame 返回 false
        // 或 WpfRenderTarget 内部丢帧），解码器内部参考帧已推进、RenderTarget 的 WriteableBitmap
        // 未更新 → 局部更新会只覆盖新帧的脏矩形，其余区域保留更旧像素。
        // 标志置位后下一帧强制整帧渲染（dirtyRects=null），把解码器当前完整画面一次性写回。
        // 用 int + Interlocked：置位可能发生在解码线程与 UI 线程两侧，Exchange 消费标志
        // 避免"读到 false 后又清除刚置位的新信号"。
        private int _forceFullRenderNextFlag;

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
        /// 避免服务端满速编码空帧空转占单核 CPU；画面变化时仍每帧立即请求保持流畅。
        /// D15 延迟优化：250→40ms。心跳周期是"静止→变化第一帧"的感知延迟上限
        /// （操作跟手度的决定因素）；空帧编码仅 ~8ms，40ms 心跳占弱机单核 ~20%，
        /// 换取画面变化最长 40ms 内被感知（原 250ms 是操作迟钝的主要来源）。</summary>
        private const int FrameRequestHeartbeatMs = 40;
        /// <summary>静止连击阈值（≥3 帧连续空帧）：心跳从 40ms 升到 100ms（中档）。
        /// 服务端静止时每条心跳都要做全帧瓦片比较 + 空帧编码发送（弱机常驻单核），
        /// 降频直接降低服务端空闲 CPU；变化帧立即归零计数回到 40ms。</summary>
        private const int StaticStreakMid = 3;
        /// <summary>静止连击阈值（≥10 帧连续空帧）：心跳再升到 250ms（深度静止档）。
        /// 此档位是"静止→变化"的感知延迟上限（服务端配合输入驱动强制采集，
        /// 输入发生后 250ms 内必然发出请求，画面不会残留）。</summary>
        private const int StaticStreakDeep = 10;
        /// <summary>中档心跳间隔（毫秒），见 StaticStreakMid。</summary>
        private const int StaticHeartbeatMs = 100;
        /// <summary>深度静止心跳间隔（毫秒），见 StaticStreakDeep。</summary>
        private const int DeepStaticHeartbeatMs = 250;
        /// <summary>连续静止（0 区域空帧）帧数计数：仅渲染线程读写，心跳阶梯依据。
        /// 变化帧（DirtyRects 非空/H264 全帧）立即归零。</summary>
        private int _staticFrameStreak;
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
            set
            {
                var previous = _renderTarget as WpfRenderTarget;
                if (previous != null)
                    previous.FrameDropped -= OnRenderFrameDropped;
                _renderTarget = value;
                var current = value as WpfRenderTarget;
                if (current != null)
                    current.FrameDropped += OnRenderFrameDropped;
            }
        }

        /// <summary>
        /// 渲染层丢弃了一帧（UI 线程繁忙 / 尺寸切换 / 位图不匹配）。
        /// 脏矩形渲染下丢失的像素不会被后续增量帧补上，必须让下一帧整帧重绘。
        /// </summary>
        private void OnRenderFrameDropped()
        {
            if (!_running) return;
            Interlocked.Exchange(ref _forceFullRenderNextFlag, 1);
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
            // 新会话：序列号基线未知（首帧建立），清掉上一会话残留的队列与等待状态
            _expectedSequenceNumber = -1;
            lock (_decodeLock)
            {
                _decodeQueue.Clear();
                _awaitingKeyframe = false;
                _framesDroppedAwaitingKeyframe = 0;
            }
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
            Logger.Info("ClientStreamSession stopping, framesDecoded={0} decodeFailures={1} renderBorrowed={2} renderEmpty={3} renderErrors={4}",
                _frameCount, _decodeFailures, _renderLoopBorrowed, _renderLoopEmpty, _renderLoopErrors);
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
                _decodeQueue.Clear();
                _awaitingKeyframe = false;
            }
            // 退订渲染丢帧通知：避免会话停止后仍持有对已释放渲染目标的引用
            var wpfTarget = _renderTarget as WpfRenderTarget;
            if (wpfTarget != null)
                wpfTarget.FrameDropped -= OnRenderFrameDropped;

            _decoder?.Dispose();
            _decoder = null;
            _h264Decoder?.Dispose();
            _h264Decoder = null;
            _h264DecW = -1;
            _h264DecH = -1;
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

        /// <summary>
        /// 服务端复制了文件（仅元信息到达）：VM 据此放置延迟渲染占位，
        /// 真正粘贴时再取 consumer 下载。
        /// </summary>
        public event Action<uint> FilePasteOffered;

        /// <summary>
        /// 文件通道发送委托（由 VM 注入，优先走独立通道；未注入时回退主连接）。
        /// 参数：MessageType 与 payload，返回是否已发送。
        /// </summary>
        public Func<byte, byte[], bool> FileChannelSend { get; set; }

        /// <summary>
        /// 收到文件传输协议消息（文件通道未建立时服务端会回退主连接）。
        /// 参数为 MessageType 字节与 payload，由文件传输会话解析。
        /// </summary>
        public event Action<byte, byte[]> FileTransferMessageReceived;

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

        /// <summary>把一条完整消息路由到对应处理器（单条损坏消息不影响接收线程）。</summary>
        private void RouteMessage(MessageReceivedEventArgs e)
        {
            try
            {
                RouteMessageCore(e);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "RouteMessage failed: type=0x{0:X2} len={1}",
                    e.MessageType, e.Data != null ? e.Data.Length : 0);
            }
        }

        /// <summary>消息路由实现。</summary>
        private void RouteMessageCore(MessageReceivedEventArgs e)
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
            else if (e.MessageType == (byte)MessageType.FileTransferOffer
                     || e.MessageType == (byte)MessageType.FileTransferStart
                     || e.MessageType == (byte)MessageType.FileTransferData
                     || e.MessageType == (byte)MessageType.FileTransferEnd
                     || e.MessageType == (byte)MessageType.FileTransferCancel
                     || e.MessageType == (byte)MessageType.FileTransferAck)
            {
                Action<byte, byte[]> handler = FileTransferMessageReceived;
                if (handler != null) handler(e.MessageType, e.Data);
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
        /// 视频帧入队（接收线程）。ZRLE 按序全部解码（增量链不可中断），
        /// H264 同样按序解码；一旦发生丢帧/积压，丢弃后续增量帧并等待全量帧重建基线。
        /// </summary>
        private void EnqueueVideoFrame(VideoFrameMessage msg)
        {
            if (msg == null || msg.Data == null || msg.Data.Length == 0)
                return;

            bool needResync = false;
            bool enqueue = true;
            lock (_decodeLock)
            {
                // 序列缺口：增量链/参考链已断，队列里的旧帧也失去意义
                if (_expectedSequenceNumber >= 0 && msg.SequenceNumber != _expectedSequenceNumber)
                {
                    _sequenceGaps++;
                    if (_sequenceGaps == 1 || _sequenceGaps % 30 == 0)
                        Logger.Warn("Video frame sequence gap: expected={0} got={1} codec={2} (total gaps={3})",
                            _expectedSequenceNumber, msg.SequenceNumber, msg.Codec, _sequenceGaps);
                    _decodeQueue.Clear();
                    // 缺口后的这一帧若非全量帧，跳过它等待服务端重建基线
                    if (!msg.IsKeyframe)
                    {
                        BeginAwaitingKeyframe();
                        needResync = true;
                    }
                }
                _expectedSequenceNumber = msg.SequenceNumber + 1;

                if (_awaitingKeyframe)
                {
                    if (msg.IsKeyframe)
                    {
                        _awaitingKeyframe = false;
                        if (_framesDroppedAwaitingKeyframe > 0)
                        {
                            Logger.Info("Full frame received after resync (seq={0}, codec={1}, dropped={2} frames)",
                                msg.SequenceNumber, msg.Codec, _framesDroppedAwaitingKeyframe);
                            _framesDroppedAwaitingKeyframe = 0;
                        }
                    }
                    else
                    {
                        long waitedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - _awaitingKeyframeSinceTicks)
                            * 1000 / System.Diagnostics.Stopwatch.Frequency;
                        if (waitedMs < KeyframeWaitTimeoutMs)
                        {
                            // 基线不可信：丢弃该增量帧，继续等全量帧
                            _framesDroppedAwaitingKeyframe++;
                            enqueue = false;
                        }
                        else
                        {
                            // 服务端迟迟未响应全量请求：放弃等待（画面冻结比局部错位更糟），
                            // 恢复增量解码并再次请求全量帧
                            _awaitingKeyframe = false;
                            needResync = true;
                            Logger.Warn("Keyframe wait timed out after {0}ms, resuming incremental decode (dropped={1} frames)",
                                waitedMs, _framesDroppedAwaitingKeyframe);
                        }
                    }
                }

                if (enqueue)
                {
                    // 全量帧（ZRLE 全帧 / H264 IDR）自身可独立解码：更旧的增量帧全部作废
                    if (msg.IsKeyframe && _decodeQueue.Count > 0)
                        _decodeQueue.Clear();

                    int limit = msg.Codec == CodecId.Zrle ? MaxQueuedZrleFrames : MaxQueuedH264Frames;
                    if (_decodeQueue.Count >= limit)
                    {
                        _decodeQueueOverflows++;
                        _decodeFrameDrops++;
                        if (_decodeQueueOverflows == 1 || _decodeQueueOverflows % 30 == 0)
                            Logger.Warn("Decode queue full ({0}), dropping backlog (codec={1}, total overflows={2} drops={3})",
                                limit, msg.Codec, _decodeQueueOverflows, _decodeFrameDrops);
                        // 积压到上限：整队作废。当前帧是增量帧时同样不能解码（基线已丢），
                        // 转入等待全量帧；全量帧则保留（自身完整）。
                        _decodeQueue.Clear();
                        if (!msg.IsKeyframe)
                        {
                            BeginAwaitingKeyframe();
                            _framesDroppedAwaitingKeyframe++;
                            enqueue = false;
                            needResync = true;
                        }
                    }

                    if (enqueue)
                    {
                        _decodeQueue.Enqueue(msg);
                        Monitor.Pulse(_decodeLock);
                    }
                }
            }
            // 锁外发送：RequestDecoderKeyframe 走 transport.Send（内部有发送锁），
            // 避免持解码锁等待网络/与接收线程互等
            if (needResync)
                RequestDecoderKeyframe();
        }

        /// <summary>进入"等待全量帧"状态（调用方必须持有 _decodeLock）。</summary>
        private void BeginAwaitingKeyframe()
        {
            if (!_awaitingKeyframe)
                _awaitingKeyframeSinceTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            _awaitingKeyframe = true;
        }

        /// <summary>
        /// 解码线程主循环：按接收顺序解码，保证 ZRLE 增量链完整。
        /// </summary>
        private void DecodeLoop()
        {
            while (_running)
            {
                VideoFrameMessage msg;
                lock (_decodeLock)
                {
                    while (_decodeQueue.Count == 0 && _running)
                        Monitor.Wait(_decodeLock, 100);
                    if (!_running) break;
                    if (_decodeQueue.Count == 0) continue;
                    msg = _decodeQueue.Dequeue();
                }

                try
                {
                    ProcessVideoFrame(msg);
                }
                catch (Exception ex)
                {
                    // 单帧解码异常不应杀死解码线程；解码器状态可能已错乱，请求全量帧
                    Logger.Warn(ex, "ProcessVideoFrame failed on decode thread: seq={0}", msg.SequenceNumber);
                    RequestDecoderKeyframe();
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
        /// 收到 ClipFormatList（延迟渲染）：服务端用户复制了文件，只登记元信息，
        /// 不立即下载；等本机真正粘贴（WM_RENDERFORMAT）或 Ctrl+V 时再由 VM 触发下载。
        /// </summary>
        private void HandleClipFormatList(byte[] data)
        {
            try
            {
                var msg = ClipFormatListMessage.Unpack(data);
                Logger.Info("ClipFormatList received (delayed): transferId={0} fileCount={1}",
                    msg.TransferId, msg.Files.Count);

                if (_transport == null)
                {
                    Logger.Warn("HandleClipFormatList: transport is null, cannot stage the transfer");
                    return;
                }

                var consumer = new FileClipboardConsumer(msg.TransferId, msg.Files, "client",
                    (sid, payload) =>
                    {
                        // 优先走文件通道（不与视频流抢带宽），未建立时回退主连接
                        var channelSend = FileChannelSend;
                        if (channelSend != null && channelSend((byte)MessageType.ClipFileContentsReq, payload))
                            return;
                        var transport = _transport;
                        if (transport != null)
                            transport.Send(Framing.BuildMessage((byte)MessageType.ClipFileContentsReq, payload));
                    },
                    localPaths =>
                    {
                        // 下载完成（无论成功失败）后从字典移除，避免长期运行内存累积
                        FileClipboardConsumer removed;
                        _clipConsumers.TryRemove(msg.TransferId, out removed);

                        Logger.Info("File clipboard download complete: transferId={0} files={1}",
                            msg.TransferId, localPaths != null ? localPaths.Length : 0);
                        // 进度收尾：通知 UI 传输结束（失败时 VM 会清除进度条）
                        var progressHandler = FileClipboardProgress;
                        if (progressHandler != null)
                            progressHandler(localPaths != null ? 1 : 0, 1);
                    });

                _clipConsumers[msg.TransferId] = consumer;

                consumer.ProgressChanged += (downloaded, total) =>
                {
                    var handler = FileClipboardProgress;
                    if (handler != null)
                        handler(downloaded, total);
                };

                // 不下载：由 VM 在剪贴板放置延迟渲染占位，粘贴时才调用 EnsureDownloaded
                var offered = FilePasteOffered;
                if (offered != null)
                    offered(msg.TransferId);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "HandleClipFormatList failed");
            }
        }

        /// <summary>取指定传输的延迟下载 consumer（VM 用于粘贴时按需下载）。</summary>
        public FileClipboardConsumer GetClipConsumer(uint transferId)
        {
            FileClipboardConsumer consumer;
            return _clipConsumers.TryGetValue(transferId, out consumer) ? consumer : null;
        }

        /// <summary>
        /// 文件通道上收到的剪贴板文件分块消息（服务端的 FileContentsRes 响应）：
        /// 转给与主连接相同的处理器，使延迟下载可以走独立通道。
        /// </summary>
        public void HandleClipboardChannelMessage(byte messageType, byte[] payload)
        {
            if (messageType == (byte)MessageType.ClipFileContentsReq)
                HandleClipFileContentsReq(payload);
            else if (messageType == (byte)MessageType.ClipFileContentsRes)
                HandleClipFileContentsRes(payload);
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
            // D14：协商解码器立即重建；H264 懒解码器由下方尺寸检查标记重建（_h264DecW=-1）。
            if (msg.Width != FrameWidth || msg.Height != FrameHeight)
            {
                Logger.Info("Resolution changed: {0}x{1} -> {2}x{3}",
                    FrameWidth, FrameHeight, msg.Width, msg.Height);
                if (_decoder != null)
                {
                    _decoder.Reset();
                    _decoder.Initialize(msg.Width, msg.Height);
                }
                _h264DecW = -1;
                _h264DecH = -1;
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
            if (writeSlot == null)
            {
                // 双槽都被占用：本帧根本没有送进解码器。服务端参考帧已推进，
                // 客户端解码基线停在上上帧 → 后续增量全部错位，必须请求全量帧重建。
                _frameBufferDrops++;
                if (_frameBufferDrops == 1 || _frameBufferDrops % 30 == 0)
                    Logger.Warn("BorrowWriteBuffer unavailable, frame not decoded (seq={0}, total drops={1})",
                        msg.SequenceNumber, _frameBufferDrops);
                RequestDecoderKeyframe();
                return;
            }

            // D14：按帧头 Codec 选择解码器（会话内 ZRLE/H264 动态切换）。
            // H264 解码器懒创建：首次收到 H264 帧时创建并 Initialize。
            IVideoDecoder decoder;
            if (msg.Codec == CodecId.H264Software || msg.Codec == CodecId.H264Hardware)
            {
                if (_decoder != null && _decoder.Codec == msg.Codec)
                {
                    // 协商即 H264 的旧路径：复用 InitPipeline 创建的解码器（尺寸重建已在上方完成）
                    decoder = _decoder;
                }
                else
                {
                    if (_h264Decoder == null)
                    {
                        if (_h264DecoderUnavailable)
                            return; // 已确认不可用，静默丢帧（错误只记录一次）
                        _h264Decoder = DecoderFactory.Create(CodecId.H264Software);
                        if (_h264Decoder == null)
                        {
                            _h264DecoderUnavailable = true;
                            Logger.Error("D14: H264Software decoder unavailable — H264 frames will be dropped " +
                                "(server switched to lossy codec for high-change content)");
                            return;
                        }
                        Logger.Info("D14: first H264Software frame received — lazy decoder created (seq={0})",
                            msg.SequenceNumber);
                        _h264DecW = -1; // 新建解码器必须 Initialize
                    }
                    if (_h264DecW != msg.Width || _h264DecH != msg.Height)
                    {
                        _h264Decoder.Reset();
                        _h264Decoder.Initialize(msg.Width, msg.Height);
                        _h264DecW = msg.Width;
                        _h264DecH = msg.Height;
                    }
                    decoder = _h264Decoder;
                }
            }
            else
            {
                decoder = _decoder;
            }

            if (decoder == null)
            {
                // 解码器不可用 — 无法处理数据，丢弃此帧
                if (_frameCount == 0)
                {
                    Logger.Error("No decoder available, cannot decode frame seq={0} codec={1}",
                        msg.SequenceNumber, msg.Codec);
                    RaiseFatal("No video decoder available (codec: " + Codec + ")");
                }
                return;
            }

            var result = decoder.Decode(msg.Data, writeSlot);
            if (result.Status != DecodeStatus.Ok)
            {
                if (result.Status == DecodeStatus.Failed)
                {
                    _decodeFailures++;
                    if (_decodeFailures <= 3 || _decodeFailures % 50 == 0)
                        Logger.Warn("Decode failed: status={0} seq={1} keyframe={2} dataLen={3} (total failures={4})",
                            result.Status, msg.SequenceNumber, msg.IsKeyframe, msg.Data.Length, _decodeFailures);
                    // 解码脱同步恢复：连续解码失败时向服务端请求关键帧（IDR / ZRLE 全量帧）。
                    // H264：P 帧丢参考帧后只能等 IDR 恢复；ZRLE：解码失败 = 本帧被丢弃，
                    // 客户端解码缓冲停在旧画面、服务端参考帧已推进 → 后续增量帧叠到旧基线
                    // 产生花屏。两种 codec 都请求全量重建（服务端对 ZRLE 请求同样强制
                    // 全量编码），1~2 帧内恢复画面。
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
            // D14：按帧头 codec 判断——H264 帧（或非 Zrle 帧）保持 dirtyRects=null → 全帧渲染。
            // D16：渲染层或提交环节丢过帧时，WriteableBitmap 与解码器已脱节，
            // 本帧必须放弃局部更新、用当前完整解码帧重写 RenderTarget。
            // 标志用 Exchange 消费：若消费后渲染层又丢帧，标志会重新置位留给下一帧。
            bool forceFull = Interlocked.Exchange(ref _forceFullRenderNextFlag, 0) != 0;
            ScreenRect[] dirtyRects = null;
            if (msg.Codec == CodecId.Zrle && msg.Data != null && !forceFull)
            {
                dirtyRects = ZrleRegionCodec.ExtractRects(msg.Data);
                if (_frameCount <= 10 || _frameCount % 100 == 0)
                {
                    int rectCount = dirtyRects != null ? dirtyRects.Length : -1;
                    Logger.Info("ExtractRects: seq={0} frameCount={1} dataLen={2} rects={3} forceFull={4}",
                        msg.SequenceNumber, _frameCount, msg.Data.Length, rectCount, forceFull);
                }
            }

            bool committed = _frameBuffer.CommitFrame(msg.Width, msg.Height, dirtyRects);
            if (!committed)
            {
                if (_frameCount <= 10 || _frameCount % 100 == 0)
                    Logger.Warn("CommitFrame DROPPED (reader busy): seq={0} frameCount={1}", msg.SequenceNumber, _frameCount);
                // RenderTarget 未拿到本帧，下一帧必须全量渲染以恢复同步。
                // 注意：解码器内部参考帧已推进，这里不能请求关键帧（链路本身没断）。
                Interlocked.Exchange(ref _forceFullRenderNextFlag, 1);
            }
            else if (forceFull && _frameCount <= 10)
            {
                Logger.Info("Full-frame redraw committed: seq={0} frameCount={1}", msg.SequenceNumber, _frameCount);
            }
            Interlocked.Increment(ref _frameCount);
            Interlocked.Add(ref _receivedBytes, msg.Data != null ? msg.Data.Length : 0);

            if (_frameCount == 1)
                Logger.Info("FIRST frame decoded: seq={0} size={1}x{2} keyframe={3} dataLen={4} dirtyRects={5}",
                    msg.SequenceNumber, msg.Width, msg.Height, msg.IsKeyframe, msg.Data.Length,
                    dirtyRects != null ? dirtyRects.Length : -1);
            else if (_frameCount % 100 == 0)
                Logger.Debug("Frames decoded: {0}, last seq={1} dataLen={2}", _frameCount, msg.SequenceNumber, msg.Data.Length);

            // 像素完整性诊断：ZRLE 解码还原是否完整无偏差（黑屏排查）。
            // 只统计前 3 帧 + 每 100 帧，抽样扫描（每 8 像素 × 每 8 行），
            // 单帧 <0.5ms 开销，不影响 FPS。
            if (_frameCount <= 3 || _frameCount % 100 == 0)
            {
                long nonBlack = CountNonBlackPixels(writeSlot, msg.Width, msg.Height);
                // D14：codec 打印帧头实际编码器（混合模式下 ZRLE/H264 交替可见）
                Logger.Info("Decode pixel check: frameCount={0} seq={1} codec={2} nonBlackRatio={3:P1} (sample={4}/{5})",
                    _frameCount, msg.SequenceNumber, msg.Codec,
                    (double)nonBlack / Math.Max(1, ((long)msg.Width * msg.Height) / 64),
                    nonBlack, ((long)msg.Width * msg.Height) / 64);
            }
        }

        /// <summary>
        /// 抽样统计 BGRA 像素中"非黑"像素数量（B/G/R 任一非零即非黑）。
        /// 抽样步长 8 像素 × 8 行，覆盖全帧分布（避免只数到边缘留黑区导致的误判）。
        /// 用于诊断 ZRLE 解码还原是否完整：若首帧非黑比例 <5%，说明还原产物基本全黑。
        /// </summary>
        private static long CountNonBlackPixels(byte[] bgra, int width, int height)
        {
            if (bgra == null || width <= 0 || height <= 0)
                return 0;
            long count = 0;
            int stride = width * 4;
            int step = 8;
            for (int y = 0; y < height; y += step)
            {
                int rowBase = y * stride;
                for (int x = 0; x < width; x += step)
                {
                    int o = rowBase + x * 4;
                    if (bgra[o] != 0 || bgra[o + 1] != 0 || bgra[o + 2] != 0)
                        count++;
                }
            }
            return count;
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

        // 渲染线程诊断计数
        private long _renderLoopBorrowed;    // 成功借到帧的次数
        private long _renderLoopEmpty;       // 无帧可借的次数
        private long _renderLoopErrors;      // 渲染异常次数

        private void RenderLoop()
        {
            Logger.Info("RenderLoop thread started, flowControl={0}", _flowControlEnabled);
            while (_running)
            {
                ReadFrameRef frame;
                if (_frameBuffer != null && _frameBuffer.TryBorrowReadFrame(out frame))
                {
                    Interlocked.Increment(ref _renderLoopBorrowed);
                    long borrowed = Interlocked.Read(ref _renderLoopBorrowed);
                    if (borrowed <= 5 || borrowed % 100 == 0)
                    {
                        int rectCount = frame.DirtyRects != null ? frame.DirtyRects.Length : -1;
                        Logger.Info("RenderLoop borrow#{0}: seq={1} w={2} h={3} dirtyRects={4} pixelsLen={5}",
                            borrowed, frame.Sequence, frame.Width, frame.Height, rectCount,
                            frame.Pixels != null ? frame.Pixels.Length : 0);
                    }
                    try
                    {
                        // 阶段二：携带 DirtyRects 调用局部更新重载。
                        // ZRLE 无变化帧（0 区域）→ 渲染层跳过；H264（null）→ 全帧渲染。
                        if (_renderTarget != null)
                        {
                            _renderTarget.RenderFrame(frame.Pixels, frame.Width, frame.Height, frame.DirtyRects);
                        }
                        else
                        {
                            if (borrowed <= 3)
                                Logger.Warn("RenderLoop borrow#{0}: _renderTarget is NULL!", borrowed);
                        }
                        // 阶段三：画面有变化 → 渲染完成后立即请求下一帧（保持流畅）。
                        // 服务端等请求才编码发送 → 帧率 = 客户端消费能力，不积压不丢帧。
                        if (_flowControlEnabled)
                        {
                            // D14 修复（FPS 主瓶颈）：DirtyRects==null 是 H264 全帧渲染帧
                            // （服务端实际编码了新内容），渲染完必须立即请求下一帧。
                            // 旧判定把 null 当"无变化"→ H264 模式只靠 250ms 心跳驱动，
                            // 帧率被锁死在 ~4 FPS（日志实证：H264 编码仅 35ms/帧，
                            // 帧间隔却 250-300ms，等于心跳周期）。
                            // ZRLE 空帧（Length==0，4 字节保活帧）仍走心跳，不空转打满。
                            bool hasChanges = frame.DirtyRects == null || frame.DirtyRects.Length > 0;
                            // 静止连击计数（心跳自适应阶梯依据）：变化帧立即归零，
                            // 连续空帧爬阶梯——服务端静止时仍要为每条心跳做全帧瓦片
                            // 比较 + 空帧 Pack/发送（弱机常驻单核 30-60%），阶梯降频
                            // 在"静止→变化"感知延迟（最长 250ms）与服务端空闲 CPU 间折中。
                            _staticFrameStreak = hasChanges ? 0 : _staticFrameStreak + 1;
                            if (hasChanges)
                            {
                                SendFramebufferUpdateRequest();
                                _lastFrameRequestTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _renderLoopErrors);
                        Logger.Error(ex, "RenderLoop EXCEPTION borrow#{0}: {1}", borrowed, ex.Message);
                        // 不 throw：渲染线程异常退出会导致永远黑屏，必须吞掉继续
                    }
                    finally
                    {
                        _frameBuffer.ReleaseReadFrame();
                    }
                }
                else
                {
                    Interlocked.Increment(ref _renderLoopEmpty);
                    long empty = Interlocked.Read(ref _renderLoopEmpty);
                    if (empty == 1)
                        Logger.Info("RenderLoop: no frame available (first time, normal during initial wait)");
                    // 事件驱动等待：CommitFrame 提交新帧时 PulseAll 唤醒（FrameBuffer.WaitForFrame），
                    // 替代原固定 Thread.Sleep(5) 空轮询（实测 4 分钟会话空轮询 33401 次，
                    // 约 139 次/秒纯 CPU 浪费）。40ms 超时兜底：万一错过 Pulse，
                    // 仍保证下方心跳判定以基础节奏运行。
                    if (_frameBuffer != null)
                        _frameBuffer.WaitForFrame(40);
                    else
                        Thread.Sleep(5); // 管线尚未初始化（首帧前）：退回短睡
                }

                // 阶段三：心跳请求（每轮检查，与是否借到帧无关）。
                // 静帧（服务端回 0 区域空帧、画面无变化）时维持低帧率空帧轮询：
                // 若把心跳放进借帧分支，静帧时客户端不再发请求 → 服务端 1s 超时后
                // 不再推帧 → 客户端永远等不到新帧 → 画面变化永久不更新（僵局）。
                // 心跳保证任何时刻画面变化在 ≤ 心跳周期内被客户端感知并请求更新。
                // 自适应阶梯（不降清晰度的服务端省 CPU 手段）：
                //   活跃（静止连击 <3）→ 40ms：变化期帧率优先；
                //   静止连击 [3,10) → 100ms：服务端每秒少做 ~15 次全帧瓦片比较；
                //   深度静止（≥10）→ 250ms：服务端静止 CPU 降 50-90%（弱机收益最大）。
                // 任何变化帧立即归零阶梯（_staticFrameStreak=0），下一帧请求即刻回到 40ms。
                if (_flowControlEnabled)
                {
                    long now = System.Diagnostics.Stopwatch.GetTimestamp();
                    int heartbeatMs = _staticFrameStreak < StaticStreakMid
                        ? FrameRequestHeartbeatMs
                        : (_staticFrameStreak < StaticStreakDeep ? StaticHeartbeatMs : DeepStaticHeartbeatMs);
                    long heartbeatTicks = (long)heartbeatMs * System.Diagnostics.Stopwatch.Frequency / 1000;
                    if (now - _lastFrameRequestTicks >= heartbeatTicks)
                    {
                        SendFramebufferUpdateRequest();
                        _lastFrameRequestTicks = now;
                    }
                }
            }
            Logger.Info("RenderLoop thread exiting, borrowed={0} empty={1} errors={2}",
                _renderLoopBorrowed, _renderLoopEmpty, _renderLoopErrors);
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

        /// <summary>
        /// 捕获当前帧像素的只读副本（用于缩略图）。线程安全：从 FrameBuffer 借读帧并
        /// 立即复制到托管数组，不阻塞渲染循环。
        /// </summary>
        public bool TryCaptureFrame(out int width, out int height, out byte[] bgra)
        {
            width = 0; height = 0; bgra = null;
            if (_frameBuffer == null) return false;
            ReadFrameRef frame;
            if (!_frameBuffer.TryBorrowReadFrame(out frame)) return false;
            try
            {
                if (frame.Pixels == null || frame.Pixels.Length == 0) return false;
                width = frame.Width;
                height = frame.Height;
                bgra = new byte[frame.Pixels.Length];
                System.Buffer.BlockCopy(frame.Pixels, 0, bgra, 0, bgra.Length);
                return true;
            }
            finally
            {
                _frameBuffer.ReleaseReadFrame();
            }
        }
    }
}
