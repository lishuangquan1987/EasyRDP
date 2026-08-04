#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using EasyDesk.Core.Models;
using EasyRDP.Core.Protocol;
using EasyRDP.Core.Services;
using EasyRDP.Core.Session;
using EasyRDP.Core.Transport;
using NLog;

namespace EasyRDP.Server.Wpf
{
    /// <summary>
    /// 服务端视频流会话。三线程模型：截屏回调（CaptureService 线程）→ 编码线程 → 发送线程。
    /// 两级有界队列：_frameQueue（截屏→编码）、_sendQueue（编码→发送）。
    /// </summary>
    public class ServerStreamSession : IServerStreamSession
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private readonly ICaptureService _captureService;
        private readonly Action<uint, byte[]> _sendTo;
        private readonly ICursorTracker _cursorTracker;
        private readonly object _lock = new object();

        private uint _sessionId;
        private CodecId _codec;
        private IVideoEncoder _encoder;
        private volatile bool _running;
        private volatile bool _stopping;
        // 0=未触发, 1=已触发；用 Interlocked 保证跨线程只触发一次
        private int _fatalRaisedFlag;

        // Two-level queues
        private Queue<CapturedFrame> _frameQueue = new Queue<CapturedFrame>();
        private int _frameQueueCapacity = 2;
        private Queue<FrameToSend> _sendQueue = new Queue<FrameToSend>();
        private int _sendQueueCapacity = 2;

        // Capture buffers with ownership tracking: a buffer is only reused after the
        // encode thread has finished reading it. Plain A/B alternation could overwrite
        // a buffer the encoder is still reading when encode takes longer than 2 captures.
        private readonly byte[][] _captureBufs = new byte[2][];
        private readonly bool[] _captureBufInUse = new bool[2];
        private int _lastW, _lastH;
        // 内容坐标空间尺寸（帧尺寸，可能大于编码尺寸）
        private int _contentW, _contentH;
        // 编码降采样缓冲：捕获帧全分辨率存入 _captureBufs，编码前降采样到 _lastW×_lastH
        private byte[] _encodeBuf;

        // Sequence
        private long _sequenceNumber;

        // D11 adaptive
        private Queue<long> _encodeTimes = new Queue<long>();
        private long _encodeSum;
        private const int AdaptiveWindow = 30;
        // 编码分辨率上限与 CaptureService.CaptureMaxWidth 共用同一常量。
        // 0 = 不降分辨率（当前默认，用户明确要求全分辨率画面）；
        // >0 时截屏直接按该尺寸 StretchBlt 缩放，编码器不再做第二遍软件缩放。
        private const int MaxEncodeWidth = CaptureService.CaptureMaxWidth;

        // D12 global load
        private volatile int _globalLoadLevel;

        // Diagnostics counters
        private int _consecutiveEncodeFailures;
        private int _sendQueueDrops;
        private int _captureQueueDrops;
        private long _framesEncoded;
        private long _framesSent;
        // 静态帧检测：上次成功编码的 BGRA 缓冲，用于跳过内容未变化的帧
        // （光标由 CursorTracker 单独同步、不在画面内，鼠标移动不会触发视频重编码）。
        private byte[] _prevBgra;
        // 连续跳过的帧数：恢复编码时强制关键帧，避免长间隔后解码漂移
        private int _framesSkipped;

        // Cursor session
        private ICursorTrackerSession _cursorSession;

        // Threads
        private Thread _encodeThread;
        private Thread _sendThread;

        // Properties
        public CodecId Codec { get { return _codec; } }
        public int FrameDelayMs { get; set; }
        public int KeyframeInterval { get; set; }
        public int TargetBitrate { get; set; }
        /// <summary>已发送帧数（线程安全读取，供 UI 会话列表展示）。</summary>
        public long FramesSent { get { return Interlocked.Read(ref _framesSent); } }
        public int FrameQueueCapacity
        {
            get { lock (_lock) return _frameQueueCapacity; }
            set { lock (_lock) _frameQueueCapacity = value; }
        }
        public int SendQueueCapacity
        {
            get { lock (_lock) return _sendQueueCapacity; }
            set { lock (_lock) _sendQueueCapacity = value; }
        }
        public int PendingFrames
        {
            get { lock (_lock) return _sendQueue.Count; }
        }

