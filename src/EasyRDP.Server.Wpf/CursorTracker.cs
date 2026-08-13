#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using EasyDesk.Core;
using EasyDesk.Core.Models;
using EasyRDP.Core.Protocol;
using EasyRDP.Core.Rendering;
using EasyRDP.Core.Transport;

namespace EasyRDP.Server.Wpf
{
    /// <summary>
    /// 全局光标追踪器。实现 ICursorTracker，拥有独立 60Hz 线程轮询 ICursorCapturer，
    /// 检测光标位置与形状变化，分发给所有订阅的 CursorTrackerSession。
    /// </summary>
    public class CursorTracker : ICursorTracker
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
        private readonly ICursorCapturer _capturer;
        private Thread _pollThread;
        private volatile bool _running;
        private volatile int _intervalMs = 16; // ~60Hz
        private volatile bool _enableShape = true;
        // 生命周期锁：Start/StopAll 在并发会话接入/断开下必须串行（防止检查-执行竞态产生双线程）
        private readonly object _lifecycleLock = new object();
        private readonly object _lock = new object();
        private readonly List<CursorTrackerSession> _sessions = new List<CursorTrackerSession>();
        private bool _disposed;

        // 上次光标状态（用于检测变化）
        private int _lastX, _lastY;
        private byte[] _lastShapeData;
        private bool _hasLastState;
        // 最近一次含形状的完整 CursorUpdateMessage payload（供新会话 Start() 时补发初始状态）
        private byte[] _lastFullPayload;
        private long _logCount;

        public int IntervalMs
        {
            get { return _intervalMs; }
            set { _intervalMs = value; }
        }

        public bool EnableShape
        {
            get { return _enableShape; }
            set { _enableShape = value; }
        }

        public CursorTracker(ICursorCapturer capturer)
        {
            if (capturer == null)
                throw new ArgumentNullException("capturer");
            _capturer = capturer;
        }

        /// <summary>为指定 Session 创建光标追踪会话。</summary>
        public ICursorTrackerSession CreateSession()
        {
            lock (_lock)
            {
                var session = new CursorTrackerSession(this);
                _sessions.Add(session);
                return session;
            }
        }

        /// <summary>移除并清理已停止的会话（由外部在 Session 销毁时调用）。</summary>
        public void RemoveSession(ICursorTrackerSession session)
        {
            CursorTrackerSession concrete = session as CursorTrackerSession;
            if (concrete == null) return;
            lock (_lock)
            {
                concrete.StopInternal();
                _sessions.Remove(concrete);
            }
        }

        /// <summary>
        /// 向新启动的会话补发最近一次含形状的完整光标状态（位置 + 热区 + 位图）。
        /// 全局 _hasLastState 只代表"轮询线程已工作过"，不代表新会话已拿到初始位图；
        /// 服务端先于客户端连接运行时，首轮带形状的广播发生在 0 个会话上，之后新会话
        /// 只会收到纯位置更新（RgbaPixels=null），客户端将永远没有光标位图可渲染。
        /// </summary>
        internal void SendInitialState(CursorTrackerSession session)
        {
            byte[] payload;
            lock (_lock)
            {
                payload = _lastFullPayload;
            }
            if (payload != null)
            {
                session.SendCursorUpdate(payload);
                Logger.Info("CursorTracker: sent initial cursor state ({0} bytes) to new session", payload.Length);
            }
            // payload 为 null（服务端刚启动、尚未首次轮询）时由首次轮询（firstUpdate）广播覆盖。
        }

        /// <summary>启动 60Hz 轮询线程。</summary>
        public void Start()
        {
            lock (_lifecycleLock)
            {
                if (_running) return;
                _running = true;
                _pollThread = new Thread(PollLoop);
                _pollThread.IsBackground = true;
                // 降优先级：光标轮询是后台任务，避免与输入处理/编码竞争 CPU
                _pollThread.Priority = ThreadPriority.BelowNormal;
                _pollThread.Start();
            }
        }

