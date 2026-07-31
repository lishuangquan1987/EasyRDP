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
        private readonly object _lock = new object();
        private readonly List<CursorTrackerSession> _sessions = new List<CursorTrackerSession>();
        private bool _disposed;

        // 上次光标状态（用于检测变化）
        private int _lastX, _lastY;
        private byte[] _lastShapeData;
        private bool _hasLastState;
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

        /// <summary>启动 60Hz 轮询线程。</summary>
        public void Start()
        {
            if (_running) return;
            _running = true;
            _pollThread = new Thread(PollLoop);
            _pollThread.IsBackground = true;
            _pollThread.Start();
        }

        /// <summary>停止所有客户端的光标追踪并结束线程。</summary>
        public void StopAll()
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
                Width = shapeChanged && shapeData != null ? rawInfo.Width : 0,
                Height = shapeChanged && shapeData != null ? rawInfo.Height : 0,
                HotX = shapeChanged ? rawInfo.HotspotX : 0,
                HotY = shapeChanged ? rawInfo.HotspotY : 0,
                RgbaPixels = shapeChanged ? shapeData : null
            };

            byte[] payload = msg.Pack();

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
        private Action<uint, byte[]> _sendTo;
        private uint _sessionId;
        private volatile bool _running;
        internal bool IsRunning { get { return _running; } }

        internal CursorTrackerSession(CursorTracker owner)
        {
            _owner = owner;
        }

        /// <summary>注入本会话的发送回调。</summary>
        public void AttachSendTo(Action<uint, byte[]> sendTo, uint sessionId)
        {
            lock (_sendLock)
            {
                _sendTo = sendTo;
                _sessionId = sessionId;
            }
        }

        /// <summary>启动本会话的光标追踪。</summary>
        public void Start()
        {
            _running = true;
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
            Action<uint, byte[]> sendTo;
            uint sessionId;
            lock (_sendLock)
            {
                if (!_running || _sendTo == null) return;
                sendTo = _sendTo;
                sessionId = _sessionId;
            }
            // 光标消息始终单分片，直接构建线格式发送，不经过 FragAndSend
            // 使用 frameId=0 避免与视频流的 FrameId 命名空间碰撞
            byte[] wire = BuildCursorWire(payload);
            sendTo(sessionId, wire);
        }

        private static byte[] BuildCursorWire(byte[] payload)
        {
            // Magic(1)+Type(1)+PayloadLen(4)+FrameId(4)+FragIdx(2)+FragCount(2)+CRC16(2)+FragData
            int headerSize = 16;
            byte[] wire = new byte[headerSize + payload.Length];
            int pos = 0;
            wire[pos++] = Constants.FrameMagic;
            wire[pos++] = (byte)MessageType.CursorUpdate;
            // PayloadLen (4 bytes LE)
            uint totalLen = (uint)payload.Length;
            wire[pos++] = (byte)(totalLen & 0xFF);
            wire[pos++] = (byte)((totalLen >> 8) & 0xFF);
            wire[pos++] = (byte)((totalLen >> 16) & 0xFF);
            wire[pos++] = (byte)((totalLen >> 24) & 0xFF);
            // FrameId = 0（不与视频流碰撞）
            wire[pos++] = 0; wire[pos++] = 0; wire[pos++] = 0; wire[pos++] = 0;
            // FragIdx = 0（单分片）
            wire[pos++] = 0; wire[pos++] = 0;
            // FragCount = 1
            wire[pos++] = 1; wire[pos++] = 0;
            // CRC16（占位，先写数据再计算）
            if (payload.Length > 0)
                Buffer.BlockCopy(payload, 0, wire, pos + 2, payload.Length);
            ushort crc = MessageReassembler.ComputeCrc16(payload, 0, payload.Length);
            wire[pos++] = (byte)(crc & 0xFF);
            wire[pos++] = (byte)((crc >> 8) & 0xFF);
            return wire;
        }
    }
}