        public event EventHandler<ErrorEventArgs> FatalError;

        public ServerStreamSession(ICaptureService captureService, Action<uint, byte[]> sendTo,
            ICursorTracker cursorTracker)
        {
            _captureService = captureService;
            _sendTo = sendTo;
            _cursorTracker = cursorTracker;
            // 16ms ≈ 60fps 起步（采集已是 60fps）：编码跟不上时 D11 自适应会把帧间隔回调，
            // 低延迟优先于低帧率 —— 端到端画面延迟主要由帧周期决定。
            FrameDelayMs = 16; // ~60fps default
            KeyframeInterval = 30;
            // 1080p 屏幕内容：12Mbps 动态滚动场景仍可见色度/文字边缘瑕疵，
            // 15Mbps + 屏幕内容模式 + QP 上限 36 + 关闭去块滤波后更接近 VNC 观感；
            // 局域网下带宽充裕，22.5M 上限码率由编码器 iMaxBitrate 兜底。
            TargetBitrate = 15000000;
        }

        public void Start(uint sessionId, CodecId codec)
        {
            if (_running) return;
            _sessionId = sessionId;
            _codec = codec;
            Logger.Info("ServerStreamSession {0} starting with codec {1}", sessionId, codec);

            // Create encoder — H264 是唯一支持的编码方式，不再回退到原始像素
            _encoder = EncoderFactory.Create(codec);
            if (_encoder == null)
            {
                var ex = new InvalidOperationException("H264 encoder unavailable: " + codec);
                Logger.Error("Session {0}: encoder not available for codec {1} — H264 is mandatory, aborting session", sessionId, codec);
                // 抛异常让握手流程返回 InternalError 并断开，而不是假成功导致客户端黑屏
                throw ex;
            }
            Logger.Info("Session {0}: encoder created for codec {1}", sessionId, codec);

            // 初始化阶段抛异常时先释放编码器，再向上抛出（此时 _running=false，
            // Stop() 会提前返回，若不在此释放会造成原生句柄泄漏）
            try
            {
                // Get screen dimensions
                var bounds = _captureService.GetPrimaryScreen();
                _contentW = bounds.Width;
                _contentH = bounds.Height;
                // 编码分辨率：主屏尺寸超出上限时等比降采样（内容坐标空间不变，仅提速）。
                // OpenH264 I420 要求偶数宽高：向上取偶。
                int encodeW = bounds.Width;
                int encodeH = bounds.Height;
                if (MaxEncodeWidth > 0 && encodeW > MaxEncodeWidth)
                {
                    encodeH = Math.Max(1, (int)((long)encodeH * MaxEncodeWidth / encodeW));
                    encodeW = MaxEncodeWidth;
                }
                _lastW = (encodeW + 1) & ~1;
                _lastH = (encodeH + 1) & ~1;
                _encodeBuf = new byte[_lastW * _lastH * 4];

                _encoder.Initialize(_lastW, _lastH, TargetBitrate);

                // Pre-allocate double buffers（按主屏全分辨率；降采样在编码线程完成）
                int size = bounds.Width * bounds.Height * 4;
                _captureBufs[0] = new byte[size];
                _captureBufs[1] = new byte[size];
                _captureBufInUse[0] = false;
                _captureBufInUse[1] = false;
            }
            catch
            {
                try { _encoder.Dispose(); } catch { }
                _encoder = null;
                throw;
            }

            _running = true;
            _stopping = false;
            Interlocked.Exchange(ref _fatalRaisedFlag, 0);
            Logger.Info("Session {0}: stream started, resolution={1}x{2}, frameDelay={3}ms",
                sessionId, _lastW, _lastH, FrameDelayMs);

            // Subscribe to capture events
            _captureService.FrameCaptured += OnFrameCaptured;

            // Start cursor tracking
            if (_cursorTracker != null)
            {
                _cursorSession = _cursorTracker.CreateSession();
                _cursorSession.AttachSendTo(_sendTo, _sessionId);
                _cursorSession.Start();
            }

            // Start threads
            _encodeThread = new Thread(EncodeLoop);
            _encodeThread.IsBackground = true;
            // 降优先级：编码是吞吐型后台任务，弱机 CPU 饱和时不能抢占输入处理线程，
            // 否则远端右键/点击响应会延迟到秒级。
            _encodeThread.Priority = ThreadPriority.BelowNormal;
            _encodeThread.Start();

            _sendThread = new Thread(SendLoop);
            _sendThread.IsBackground = true;
            _sendThread.Start();
        }