        /// <summary>停止所有客户端的光标追踪并结束线程。</summary>
        public void StopAll()
        {
            lock (_lifecycleLock)
            {
                _running = false;
                if (_pollThread != null)
                {
                    _pollThread.Join(2000);
                    _pollThread = null;
                }
                lock (_lock)
                {
                    foreach (var s in _sessions)
                        s.StopInternal();
                    _sessions.Clear();
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopAll();
        }

        private void PollLoop()
        {
            while (_running)
            {
                try
                {
                    PollOnce();
                }
                catch (Exception ex)
                {
                    // 单次轮询失败，记录日志后跳过（不刷屏）
                    _logCount++;
                    if (_logCount == 1 || _logCount % 60 == 0)
                        Logger.Warn(ex, "CursorTracker PollOnce failed (total={0})", _logCount);
                }
                Thread.Sleep(_intervalMs);
            }
        }

        private void PollOnce()
        {
            EasyDesk.Core.Models.CursorInfo rawInfo = _capturer.GetCursorInfo();

            int x = rawInfo.X;
            int y = rawInfo.Y;
            byte[] shapeData = _enableShape ? rawInfo.ImageData : null;

            bool positionChanged = !_hasLastState || x != _lastX || y != _lastY;
            bool shapeChanged = _enableShape && _hasLastState
                && !ArraysEqual(shapeData, _lastShapeData);
            // 首次轮询（_hasLastState=false）必须携带形状数据：
            // 否则客户端永远拿不到初始光标位图，只会更新位置（表现就是"鼠标永远是箭头"）。
            bool firstUpdate = !_hasLastState;
            bool includeShape = _enableShape && (firstUpdate || shapeChanged);

            if (!positionChanged && !shapeChanged)
            {
                _hasLastState = true;
                return; // 无变化，不发送
            }

            _lastX = x;
            _lastY = y;
            _lastShapeData = shapeData;
            _hasLastState = true;

            // 构建 CursorUpdateMessage（仅在形状变化时包含像素数据）
            var msg = new CursorUpdateMessage
            {
                // Windows 无全局"光标隐藏"API，恒为可见
                Visible = true,
                X = x,
                Y = y,
                Width = includeShape && shapeData != null ? rawInfo.Width : 0,
                Height = includeShape && shapeData != null ? rawInfo.Height : 0,
                HotX = includeShape ? rawInfo.HotspotX : 0,
                HotY = includeShape ? rawInfo.HotspotY : 0,
                RgbaPixels = includeShape ? shapeData : null
            };

            byte[] payload = msg.Pack();

            // 缓存最近一次含形状的完整状态，供之后新启动的会话立即补发（SendInitialState）
            if (includeShape)
            {
                lock (_lock)
                {
                    _lastFullPayload = payload;
                }
            }

            // 分发给所有活跃会话
            List<CursorTrackerSession> snapshot;
            lock (_lock)
            {
                snapshot = new List<CursorTrackerSession>(_sessions);
            }

            foreach (var session in snapshot)
            {
                if (session.IsRunning)
                {
                    session.SendCursorUpdate(payload);
                }
            }
        }

        private static bool ArraysEqual(byte[] a, byte[] b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }
    }

    /// <summary>
    /// 会话级光标控制。由 CursorTracker 为每个 Session 派生，
    /// 只能控制本 Session 的光标订阅。
    /// </summary>
    public class CursorTrackerSession : ICursorTrackerSession
    {
        private readonly object _sendLock = new object();
        private readonly CursorTracker _owner;
        private Action<byte[]> _sendTo;
        private volatile bool _running;
        internal bool IsRunning { get { return _running; } }

        internal CursorTrackerSession(CursorTracker owner)
        {
            _owner = owner;
        }

        /// <summary>注入本会话的发送回调（发送完整线格式字节）。</summary>
        public void AttachSendTo(Action<byte[]> sendTo)
        {
            lock (_sendLock)
            {
                _sendTo = sendTo;
            }
        }

        /// <summary>启动本会话的光标追踪。</summary>
        public void Start()
        {
            _running = true;
            // 新会话立即补发最近一次完整光标状态（含形状位图）：
            // 否则服务端已运行多时的新客户端只会收到纯位置更新，永远无法渲染出光标。
            _owner.SendInitialState(this);
        }

        /// <summary>停止本会话的光标追踪（仅设标记，不移除列表）。</summary>
        public void Stop()
        {
            _running = false;
        }

        /// <summary>内部停止（由 owner 调用，绕过公共 Stop 逻辑）。</summary>
        internal void StopInternal()
        {
            _running = false;
        }

        /// <summary>由 CursorTracker 调用，发送光标更新。</summary>
        internal void SendCursorUpdate(byte[] payload)
        {
            Action<byte[]> sendTo;
            lock (_sendLock)
            {
                if (!_running || _sendTo == null) return;
                sendTo = _sendTo;
            }
            // 组装完整消息线格式（无分片、无 CRC16）
            byte[] wire = Framing.BuildMessage((byte)MessageType.CursorUpdate, payload);
            sendTo(wire);
        }
    }
}
