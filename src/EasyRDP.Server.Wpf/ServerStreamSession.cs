using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using EasyDesk.Core;
using EasyDesk.Core.Models;
using EasyRDP.Core.Protocol;
using EasyRDP.Core.Services;
using EasyRDP.Core.Session;
using EasyRDP.Core.Transport;

namespace EasyRDP.Server.Wpf
{
    /// <summary>
    /// 服务端视频流会话。三线程模型：截屏回调（CaptureService 线程）→ 编码线程 → 发送线程。
    /// 两级有界队列：_frameQueue（截屏→编码）、_sendQueue（编码→发送）。
    /// </summary>
    public class ServerStreamSession : IServerStreamSession
    {
        private readonly ICaptureService _captureService;
        private readonly Action<uint, byte[]> _sendTo;
        private readonly ICursorTracker _cursorTracker;
        private readonly object _lock = new object();

        private uint _sessionId;
        private CodecId _codec;
        private IVideoEncoder _encoder;
        private volatile bool _running;
        private volatile bool _stopping;

        // Two-level queues
        private Queue<CapturedFrame> _frameQueue = new Queue<CapturedFrame>();
        private int _frameQueueCapacity = 2;
        private Queue<FrameToSend> _sendQueue = new Queue<FrameToSend>();
        private int _sendQueueCapacity = 2;

        // Capture double-buffer
        private byte[] _captureBufA;
        private byte[] _captureBufB;
        private bool _useBufA = true;
        private int _lastW, _lastH;

        // Sequence
        private long _sequenceNumber;

        // D11 adaptive
        private Queue<long> _encodeTimes = new Queue<long>();
        private const int AdaptiveWindow = 30;
        private int _adaptiveLevel;
        private long _adaptiveTargetMs = 33; // ~30fps

        // D12 global load
        private int _globalLoadLevel;

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
            FrameDelayMs = 33; // ~30fps default
            KeyframeInterval = 30;
            TargetBitrate = 2000000;
        }

        public void Start(uint sessionId, CodecId codec)
        {
            if (_running) return;
            _sessionId = sessionId;
            _codec = codec;

            // Create encoder
            _encoder = EncoderFactory.Create(codec);

            // Get screen dimensions
            var bounds = _captureService.GetPrimaryScreen();
            _lastW = bounds.Width;
            _lastH = bounds.Height;

            if (_encoder != null)
            {
                _encoder.Initialize(_lastW, _lastH, TargetBitrate);
            }

            // Pre-allocate double buffers
            int size = _lastW * _lastH * 4;
            _captureBufA = new byte[size];
            _captureBufB = new byte[size];

            _running = true;
            _stopping = false;

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
            _encodeThread.Start();

            _sendThread = new Thread(SendLoop);
            _sendThread.IsBackground = true;
            _sendThread.Start();
        }

        public void Stop()
        {
            if (!_running) return;
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
            if (_encodeThread != null)
            {
                if (!_encodeThread.Join(3000))
                {
                    // Encoder stuck — mark for deferred cleanup, don't Dispose encoder
                    Log("Encode thread timeout — encoder deferred cleanup");
                }
                _encodeThread = null;
            }
            if (_sendThread != null)
            {
                _sendThread.Join(3000);
                _sendThread = null;
            }

            // Stop cursor tracking
            if (_cursorSession != null)
            {
                _cursorSession.Stop();
                _cursorTracker?.RemoveSession(_cursorSession);
                _cursorSession = null;
            }

            if (_encoder != null && _encodeThread == null)
            {
                // Only dispose if thread joined cleanly
                _encoder.Dispose();
            }
            _encoder = null;

            _running = false;
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

            // Double-buffer copy
            byte[] targetBuf = _useBufA ? _captureBufA : _captureBufB;
            _useBufA = !_useBufA;

            // Resize buffer if needed
            if (targetBuf.Length < frameSize)
            {
                if (_useBufA)
                    _captureBufA = new byte[frameSize];
                else
                    _captureBufB = new byte[frameSize];
                targetBuf = _useBufA ? _captureBufA : _captureBufB;
            }

            // Copy pixels
            System.Runtime.InteropServices.Marshal.Copy(frame.Scan0, targetBuf, 0, frameSize);

            // Enqueue
            lock (_lock)
            {
                if (_frameQueue.Count < _frameQueueCapacity)
                {
                    var cf = new CapturedFrame
                    {
                        Pixels = targetBuf,
                        Width = frame.Width,
                        Height = frame.Height,
                        CaptureTimestamp = Stopwatch.GetTimestamp()
                    };
                    _frameQueue.Enqueue(cf);
                    Monitor.Pulse(_lock);
                }
                // else: queue full, drop frame
            }
        }

        private void EncodeLoop()
        {
            long lastEncodeTimestamp = 0;

            while (!_stopping)
            {
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

                // Throttle
                if (FrameDelayMs > 0)
                {
                    long now = Stopwatch.GetTimestamp();
                    long elapsed = (now - lastEncodeTimestamp) * 1000 / Stopwatch.Frequency;
                    if (elapsed < FrameDelayMs - 1)
                    {
                        Thread.Sleep(FrameDelayMs - (int)elapsed);
                    }
                }
                lastEncodeTimestamp = Stopwatch.GetTimestamp();

                // Check resolution change
                bool resolutionChanged = false;
                if (frame.Width != _lastW || frame.Height != _lastH)
                {
                    _lastW = frame.Width;
                    _lastH = frame.Height;
                    resolutionChanged = true;
                }

                // Encode
                EncodedFrame? encoded = null;
                if (_encoder != null && _encoder.IsAvailable)
                {
                    if (resolutionChanged)
                    {
                        _encoder.Reset();
                        _encoder.Initialize(_lastW, _lastH, TargetBitrate);
                    }

                    bool forceKey = resolutionChanged
                        || (_sequenceNumber % KeyframeInterval == 0);

                    long encodeStart = Stopwatch.GetTimestamp();
                    EncodedFrame result = _encoder.Encode(frame.Pixels, forceKey);
                    encoded = result;
                    long encodeEnd = Stopwatch.GetTimestamp();

                    // D11: track encode time
                    lock (_lock)
                    {
                        _encodeTimes.Enqueue(encodeEnd - encodeStart);
                        if (_encodeTimes.Count > AdaptiveWindow)
                            _encodeTimes.Dequeue();
                    }
                }

                // Build VideoFrameMessage (fallback to raw pixels if no encoder)
                var vfm = new VideoFrameMessage
                {
                    Width = frame.Width,
                    Height = frame.Height,
                    IsKeyframe = encoded.HasValue && encoded.Value.IsKeyframe,
                    SequenceNumber = _sequenceNumber++,
                    Data = encoded.HasValue ? encoded.Value.Data : frame.Pixels
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
                    if (_sendQueue.Count < _sendQueueCapacity)
                    {
                        _sendQueue.Enqueue(fts);
                        Monitor.Pulse(_lock);
                    }
                    // else: skip frame
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
                MessageReassembler.FragAndSend(
                    sendFrameId++, (byte)MessageType.VideoFrame, fts.Data,
                    _sendTo, _sessionId);
            }
        }

        private void Log(string message)
        {
            System.Diagnostics.Debug.WriteLine("[ServerStreamSession " + _sessionId + "] " + message);
        }
    }
}