        public void Stop()
        {
            if (!_running) return;
            Logger.Info("Session {0}: stopping stream, pendingFrames={1} encoded={2} sent={3} encodeFails={4} queueDrops={5} captureDrops={6}",
                _sessionId, GetPendingFrames(), Interlocked.Read(ref _framesEncoded), Interlocked.Read(ref _framesSent),
                _consecutiveEncodeFailures, _sendQueueDrops, _captureQueueDrops);
            // 1. Set stopping flag FIRST — encode/send threads check this at loop top
            _stopping = true;

            // 2. Unsubscribe from capture events so no new frames enter _frameQueue
            _captureService.FrameCaptured -= OnFrameCaptured;

            // 3. Clear queues + push sentinel values to unblock waiting threads.
            //    Order matters: Clear then Enqueue under same lock prevents a race
            //    where a thread dequeues a stale item between clear and sentinel push.
            lock (_lock)
            {
                _frameQueue.Clear();
                _sendQueue.Clear();
                _frameQueue.Enqueue(new CapturedFrame()); // Sentinel: Pixels==null
                _sendQueue.Enqueue(new FrameToSend());    // Sentinel: Data==null
                Monitor.PulseAll(_lock);
            }

            // 4. Join threads with timeout.
            //    Anti-race: if encoding thread is stuck inside native Encode() call
            //    (e.g. libx264 blocking), Join will timeout. We do NOT Dispose the
            //    encoder in that case — it would crash on freed native handles.
            //    Instead, mark for deferred cleanup and let GC/process exit reclaim.
            bool encodeJoined = true;
            if (_encodeThread != null)
            {
                if (!_encodeThread.Join(3000))
                {
                    // Encoder stuck — mark for deferred cleanup, don't Dispose encoder
                    Logger.Warn("Session {0}: encode thread timeout (3s) — encoder deferred cleanup", _sessionId);
                    encodeJoined = false;
                }
                _encodeThread = null;
            }
            if (_sendThread != null)
            {
                if (!_sendThread.Join(3000))
                {
                    Logger.Warn("Session {0}: send thread timeout (3s)", _sessionId);
                }
                _sendThread = null;
            }

            // Stop cursor tracking
            if (_cursorSession != null)
            {
                _cursorSession.Stop();
                _cursorTracker?.RemoveSession(_cursorSession);
                _cursorSession = null;
            }

            if (_encoder != null && encodeJoined)
            {
                // Only dispose if thread joined cleanly
                _encoder.Dispose();
            }
            _encoder = null;
            // 释放静态帧缓存，便于会话对象被复用时状态干净
            _prevBgra = null;

            _running = false;
            Logger.Info("Session {0}: stream stopped", _sessionId);
        }

        public void ApplyGlobalLoadLevel(int level)
        {
            _globalLoadLevel = level;
        }

        public void Dispose()
        {
            Stop();
        }

