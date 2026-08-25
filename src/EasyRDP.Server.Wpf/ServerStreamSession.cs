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
        // 帧队列容量：编码线程从队列取帧编码，采集线程入帧。
        // 编码 150-400ms/帧，采集 16ms 间隔（60fps），2 容量会丢弃约 90% 的帧。
        // 增至 4 可缓解突发，编码慢时仍有丢弃但不至于每帧都丢。
        private int _frameQueueCapacity = 4;
        private Queue<FrameToSend> _sendQueue = new Queue<FrameToSend>();
        private int _sendQueueCapacity = 2;

        // Capture buffers with ownership tracking: a buffer is only reused after the
        // encode thread has finished reading it. Plain A/B alternation could overwrite
        // a buffer the encoder is still reading when encode takes longer than 2 captures.
        // 4 缓冲：编码耗时 200-900ms 时，2 缓冲丢弃率高达 90%+，
        // 6 缓冲提高突发容限：偶发编码尖峰（如窗口弹出瞬间 ZRLE 全瓦片编码
        // 实测 239ms）时截屏线程仍有缓冲可用，避免 all capture buffers busy 丢帧。
        // 每缓冲 8.3MB(1080p BGRA)，6 缓冲共 50MB——XP 32 位 2GB 地址空间可接受。
        private readonly byte[][] _captureBufs = new byte[6][];
        private readonly bool[] _captureBufInUse = new bool[6];
        private int _lastW, _lastH;

        /// <summary>编码实际宽度（向上取偶后），客户端用此值初始化解码器与显示。</summary>
        public int EncodeWidth { get { return _lastW; } }
        /// <summary>编码实际高度（向上取偶后），客户端用此值初始化解码器与显示。</summary>
        public int EncodeHeight { get { return _lastH; } }
        // 内容坐标空间尺寸 = 物理屏幕尺寸（鼠标坐标映射基准）。
        // 与捕获/编码尺寸解耦：D11 降采样只改变捕获帧尺寸，不改变内容坐标空间。
        private int _contentW, _contentH;
        // 内容分辨率周期重查计数器（每 600 帧重查一次物理屏幕尺寸，检测显示器分辨率切换）
        private int _contentCheckCounter;
        // 托管降采样兜底缓冲：捕获帧与编码尺寸不一致时
        // （D11 档位切换过渡帧、屏幕奇数宽度偶对齐）把帧缩放到 _lastW×_lastH。
        // 稳态下捕获帧已按档位 StretchBlt 预缩放，此路径不再参与每帧热路径。
        private byte[] _encodeBuf;

        // Sequence
        private long _sequenceNumber;

        // D11 adaptive
        private Queue<long> _encodeTimes = new Queue<long>();
        private long _encodeSum;
        private const int AdaptiveWindow = 30;
        // 编码分辨率上限：0 = 不降分辨率（全分辨率）。>0 时编码线程按该宽度等比降采样。
        // D11 运行时自适应调整：编码耗时持续超标降档（1920→1280），恢复后升档回全分辨率。
        // 最低档 1280（清晰度优先：再低文字无法辨认，负载过高时由帧率自适应承担降速）。
        private volatile int _adaptiveMaxEncodeWidth;
        // 分辨率档位自适应计数：降档/升档都需要连续多帧达到阈值才动作，避免抖动。
        // 阈值基于弱机实测：ZRLE 静态帧 ~100ms、动态帧 300ms+；OpenH264 软编低一个量级。
        private int _downscaleStreak;
        private int _upscaleStreak;
        private const int DownscaleStreakLimit = 10;  // 连续 10 帧编码耗时超标 → 降一档（弱机快速降档，缩短 1~2 FPS 的持续期）
        private const int UpscaleStreakLimit = 45;    // 连续 45 帧编码耗时充裕 → 升一档（升档保守，避免降/升档振荡）
        private const double DownscaleThresholdMs = 100.0;  // 编码耗时 > 100ms 视为超标
        private const double UpscaleThresholdMs = 60.0;     // 编码耗时 < 60ms 视为充裕
        // 码率档位（bps）：默认 15Mbps，发送瓶颈/高负载时逐级下调
        private static readonly int[] BitrateSteps = new int[]
        {
            15000000,   // 15Mbps 默认
            8000000,    // 8Mbps
            4000000,    // 4Mbps
            2000000     // 2Mbps
        };
        private int _bitrateStepIndex;
        private int _sendQueueFullStreak;   // 发送队列连续满帧数（触发降码率信号）
        private const int SendQueueFullStreakLimit = 30;
        // 内容变化比例（0~1）：由变化检测结果/ZRLE 编码器统计，供自适应决策与诊断
        private volatile float _contentChangeRatio = 1.0f;
        // 滑动窗口平均编码耗时（ms，供 D12 全局负载统计与诊断面板）。
        // 不可 volatile（C# 限制）；编码线程写、TransportHost 心跳线程读，
        // double 非原子读写的撕裂对诊断统计无害（近似值足够）。
        private double _avgEncodeMs;

        // D12 global load
        private volatile int _globalLoadLevel;

        // Diagnostics counters
        private int _consecutiveEncodeFailures;
        private int _sendQueueDrops;
        private int _captureQueueDrops;
        private long _framesEncoded;
        private long _framesSent;
        // 帧变化检测器：判断当前帧相对上次成功编码的帧是否变化。
        // 通过 ChangeDetectorFactory 注入，支持原始 memcmp（FullFrameMemcmp）
        // 与块哈希（BlockHashDirtyRect）两种模式运行时切换。
        // 替代原始 _prevBgra + ByteArraysEqual 内联实现，行为保持一致。
        private readonly IFrameChangeDetector _changeDetector;
        // 连续跳过的帧数：恢复编码时强制关键帧，避免长间隔后解码漂移
        private int _framesSkipped;
        // 保活间隔：连续跳过此帧数后强制编码一帧（即使内容无变化）。
        // 目的：桌面完全静止时 BlockHashDirtyRect 会跳过所有帧，导致客户端
        // FPS=0、画面冻结、易被误判为断连。保活帧为 P 帧（内容与上帧相同，
        // 体积极小 ~200-500B），不影响带宽但维持客户端帧率显示和连接活跃度。
        // 30 帧 ≈ 0.5s @60fps 采集，即静止时保底 2 FPS。
        private const int KeepaliveFrameInterval = 30;

        // Cursor session
        private ICursorTrackerSession _cursorSession;

        // ZRLE CopyRect 触发状态：鼠标按下时启用编码器 CopyRect 搜索（窗口拖动场景）。
        // 由 TransportHost 在收到 MouseDown/MouseUp 输入事件时更新。
        private volatile bool _mouseButtonDown;

        // 阶段三：客户端请求驱动流控（仅 ZRLE 模式）。
        // _clientRequestPending：客户端已发 FramebufferUpdateRequest、等待编码发送。
        // _flowControlEnabled：本会话是否启用流控（Start 时按 codec 设置，ZRLE 启用）。
        private volatile bool _clientRequestPending;
        private volatile bool _flowControlEnabled;
        /// <summary>OnFramebufferUpdateRequest 诊断日志计数器（每 100 次请求打印一次）。</summary>
        private int _frameReqDiagCounter;

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

        /// <summary>滑动窗口平均编码耗时（毫秒）。供 D12 全局负载统计与诊断。</summary>
        public double AvgEncodeMs { get { return _avgEncodeMs; } }

        /// <summary>当前内容变化比例（0~1，1=全屏变化）。供诊断与自适应决策。</summary>
        public float ContentChangeRatio { get { return _contentChangeRatio; } }
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
            ICursorTracker cursorTracker, IFrameChangeDetector changeDetector)
        {
            _captureService = captureService;
            _sendTo = sendTo;
            _cursorTracker = cursorTracker;
            // 检测器必须由调用方注入（TransportHost 按 ServerSettings.ChangeDetectionMode 创建）。
            // null 兜底：极端情况下不崩，创建默认 memcmp 检测器保持原始行为。
            _changeDetector = changeDetector ?? new FullFrameChangeDetector();
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
            // 阶段三：仅 ZRLE 模式启用客户端请求驱动流控（H264 保持服务端推送路径不变）
            _flowControlEnabled = (codec == CodecId.Zrle);
            _clientRequestPending = false;
            Logger.Info("ServerStreamSession {0} starting with codec {1}", sessionId, codec);
            // 版本诊断标识：部署后日志可见，用于确认运行的二进制包含 EncodeLoop 流控修复。
            // 若日志无此行或 flowControlFix != v3-2026-08-09，说明部署的是旧构建。
            Logger.Info("=== EasyRDP Server build: {0} flowControlFix={1} ===",
                EasyRDP.Core.Diagnostics.BuildInfo.Describe(),
                EasyRDP.Core.Diagnostics.BuildInfo.FlowControlFixVersion);

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

            // 弱机优化：ZRLE 流控模式下客户端请求驱动（间隔 ≥250ms），服务端 60fps 捕获
            // 纯属浪费——实测捕获 863 帧 vs 编码 452 帧，半数捕获帧被 flow-drop 丢弃，
            // 捕获线程还和编码线程争抢弱机 CPU。流控模式下把捕获间隔降到 100ms
            // （10fps 上限，仍远超 2-4fps 编码需求），释放 CPU 给编码；H264 推送模式保持 16ms。
            // 注意：CaptureService 为全局单例，多会话时此设置影响所有会话
            // （弱机单会话为目标场景，见注释）。
            if (_flowControlEnabled)
            {
                var captureImpl = _captureService as CaptureService;
                if (captureImpl != null)
                    captureImpl.FrameIntervalMs = 100;
            }

            // 初始化阶段抛异常时先释放编码器，再向上抛出（此时 _running=false，
            // Stop() 会提前返回，若不在此释放会造成原生句柄泄漏）
            try
            {
                // Get screen dimensions
                var bounds = _captureService.GetPrimaryScreen();
                _contentW = bounds.Width;
                _contentH = bounds.Height;
                // 初始档位启发式：单核弱机（XP 虚拟机典型：Environment.ProcessorCount=1）
                // 且屏幕宽度 >1280 时直接从 1280 起步——否则全分辨率编码仅 1~2 FPS，
                // 要等 30 帧滑动窗口 + 连续超时帧数达标才触发 D11 降档，开局体验极差。
                // 多核/小屏保持全分辨率（0），由 D11 按需调整；单核误判时 D11 升档会恢复全分辨率。
                _adaptiveMaxEncodeWidth =
                    (Environment.ProcessorCount <= 1 && bounds.Width > 1280) ? 1280 : 0;
                ApplyCaptureMaxWidth();
                // 编码分辨率：主屏尺寸超出上限时等比降采样（内容坐标空间不变，仅提速）。
                // OpenH264 I420 要求偶数宽高：向上取偶。上限由 D11 自适应维护（0=全分辨率）。
                int encodeW = bounds.Width;
                int encodeH = bounds.Height;
                int maxEncodeW = _adaptiveMaxEncodeWidth;
                if (maxEncodeW > 0 && encodeW > maxEncodeW)
                {
                    encodeH = Math.Max(1, (int)((long)encodeH * maxEncodeW / encodeW));
                    encodeW = maxEncodeW;
                }
                _lastW = (encodeW + 1) & ~1;
                _lastH = (encodeH + 1) & ~1;
                _encodeBuf = new byte[_lastW * _lastH * 4];

                _encoder.Initialize(_lastW, _lastH, TargetBitrate);

                // Pre-allocate capture buffers（按主屏全分辨率分配，降采样后帧更小也能容纳）
                int size = bounds.Width * bounds.Height * 4;
                for (int i = 0; i < _captureBufs.Length; i++)
                {
                    _captureBufs[i] = new byte[size];
                    _captureBufInUse[i] = false;
                }
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
                _cursorSession.AttachSendTo(wire => _sendTo(_sessionId, wire));
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

            // 恢复捕获间隔（Start 时流控模式降为 100ms），避免影响后续 H264 会话的 60fps 需求
            var captureImpl = _captureService as CaptureService;
            if (captureImpl != null)
            {
                captureImpl.FrameIntervalMs = 16;
                // 复位捕获分辨率上限，避免降档状态泄漏到后续会话
                _adaptiveMaxEncodeWidth = 0;
                captureImpl.SetCaptureMaxWidth(0);
            }

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
            // 释放检测器内部缓存（参考帧/哈希），便于会话对象被复用时状态干净
            if (_changeDetector != null) _changeDetector.Reset();

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
                    // 截屏→编码队列满，丢弃此帧（编码速度跟不上截屏速度）。
                    // 诊断增强：附带编码进度/流控状态，定位是编码线程阻塞还是单纯降速。
                    _captureQueueDrops++;
                    if (_captureQueueDrops == 1 || _captureQueueDrops % 60 == 0)
                        Logger.Warn("Session {0}: capture queue full, frame dropped, total drops={1}. queueCap={2} encoded={3} pending={4} flowEnabled={5}",
                            _sessionId, _captureQueueDrops, _frameQueueCapacity,
                            Interlocked.Read(ref _framesEncoded), _clientRequestPending, _flowControlEnabled);
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

                // 阶段三：客户端请求驱动流控（仅 ZRLE 模式）。
                // 客户端渲染完一帧才发 FramebufferUpdateRequest，服务端等请求才编码，
                // 帧率由客户端消费能力决定，避免客户端解码/渲染积压导致延迟膨胀。
                // 首帧（_framesEncoded==0）跳过等待直接推送：客户端在收到首帧前无帧可渲染、
                // 不会发请求，若等待将造成永久黑屏（握手成功后客户端等首帧、服务端等请求）。
                if (_flowControlEnabled && !_clientRequestPending
                    && Interlocked.Read(ref _framesEncoded) > 0)
                {
                    // 高频诊断日志降频：仅首帧/每 100 帧打印（每帧落盘 IO 会拖慢编码线程导致卡顿）
                    bool diagThisIter = (encodeLoopIter <= 1 || encodeLoopIter % 100 == 0);
                    if (diagThisIter)
                        Logger.Debug("Session {0}: EncodeLoop iter={1} flow-wait begin, pending={2} framesEncoded={3} queueCount={4}",
                            _sessionId, encodeLoopIter, _clientRequestPending,
                            Interlocked.Read(ref _framesEncoded), GetFrameQueueCount());
                    var waitSw = System.Diagnostics.Stopwatch.StartNew();
                    lock (_lock)
                    {
                        // 带超时保活：客户端崩溃/断开后不永远等待，1s 超时后继续取帧（保底 ~1 FPS）
                        if (!_clientRequestPending && !_stopping)
                            Monitor.Wait(_lock, 1000);
                    }
                    waitSw.Stop();
                    if (_stopping) break;
                    if (diagThisIter)
                        Logger.Debug("Session {0}: EncodeLoop iter={1} flow-wait end, pending={2} waitMs={3}",
                            _sessionId, encodeLoopIter, _clientRequestPending, waitSw.ElapsedMilliseconds);
                    // 超时（_clientRequestPending==false）：不 continue，继续执行取帧编码逻辑，
                    // 实现保底 ~1 FPS。注意：原 `continue` 会跳过取帧回到循环顶部再次 Wait ——
                    // 队列中已积压的帧永远不会被编码（纯空转），必须继续取帧。
                    // 请求到达（Pulse 唤醒，_clientRequestPending==true）时同样继续取帧。
                }

                CapturedFrame frame;
                lock (_lock)
                {
                    while (_frameQueue.Count == 0 && !_stopping)
                        Monitor.Wait(_lock, 100);
                    if (_stopping) break;
                    if (_frameQueue.Count == 0) continue; // 超时路径且队列仍空：下一轮

                    // Check for sentinel
                    frame = _frameQueue.Dequeue();
                    if (frame.Pixels == null) break; // sentinel
                }

                // 丢弃已过期的旧帧：队列中还有更新帧时，当前出队的是最旧的，
                // 直接释放并继续取最新帧 → 画面延迟 ≈ 1 个截屏周期而非队列深度×帧间隔。
                // 实时桌面场景"最新帧优先"，牺牲少量平滑换取更低的端到端延迟。
                //
                // 流控模式修正（ZRLE）：请求驱动的帧不能浪费。原逻辑在流控模式下
                // 每收到一个客户端请求只取 1 帧，若队列仍有积压（截屏线程 60fps 持续
                // 入队、编码线程每次只取 1 帧）就丢弃并 continue → 请求被消费但不编码
                // → 客户端心跳再发请求 → 再次丢弃 → 编码线程永不编码（实测 encoded=1
                // 死锁、capture queue full 持续）。流控模式改为"取尽队列、保留最新帧"，
                // 确保每个请求都编码一帧最新内容。
                lock (_lock)
                {
                    if (!_stopping && _frameQueue.Count > 0)
                    {
                        if (_flowControlEnabled)
                        {
                            // 流控模式：丢弃旧帧、保留最新帧（请求不能浪费）
                            int dropped = 0;
                            bool sentinelHit = false;
                            while (_frameQueue.Count > 0)
                            {
                                CapturedFrame newer = _frameQueue.Dequeue();
                                if (newer.Pixels == null)
                                {
                                    sentinelHit = true; // Stop 流程：buffer 由会话清理
                                    break;
                                }
                                _captureBufInUse[frame.BufferIndex] = false;
                                frame = newer;
                                dropped++;
                            }
                            if (sentinelHit) break; // sentinel → 编码线程结束
                            if (dropped > 0)
                                Logger.Debug("Session {0}: EncodeLoop iter={1} flow-drop: skipped {2} older frame(s), latest bufIdx={3}",
                                    _sessionId, encodeLoopIter, dropped, frame.BufferIndex);
                        }
                        else
                        {
                            // H264 推送模式：保持原"丢弃旧帧继续取最新"逻辑
                            _captureBufInUse[frame.BufferIndex] = false;
                            continue;
                        }
                    }
                }

                // 流控模式：确认此帧将被编码后消费本次请求（请求 1:1 编码）。
                // 不能在取帧前重置——帧被丢弃/continue 时本次请求会被白白浪费（见上注释），
                // 导致编码线程死等下一次请求（客户端已发过、靠 250ms 心跳兜底仍会丢帧）。
                // 写操作与 OnFramebufferUpdateRequest 的置 true 在同一 _lock 内，
                // 消除交错覆盖窗口。
                // 注：编码异常/失败 continue 路径不会恢复 pending——客户端 250ms 心跳
                // 会重新驱动请求，避免失败重试风暴（_consecutiveEncodeFailures 已兜底断连）。
                if (_flowControlEnabled)
                {
                    // 降频：每 100 帧打印（避免每帧落盘 IO 拖慢编码线程）
                    if (encodeLoopIter <= 1 || encodeLoopIter % 100 == 0)
                        Logger.Debug("Session {0}: EncodeLoop iter={1} consume request, pending={2}->false",
                            _sessionId, encodeLoopIter, _clientRequestPending);
                    lock (_lock) { _clientRequestPending = false; }
                }

                if (encodeLoopIter == 1 || encodeLoopIter % 100 == 0)
                    Logger.Info("Session {0}: EncodeLoop iter={1} dequeued frame res={2}x{3} bgraLen={4} queueRemaining={5}",
                        _sessionId, encodeLoopIter, frame.Width, frame.Height, frame.Pixels.Length,
                        GetFrameQueueCount());

                // Throttle（D11 自适应帧率 + D12 全局负载：负载每级额外 +10ms 帧间隔）。
                // 流控模式下客户端请求本身已限速（请求到达后才编码），跳过节流避免双重延迟。
                int effectiveDelay = _flowControlEnabled ? 0 : FrameDelayMs + _globalLoadLevel * 10;
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

                // 内容分辨率 = 物理屏幕尺寸（鼠标坐标空间），会话期间通常固定。
                // 捕获帧尺寸现在可能已被 D11 降采样（StretchBlt 一步截屏+缩放），
                // 不能再用 frame.Width/Height 判断内容变化，改为周期重查物理屏幕尺寸。
                bool resolutionChanged = CheckContentResolutionChanged();

                // 编码分辨率 = 捕获帧尺寸（CaptureService 已按 D11 档位 StretchBlt 降采样，
                // 帧尺寸即目标编码尺寸，无需再托管缩放）。向上取偶保证 I420 布局安全。
                int newEncodeW = (frame.Width + 1) & ~1;
                int newEncodeH = (frame.Height + 1) & ~1;
                if (resolutionChanged)
                {
                    // 分辨率变化后旧参考帧失效，重置检测器（下次 Detect 必然返回 ShouldEncode=true）
                    _changeDetector.Reset();
                    _framesSkipped = 0;
                }
                // 编码尺寸变化（含 D11 分辨率档位调整）：需要重建编码器并强制关键帧
                bool encodeSizeChanged = (newEncodeW != _lastW || newEncodeH != _lastH);
                if (encodeSizeChanged)
                {
                    _lastW = newEncodeW;
                    _lastH = newEncodeH;
                    _encodeBuf = new byte[_lastW * _lastH * 4];
                    _encoder.Reset();
                    _encoder.Initialize(_lastW, _lastH, TargetBitrate);
                }

                long encodeStart = Stopwatch.GetTimestamp();
                EncodedFrame result;
                byte[] pixelsToEncode = null;
                // ZRLE 模式标志：try 块外声明（try 内赋值、try 外 Commit 判断均需使用）
                bool isZrle = false;
                try
                {
                    pixelsToEncode = frame.Pixels;
                    if (frame.Width != _lastW || frame.Height != _lastH)
                    {
                        // 托管降采样兜底：仅 D11 档位切换的过渡帧会走到这里
                        // （捕获线程尚未产出目标尺寸帧，或屏幕奇数宽度偶对齐）。
                        // 稳态下捕获帧尺寸已等于编码尺寸（StretchBlt 已降采样）。
                        DownscaleBgra(frame.Pixels, frame.Width, frame.Height,
                            _encodeBuf, _lastW, _lastH);
                        pixelsToEncode = _encodeBuf;
                    }

                    // 帧变化检测：通过 IFrameChangeDetector 抽象判断是否需要编码。
                    // - FullFrameMemcmp 模式：与原始 _prevBgra+ByteArraysEqual 行为完全一致，
                    //   完全相同才跳过；
                    // - BlockHashDirtyRect 模式：32×32 块哈希对比，变化块数≤阈值时也跳过，
                    //   避免光标残影/局部闪烁触发整帧 H.264 重编码（150-250ms）。
                    // - ZRLE 模式：编码器内部自己做 64×64 瓦片对比，跳过外部检测
                    //   避免双重变化检测（节省 3-5ms/帧），始终返回 ShouldEncode=true，
                    //   由 ZrleEncoder 内部决定实际编码哪些瓦片。
                    // Detect 内部缓存计算结果，编码成功后由 Commit() 提升为参考帧。
                    isZrle = _encoder != null && _encoder.Codec == CodecId.Zrle;
                    var changeResult = isZrle
                        ? new FrameChangeResult { ShouldEncode = true }
                        : _changeDetector.Detect(pixelsToEncode, _lastW, _lastH);
                    // 内容变化率统计（自适应信号 + 诊断面板数据源）：
                    // H264 路径来自块级变化检测（BlockHashDirtyRect 模式）或
                    // 全帧比较（FullFrameMemcmp 模式恒为 0/1）。
                    if (!isZrle && changeResult.TotalBlockCount > 0)
                        _contentChangeRatio = (float)changeResult.ChangedBlockCount / changeResult.TotalBlockCount;
                    if (!changeResult.ShouldEncode && _framesSkipped < KeepaliveFrameInterval)
                    {
                        // 内容无变化且未达到保活阈值：跳过编码节省 H.264 150-250ms
                        _framesSkipped++;
                        lock (_lock) { _captureBufInUse[frame.BufferIndex] = false; }
                        continue;
                    }
                    // 达到保活阈值（KeepaliveFrameInterval）或内容有变化：执行编码
                    if (!changeResult.ShouldEncode)
                    {
                        // 保活帧：内容无变化但已连续跳过 KeepaliveFrameInterval 帧，
                        // 强制编码一帧维持客户端帧率显示和连接活跃度
                        Logger.Debug("Session {0}: keepalive frame forced after {1} skipped frames",
                            _sessionId, _framesSkipped);
                    }

                    // 关键帧判定：仅在以下情况强制关键帧——
                    // 1. 分辨率变化（编码器重建后首帧必须是 IDR）
                    // 2. 长间隔后恢复编码（≥60 帧跳过≈1-4s，解码器参考帧可能过时）
                    // 3. 周期性刷新（KeyframeInterval，防止累积漂移）
                    // 不在每次静态帧跳过后都强制——P 帧基于上一帧差分，
                    // 短暂跳过（几帧）不影响解码器参考帧有效性，避免大量大体积关键帧。
                    bool forceKey = resolutionChanged
                        || encodeSizeChanged
                        || _framesSkipped >= 60
                        || (_sequenceNumber % KeyframeInterval == 0);

                    Logger.Debug("Session {0}: calling Encode seq={1} forceKey={2} res={3}x{4} bgraLen={5}",
                        _sessionId, _sequenceNumber, forceKey, frame.Width, frame.Height, frame.Pixels.Length);

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

                // 编码成功后将当前帧提升为新的参考帧（Commit 内部已缓存 Detect 时的数据，
                // 不会重新访问 pixelsToEncode）。必须在释放缓冲所有权之前调用，
                // 因为 BlockHashDirtyRect 模式的 _pendingHashes 已在 Detect 时算好，
                // 而 FullFrameMemcmp 模式的 _pendingPixels 也已在 Detect 时拷贝完成。
                // ZRLE 模式：编码器内部自管参考帧（Encode 成功后已更新），外部 Commit 无意义，
                // 且 Detect 未被调用（_pendingHashes==null），Commit 本身是空操作，跳过即可。
                if (!isZrle)
                {
                    _changeDetector.Commit();
                }
                _framesSkipped = 0;

                // 拷贝完成后像素缓冲不再被引用，释放所有权供下一帧截屏复用
                lock (_lock) { _captureBufInUse[frame.BufferIndex] = false; }

                Logger.Debug("Session {0}: Encode returned seq={1} dataLen={2} keyframe={3} encodeMs={4:F1}",
                    _sessionId, _sequenceNumber,
                    result.Data?.Length ?? 0, result.IsKeyframe, encodeMs);

                // D11: track encode time and adapt frame rate / resolution / bitrate
                lock (_lock)
                {
                    // ZRLE 路径变化率：编码器内部做 64×64 瓦片对比，统计变化瓦片比例
                    if (isZrle && _encoder is ZrleEncoder)
                    {
                        try
                        {
                            _contentChangeRatio = ((ZrleEncoder)_encoder).EstimateChangeRatio(pixelsToEncode);
                        }
                        catch
                        {
                            // 统计失败不影响主流程
                        }
                    }

                    _encodeTimes.Enqueue(encodeEnd - encodeStart);
                    _encodeSum += (encodeEnd - encodeStart);
                    if (_encodeTimes.Count > AdaptiveWindow)
                    {
                        _encodeSum -= _encodeTimes.Dequeue();
                    }
                    if (_encodeTimes.Count >= AdaptiveWindow)
                    {
                        double avgMs = _encodeSum * 1000.0 / Stopwatch.Frequency / _encodeTimes.Count;
                        _avgEncodeMs = avgMs;

                        // 帧率自适应（原有）：编码跟不上 → 降帧率；充裕 → 逐步回升
                        if (avgMs > 33)
                            FrameDelayMs = Math.Min(FrameDelayMs + 5, 120);
                        else if (avgMs < 20)
                            FrameDelayMs = Math.Max(FrameDelayMs - 5, 16);

                        // 分辨率档位自适应：持续超标降档（弱机降像素量提速），
                        // 持续充裕升档回全分辨率。档位变化触发编码器重建+强制关键帧。
                        if (avgMs > DownscaleThresholdMs)
                        {
                            _downscaleStreak++;
                            _upscaleStreak = 0;
                        }
                        else if (avgMs < UpscaleThresholdMs)
                        {
                            _upscaleStreak++;
                            _downscaleStreak = 0;
                        }
                        else
                        {
                            _downscaleStreak = 0;
                            _upscaleStreak = 0;
                        }

                        if (_downscaleStreak >= DownscaleStreakLimit)
                        {
                            _downscaleStreak = 0;
                            int next = NextDownscaleStep(_adaptiveMaxEncodeWidth);
                            // 档位不低于实际内容宽度时（如 1600 宽屏首档 1920 不生效），
                            // 循环跳档直到低于内容宽或到最低档 1280（清晰度底线）。
                            while (next >= _contentW && next > 1280)
                                next = NextDownscaleStep(next);
                            if (next != _adaptiveMaxEncodeWidth)
                            {
                                _adaptiveMaxEncodeWidth = next;
                                // 同步到捕获服务：StretchBlt 一步截屏+降采样，
                                // 编码线程不再做昂贵的托管逐像素缩放。
                                ApplyCaptureMaxWidth();
                                Logger.Info("D11: encode slow ({0:F1}ms) — downscale maxEncodeWidth={1}",
                                    avgMs, next);
                            }
                        }
                        else if (_upscaleStreak >= UpscaleStreakLimit && _adaptiveMaxEncodeWidth > 0)
                        {
                            _upscaleStreak = 0;
                            int next = NextUpscaleStep(_adaptiveMaxEncodeWidth);
                            if (next != _adaptiveMaxEncodeWidth)
                            {
                                _adaptiveMaxEncodeWidth = next;
                                // 恢复全分辨率/升档时同样同步捕获尺寸。
                                ApplyCaptureMaxWidth();
                                Logger.Info("D11: encode fast ({0:F1}ms) — upscale maxEncodeWidth={1}",
                                    avgMs, next);
                            }
                        }
                    }

                    // 码率自适应：发送队列持续满（网络/接收端瓶颈）→ 逐级降码率。
                    // OpenH264 SetOption(BITRATE) 运行时生效，不重建编码器。
                    if (_sendQueueFullStreak >= SendQueueFullStreakLimit
                        && _bitrateStepIndex < BitrateSteps.Length - 1)
                    {
                        _sendQueueFullStreak = 0;
                        _bitrateStepIndex++;
                        TargetBitrate = BitrateSteps[_bitrateStepIndex];
                        Logger.Info("D11: send queue persistently full — bitrate down to {0} bps",
                            TargetBitrate);
                        if (_encoder != null)
                            _encoder.SetTargetBitrate(TargetBitrate);
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
                    // 内容坐标空间 = 物理屏幕尺寸。客户端必须用 Content* 映射鼠标坐标，
                    // 用 Width/Height 显示 —— D11 降采样后二者不再相等。
                    ContentWidth = _contentW,
                    ContentHeight = _contentH,
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
                        _sendQueueFullStreak = 0;  // 队列未满，重置降码率计数
                    }
                    else
                    {
                        // 发送瓶颈：丢弃最旧的非关键帧、保留最新帧（实时语义：丢帧优于延迟）。
                        // 若最旧的待发帧是关键帧则保留它（解码端依赖关键帧恢复），改丢新帧。
                        // 队列持续满 → 网络/接收端是瓶颈 → 触发 D11 码率降档信号
                        _sendQueueFullStreak++;
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

                // 发送完整视频帧消息（Framing.BuildMessage 组装 framing 外层，无分片）
                byte[] wire = Framing.BuildMessage((byte)MessageType.VideoFrame, fts.Data);
                _sendTo(_sessionId, wire);

                Interlocked.Increment(ref _framesSent);
                if (_framesSent == 1)
                    Logger.Info("Session {0}: FIRST frame sent, frameId={1} payloadLen={2} keyframe={3}",
                        _sessionId, sendFrameId, fts.Data.Length, fts.IsKeyframe);
                else if (_framesSent % 100 == 0)
                    Logger.Debug("Session {0}: sent {1} frames, last frameId={2} payloadLen={3}",
                        _sessionId, _framesSent, sendFrameId, fts.Data.Length);

                sendFrameId++;
            }
        }

        private int GetPendingFrames()
        {
            lock (_lock) return _sendQueue.Count;
        }

        /// <summary>
        /// 把当前 D11 分辨率档位同步到 CaptureService。
        /// 捕获服务用 StretchBlt 一步完成截屏+降采样，编码线程的托管缩放退化为
        /// 过渡帧兜底（避免 1080p 弱机单核上 100~300ms/帧的逐像素缩放开销）。
        /// </summary>
        private void ApplyCaptureMaxWidth()
        {
            var captureImpl = _captureService as CaptureService;
            if (captureImpl != null)
                captureImpl.SetCaptureMaxWidth(_adaptiveMaxEncodeWidth);
        }

        /// <summary>
        /// 周期重查物理屏幕尺寸，检测显示器分辨率切换（内容坐标空间变化）。
        /// 捕获帧尺寸已不能用于此判断——它可能被 D11 降采样（StretchBlt）改变。
        /// 每 600 帧重查一次（GetSystemMetrics 开销极小），有变化时返回 true，
        /// 由调用方重置检测器并强制关键帧。
        /// </summary>
        private bool CheckContentResolutionChanged()
        {
            if ((++_contentCheckCounter % 600) != 0)
                return false;
            try
            {
                var bounds = _captureService.GetPrimaryScreen();
                if (bounds.Width != _contentW || bounds.Height != _contentH)
                {
                    Logger.Info("Session {0}: content changed {1}x{2} -> {3}x{4}",
                        _sessionId, _contentW, _contentH, bounds.Width, bounds.Height);
                    _contentW = bounds.Width;
                    _contentH = bounds.Height;
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Session {0}: GetPrimaryScreen failed during content resolution check",
                    _sessionId);
            }
            return false;
        }

        /// <summary>
        /// D11 分辨率降档：0（全分辨率）→1920→1280（最低档，不再下降）。
        /// 清晰度优先：1280×776 是远程桌面文字可读性的底线，
        /// 低于此档（960）文字与图标已无法辨认；负载过高时宁可降帧率不再降分辨率。
        /// </summary>
        private static int NextDownscaleStep(int current)
        {
            if (current == 0) return 1920;
            return 1280;
        }

        /// <summary>
        /// D11 分辨率升档：1280→1920→0（恢复全分辨率）。
        /// </summary>
        private static int NextUpscaleStep(int current)
        {
            if (current < 1920) return 1920;
            return 0;
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
            // 预计算每列源像素范围 [sx0, sx1)：远程桌面降采样比 ≤2（1914→1280），
            // 每个目标像素最多覆盖 1~2 个源像素，预计算消除内层循环中的逐像素乘除法。
            int[] sx0s = new int[dstW];
            int[] sx1s = new int[dstW];
            for (int dx = 0; dx < dstW; dx++)
            {
                int s0 = dx * srcW / dstW;
                int s1 = (dx + 1) * srcW / dstW;
                if (s1 <= s0) s1 = s0 + 1;
                if (s1 > srcW) s1 = srcW;
                sx0s[dx] = s0;
                sx1s[dx] = s1;
            }

            for (int dy = 0; dy < dstH; dy++)
            {
                int sy0 = dy * srcH / dstH;
                int sy1 = (dy + 1) * srcH / dstH;
                if (sy1 <= sy0) sy1 = sy0 + 1;
                if (sy1 > srcH) sy1 = srcH;

                int doffBase = dy * dstW * 4;
                for (int dx = 0; dx < dstW; dx++)
                {
                    int sx0 = sx0s[dx];
                    int sx1 = sx1s[dx];
                    int b = 0, g = 0, r = 0;
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
                            cnt++;
                        }
                    }
                    // cnt ∈ {1,2,4}（行/列各 1~2 个源像素）：用移位代替整数除法提速。
                    int shift = cnt == 4 ? 2 : (cnt == 2 ? 1 : 0);
                    int doff = doffBase + dx * 4;
                    dst[doff] = (byte)(b >> shift);
                    dst[doff + 1] = (byte)(g >> shift);
                    dst[doff + 2] = (byte)(r >> shift);
                    dst[doff + 3] = 255; // 屏幕内容恒不透明，alpha 直接写满
                }
            }
        }

        // 注：原始静态帧检测（ByteArraysEqual + msvcrt.memcmp P/Invoke）已迁移至
        // FullFrameChangeDetector，通过 IFrameChangeDetector 抽象注入本类。
        // BlockHashDirtyRect 模式另提供 32×32 块哈希检测，由 ChangeDetectorFactory 按
        // ServerSettings.ChangeDetectionMode 创建。两种模式可在运行时切换。

        /// <summary>
        /// 更新鼠标按下状态（阶段二：ZRLE CopyRect 触发条件）。
        /// 由 TransportHost 在收到 MouseDown/MouseUp 输入事件时调用；
        /// 编码线程在 Encode 前读取，仅鼠标按下时启用 CopyRect 搜索。
        /// </summary>
        public void SetMouseButtonDown(bool isDown)
        {
            _mouseButtonDown = isDown;
            var zrle = _encoder as ZrleEncoder;
            if (zrle != null) zrle.SetMouseButtonDown(isDown);
        }

        /// <summary>
        /// 处理客户端帧请求（阶段三流控）：置请求标志并唤醒编码线程。
        /// 由 TransportHost 在收到 FramebufferUpdateRequest 消息时调用（接收线程）。
        /// </summary>
        public void OnFramebufferUpdateRequest()
        {
            // 降频诊断：每 100 次请求打印一次（每请求落盘 IO 会拖慢接收线程）
            if ((++_frameReqDiagCounter % 100) == 0)
                Logger.Debug("Session {0}: OnFramebufferUpdateRequest: pending={1}->true framesEncoded={2} threadId={3}",
                    _sessionId, _clientRequestPending, Interlocked.Read(ref _framesEncoded),
                    System.Threading.Thread.CurrentThread.ManagedThreadId);
            // 与编码线程的请求消费写（EncodeLoop 内同 _lock）在同一锁内，
            // 消除"置 true / 置 false"交错覆盖窗口（volatile 仅保证可见性）。
            lock (_lock)
            {
                _clientRequestPending = true;
                Monitor.Pulse(_lock);
            }
        }

        /// <summary>获取帧队列当前长度（诊断日志用；调用方不在 _lock 内时安全）。</summary>
        private int GetFrameQueueCount()
        {
            lock (_lock) { return _frameQueue.Count; }
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