        private void OnFrameCaptured(ScreenFrame frame)
        {
            if (_stopping) return;
            if (frame.Scan0 == IntPtr.Zero) return;

            int frameSize = frame.Width * frame.Height * 4;
            if (frameSize <= 0) return;

            // 选择空闲缓冲并立即标记占用：防止编码线程还在读某块缓冲时被下一次截屏覆盖
            int bufIdx = -1;
            lock (_lock)
            {
                if (_frameQueue.Count >= _frameQueueCapacity)
                {
                    // 截屏→编码队列满，丢弃此帧（编码速度跟不上截屏速度）
                    _captureQueueDrops++;
                    if (_captureQueueDrops == 1 || _captureQueueDrops % 60 == 0)
                        Logger.Warn("Session {0}: capture queue full, frame dropped, total drops={1}",
                            _sessionId, _captureQueueDrops);
                    return;
                }
                for (int i = 0; i < _captureBufs.Length; i++)
                {
                    if (!_captureBufInUse[i])
                    {
                        bufIdx = i;
                        break;
                    }
                }
                if (bufIdx < 0)
                {
                    _captureQueueDrops++;
                    if (_captureQueueDrops == 1 || _captureQueueDrops % 60 == 0)
                        Logger.Warn("Session {0}: all capture buffers busy, frame dropped, total drops={1}",
                            _sessionId, _captureQueueDrops);
                    return;
                }
                _captureBufInUse[bufIdx] = true;
            }

            byte[] targetBuf = _captureBufs[bufIdx];
            if (targetBuf == null || targetBuf.Length < frameSize)
            {
                // 按捕获帧实际尺寸分配（编码降采样由编码线程完成，不在此裁剪）
                targetBuf = new byte[frameSize];
                _captureBufs[bufIdx] = targetBuf;
            }

            // 原样拷贝捕获帧（含奇数分辨率；取偶/降采样由编码线程统一处理）
            System.Runtime.InteropServices.Marshal.Copy(frame.Scan0, targetBuf, 0, frameSize);

            // Enqueue（缓冲已选中并标记占用，队列只可能被编码线程消耗，入队必然成功）
            lock (_lock)
            {
                var cf = new CapturedFrame
                {
                    Pixels = targetBuf,
                    BufferIndex = bufIdx,
                    Width = frame.Width,
                    Height = frame.Height,
                    CaptureTimestamp = Stopwatch.GetTimestamp()
                };
                _frameQueue.Enqueue(cf);
                Monitor.Pulse(_lock);
            }
        }

        private void EncodeLoop()
        {
            long lastEncodeTimestamp = 0;
            int encodeLoopIter = 0;

            Logger.Info("Session {0}: EncodeLoop thread started", _sessionId);

            while (!_stopping)
            {
                encodeLoopIter++;
                CapturedFrame frame;
                lock (_lock)
                {
                    while (_frameQueue.Count == 0 && !_stopping)
                        Monitor.Wait(_lock, 100);
                    if (_stopping) break;
                    if (_frameQueue.Count == 0) continue;

                    // Check for sentinel
                    frame = _frameQueue.Dequeue();
                    if (frame.Pixels == null) break; // sentinel
                }

                // 丢弃已过期的旧帧：队列中还有更新帧时，当前出队的是最旧的，
                // 直接释放并继续取最新帧 → 画面延迟 ≈ 1 个截屏周期而非队列深度×帧间隔。
                // 实时桌面场景"最新帧优先"，牺牲少量平滑换取更低的端到端延迟。
                lock (_lock)
                {
                    if (!_stopping && _frameQueue.Count > 0)
                    {
                        _captureBufInUse[frame.BufferIndex] = false;
                        continue;
                    }
                }

                if (encodeLoopIter == 1 || encodeLoopIter % 100 == 0)
                    Logger.Info("Session {0}: EncodeLoop iter={1} dequeued frame res={2}x{3} bgraLen={4}",
                        _sessionId, encodeLoopIter, frame.Width, frame.Height, frame.Pixels.Length);

                // Throttle（D11 自适应帧率 + D12 全局负载：负载每级额外 +10ms 帧间隔）
                int effectiveDelay = FrameDelayMs + _globalLoadLevel * 10;
                if (effectiveDelay > 0)
                {
                    long now = Stopwatch.GetTimestamp();
                    long elapsed = (now - lastEncodeTimestamp) * 1000 / Stopwatch.Frequency;
                    if (elapsed < effectiveDelay - 1)
                    {
                        Thread.Sleep(effectiveDelay - (int)elapsed);
                    }
                }
                lastEncodeTimestamp = Stopwatch.GetTimestamp();

                // 内容尺寸变化检测（如显示器分辨率切换）
                bool resolutionChanged = false;
                if (frame.Width != _contentW || frame.Height != _contentH)
                {
                    Logger.Info("Session {0}: content changed {1}x{2} -> {3}x{4}",
                        _sessionId, _contentW, _contentH, frame.Width, frame.Height);
                    _contentW = frame.Width;
                    _contentH = frame.Height;
                    resolutionChanged = true;
                }

                // 编码分辨率计算 + 编码器重建
                int newEncodeW = _contentW;
                int newEncodeH = _contentH;
                if (MaxEncodeWidth > 0 && newEncodeW > MaxEncodeWidth)
                {
                    newEncodeH = Math.Max(1, (int)((long)newEncodeH * MaxEncodeWidth / newEncodeW));
                    newEncodeW = MaxEncodeWidth;
                }
                newEncodeW = (newEncodeW + 1) & ~1;
                newEncodeH = (newEncodeH + 1) & ~1;
                if (resolutionChanged)
                {
                    // 分辨率变化后旧帧缓存失效，重置静态帧检测
                    _prevBgra = null;
                    _framesSkipped = 0;
                }
                if (newEncodeW != _lastW || newEncodeH != _lastH)
                {
                    _lastW = newEncodeW;
                    _lastH = newEncodeH;
                    _encodeBuf = new byte[_lastW * _lastH * 4];
                    _encoder.Reset();
                    _encoder.Initialize(_lastW, _lastH, TargetBitrate);
                }

                bool forceKey = resolutionChanged
                    || _framesSkipped > 0
                    || (_sequenceNumber % KeyframeInterval == 0);

                Logger.Debug("Session {0}: calling Encode seq={1} forceKey={2} res={3}x{4} bgraLen={5}",
                    _sessionId, _sequenceNumber, forceKey, frame.Width, frame.Height, frame.Pixels.Length);

                long encodeStart = Stopwatch.GetTimestamp();
                EncodedFrame result;
                byte[] pixelsToEncode = null;
                int compareLen = _lastW * _lastH * 4;
                try
                {
                    pixelsToEncode = frame.Pixels;
                    if (frame.Width != _lastW || frame.Height != _lastH)
                    {
                        // 降采样到编码分辨率（内容坐标空间不变，仅降低像素量提速）
                        DownscaleBgra(frame.Pixels, frame.Width, frame.Height,
                            _encodeBuf, _lastW, _lastH);
                        pixelsToEncode = _encodeBuf;
                    }

                    // 静态帧跳过：内容未变化时不编码不发送。Win7 弱机每帧编码
                    // 100~300ms，静止桌面占绝大多数帧——跳过可把延迟预算留给真正变化。
                    // 注意只比较 encode 尺寸范围（frame 缓冲按全分辨率预分配，尾部是旧数据）。
                    if (_prevBgra != null && ByteArraysEqual(pixelsToEncode, _prevBgra, compareLen))
                    {
                        _framesSkipped++;
                        lock (_lock) { _captureBufInUse[frame.BufferIndex] = false; }
                        continue;
                    }

                    result = _encoder.Encode(pixelsToEncode, forceKey);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Session {0}: Encode threw exception seq={1} — frame skipped",
                        _sessionId, _sequenceNumber);
                    _consecutiveEncodeFailures++;
                    if (_consecutiveEncodeFailures == 30)
                        RaiseFatal("Encoder threw repeatedly (" + _consecutiveEncodeFailures + " times)");
                    lock (_lock) { _captureBufInUse[frame.BufferIndex] = false; }
                    continue;
                }
                long encodeEnd = Stopwatch.GetTimestamp();
                double encodeMs = (encodeEnd - encodeStart) * 1000.0 / Stopwatch.Frequency;

                // 编码失败：丢弃此帧，不发送任何数据（不再回退到原始像素），
                // 也不更新静态帧参考（失败的帧从未发给客户端，不能作为下次比对基准）
                if (result.Data == null || result.Data.Length == 0)
                {
                    _consecutiveEncodeFailures++;
                    if (_consecutiveEncodeFailures == 1 || _consecutiveEncodeFailures % 30 == 0)
                        Logger.Warn("Session {0}: encode failed (seq={1}), frame dropped. Consecutive failures={2}, encodeMs={3:F1}",
                            _sessionId, _sequenceNumber, _consecutiveEncodeFailures, encodeMs);
                    if (_consecutiveEncodeFailures == 30)
                        RaiseFatal("Encoder failed repeatedly (" + _consecutiveEncodeFailures + " frames)");
                    lock (_lock) { _captureBufInUse[frame.BufferIndex] = false; }
                    continue;
                }

                // 编码成功后、释放像素缓冲所有权之前，先拷贝当前帧用于下次静态帧比对。
                // 顺序不能颠倒：缓冲释放后截屏线程可能立刻复用并覆盖该数组，
                // 再拷贝会读到新旧混合数据，导致静态帧检测误判。
                if (_prevBgra == null || _prevBgra.Length != compareLen)
                    _prevBgra = new byte[compareLen];
                Buffer.BlockCopy(pixelsToEncode, 0, _prevBgra, 0, compareLen);
                _framesSkipped = 0;

                // 拷贝完成后像素缓冲不再被引用，释放所有权供下一帧截屏复用
                lock (_lock) { _captureBufInUse[frame.BufferIndex] = false; }

                Logger.Debug("Session {0}: Encode returned seq={1} dataLen={2} keyframe={3} encodeMs={4:F1}",
                    _sessionId, _sequenceNumber,
                    result.Data?.Length ?? 0, result.IsKeyframe, encodeMs);

                // D11: track encode time and adapt frame rate
                lock (_lock)
                {
                    _encodeTimes.Enqueue(encodeEnd - encodeStart);
                    _encodeSum += (encodeEnd - encodeStart);
                    if (_encodeTimes.Count > AdaptiveWindow)
                    {
                        _encodeSum -= _encodeTimes.Dequeue();
                    }
                    if (_encodeTimes.Count >= AdaptiveWindow)
                    {
                        double avgMs = _encodeSum * 1000.0 / Stopwatch.Frequency / _encodeTimes.Count;
                        // 编码跟不上（平均耗时 > 33ms）→ 降帧率；充裕（< 20ms）→ 逐步回升
                        if (avgMs > 33)
                            FrameDelayMs = Math.Min(FrameDelayMs + 5, 120);
                        else if (avgMs < 20)
                            FrameDelayMs = Math.Max(FrameDelayMs - 5, 16);
                    }
                }

                _consecutiveEncodeFailures = 0;
                Interlocked.Increment(ref _framesEncoded);
                if (_framesEncoded == 1)
                    Logger.Info("Session {0}: FIRST frame encoded ok, seq={1} size={2} keyframe={3} encodeMs={4:F1}",
                        _sessionId, _sequenceNumber, result.Data.Length, result.IsKeyframe, encodeMs);
                else if (_framesEncoded % 100 == 0)
                    Logger.Debug("Session {0}: encoded {1} frames, last seq={2} size={3} keyframe={4} encodeMs={5:F1}",
                        _sessionId, _framesEncoded, _sequenceNumber, result.Data.Length, result.IsKeyframe, encodeMs);

                // Build VideoFrameMessage with H264 data only
                var vfm = new VideoFrameMessage
                {
                    // 用编码器实际尺寸（取偶后），保证客户端解码缓冲与 SPS 一致
                    Width = result.Width,
                    Height = result.Height,
                    IsKeyframe = result.IsKeyframe,
                    SequenceNumber = _sequenceNumber++,
                    Data = result.Data
                };
                byte[] payload = vfm.Pack();

                var fts = new FrameToSend
                {
                    Data = payload,
                    IsKeyframe = vfm.IsKeyframe,
                    SequenceNumber = vfm.SequenceNumber,
                    CaptureTimestamp = frame.CaptureTimestamp
                };

                // Enqueue to send queue
                lock (_lock)
                {
                    if (_stopping)
                    {
                        // Stop 已清空队列并推入 sentinel：不再入队，避免孤儿帧堆积
                        _sendQueueDrops++;
                    }
                    else if (_sendQueue.Count < _sendQueueCapacity)
                    {
                        _sendQueue.Enqueue(fts);
                        Monitor.Pulse(_lock);
                    }
                    else
                    {
                        // 发送瓶颈：丢弃最旧的非关键帧、保留最新帧（实时语义：丢帧优于延迟）。
                        // 若最旧的待发帧是关键帧则保留它（解码端依赖关键帧恢复），改丢新帧。
                        if (_sendQueue.Count > 0 && _sendQueue.Peek().IsKeyframe)
                        {
                            // 保留关键帧，丢弃新帧（队列维持容量上限不变）
                            _sendQueueDrops++;
                            if (_sendQueueDrops == 1 || _sendQueueDrops % 30 == 0)
                                Logger.Warn("Session {0}: send queue full, new frame dropped to preserve keyframe (seq={1}), total drops={2}",
                                    _sessionId, vfm.SequenceNumber, _sendQueueDrops);
                        }
                        else
                        {
                            // 丢弃最旧的非关键帧，入队最新帧
                            if (_sendQueue.Count > 0)
                                _sendQueue.Dequeue();
                            _sendQueueDrops++;
                            if (_sendQueueDrops == 1 || _sendQueueDrops % 30 == 0)
                                Logger.Warn("Session {0}: send queue full, oldest frame dropped (seq={1}), total drops={2}",
                                    _sessionId, vfm.SequenceNumber, _sendQueueDrops);
                            _sendQueue.Enqueue(fts);
                            Monitor.Pulse(_lock);
                        }
                    }
                }
            }
        }

        private void SendLoop()
        {
            uint sendFrameId = 1;

            while (!_stopping)
            {
                FrameToSend fts;
                lock (_lock)
                {
                    while (_sendQueue.Count == 0 && !_stopping)
                        Monitor.Wait(_lock, 100);
                    if (_stopping) break;
                    if (_sendQueue.Count == 0) continue;

                    fts = _sendQueue.Dequeue();
                    if (fts.Data == null) break; // sentinel
                }

                // Fragment and send
                int fragCount = (fts.Data.Length + Constants.FragmentSize - 1) / Constants.FragmentSize;
                if (fragCount == 0) fragCount = 1;

                MessageReassembler.FragAndSend(
                    sendFrameId, (byte)MessageType.VideoFrame, fts.Data,
                    _sendTo, _sessionId);

                Interlocked.Increment(ref _framesSent);
                if (_framesSent == 1)
                    Logger.Info("Session {0}: FIRST frame sent, frameId={1} payloadLen={2} fragCount={3} keyframe={4}",
                        _sessionId, sendFrameId, fts.Data.Length, fragCount, fts.IsKeyframe);
                else if (_framesSent % 100 == 0)
                    Logger.Debug("Session {0}: sent {1} frames, last frameId={2} payloadLen={3} fragCount={4}",
                        _sessionId, _framesSent, sendFrameId, fts.Data.Length, fragCount);

                sendFrameId++;
            }
        }

        private int GetPendingFrames()
        {
            lock (_lock) return _sendQueue.Count;
        }

        /// <summary>
        /// 盒式滤波降采样 BGRA 帧到目标尺寸（编码提速用）。
        /// 内容坐标空间不变：降采样仅减少编码像素量，客户端按原内容空间映射。
        /// </summary>
        private static void DownscaleBgra(byte[] src, int srcW, int srcH,
            byte[] dst, int dstW, int dstH)
        {
            int dstBytes = dstW * dstH * 4;
            if (src == null || dst == null || dst.Length < dstBytes)
                return;
            for (int dy = 0; dy < dstH; dy++)
            {
                int sy0 = dy * srcH / dstH;
                int sy1 = Math.Max(sy0 + 1, (dy + 1) * srcH / dstH);
                for (int dx = 0; dx < dstW; dx++)
                {
                    int sx0 = dx * srcW / dstW;
                    int sx1 = Math.Max(sx0 + 1, (dx + 1) * srcW / dstW);
                    int b = 0, g = 0, r = 0, a = 0;
                    int cnt = 0;
                    for (int sy = sy0; sy < sy1; sy++)
                    {
                        int rowBase = sy * srcW * 4;
                        for (int sx = sx0; sx < sx1; sx++)
                        {
                            int off = rowBase + sx * 4;
                            b += src[off];
                            g += src[off + 1];
                            r += src[off + 2];
                            a += src[off + 3];
                            cnt++;
                        }
                    }
                    int doff = (dy * dstW + dx) * 4;
                    dst[doff] = (byte)(b / cnt);
                    dst[doff + 1] = (byte)(g / cnt);
                    dst[doff + 2] = (byte)(r / cnt);
                    dst[doff + 3] = (byte)(a / cnt);
                }
            }
        }

        /// <summary>
        /// 比较两个 BGRA 缓冲的前 length 字节是否完全一致（静态帧检测用）。
        /// 首字节不同即提前返回，内容变化时开销极小。
        /// </summary>
        private static bool ByteArraysEqual(byte[] a, byte[] b, int length)
        {
            if (a == null || b == null) return false;
            if (a.Length < length || b.Length < length) return false;
            for (int i = 0; i < length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        /// <summary>触发一次 FatalError（不可恢复故障，TransportHost 据此断开会话）。</summary>
        private void RaiseFatal(string message)
        {
            if (Interlocked.CompareExchange(ref _fatalRaisedFlag, 1, 0) != 0) return;
            Logger.Error("Session {0}: FatalError: {1}", _sessionId, message);
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
    }
}
