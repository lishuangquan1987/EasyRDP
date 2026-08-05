#nullable disable
using System;
using System.Diagnostics;
using System.Threading;
using EasyRDP.Core.Protocol;
using EasyRDP.Core.Session;
using EasyRDP.Core.Transport;
using NLog;

namespace EasyRDP.Client.Wpf
{
    /// <summary>
    /// 客户端输入会话。捕获 WPF 键盘/鼠标事件并发送给服务端。
    /// </summary>
    public class ClientInputSession : IClientInputSession
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private ITransportClient _transport;
        private int _screenWidth;
        private int _screenHeight;
        private bool _disposed;
        // 与流帧 ID 命名空间分离；用 int + Interlocked 保证线程安全
        // （SendInput 会被 UI 线程与鼠标节流定时器线程并发调用，uint 的 ++ 不是原子的）
        private int _sendFrameId = 1000;

        // 鼠标移动节流：WPF MouseMove 频率可达 100Hz+，每个事件都发一个分片会打满链路。
        // 这里只保留最新坐标，由定时器按 ~120Hz 合并发送，交互延迟增加 <8ms。
        private readonly object _mouseLock = new object();
        private bool _hasPendingMouse;
        private int _pendingMouseX;
        private int _pendingMouseY;
        // 最近一次实际发送给服务端的鼠标坐标（诊断用，点击时与本地位置/回显对比）
        private int _lastSentMouseX;
        private int _lastSentMouseY;
        private Timer _mouseFlushTimer;
        // 8ms ≈ 120Hz 合并发送：降低输入链路延迟（16ms 时鼠标回显最多多等 16ms）
        private const int MouseFlushIntervalMs = 8;
        // 距上次发送超过该阈值时，QueueMouseMove 立即发送最新坐标（首次移动不等定时器，提升及时性）
        private static readonly long ImmediateFlushTicks = Stopwatch.Frequency / 125;
        private long _lastMouseSendTicks;

        public void Start(ITransportClient transport, int screenWidth, int screenHeight)
        {
            _transport = transport;
            _screenWidth = screenWidth;
            _screenHeight = screenHeight;
            // 防止重复 Start（如重连前未 Stop）导致旧定时器泄漏
            if (_mouseFlushTimer != null)
            {
                try { _mouseFlushTimer.Dispose(); } catch { }
                _mouseFlushTimer = null;
            }
            _mouseFlushTimer = new Timer(state => FlushPendingMouse(), null,
                MouseFlushIntervalMs, MouseFlushIntervalMs);
        }

        /// <summary>服务端分辨率变化通知，更新坐标映射。</summary>
        public void OnResolutionChanged(int newWidth, int newHeight)
        {
            _screenWidth = newWidth;
            _screenHeight = newHeight;
        }

        public void Stop()
        {
            // 先停定时器并等待在途回调结束，再置空 transport，
            // 避免 FlushPendingMouse 在 Stop 返回后仍访问 _transport
            if (_mouseFlushTimer != null)
            {
                try
                {
                    var waitHandle = new ManualResetEvent(false);
                    _mouseFlushTimer.Dispose(waitHandle);
                    waitHandle.WaitOne();
                    waitHandle.Dispose();
                }
                catch { }
                _mouseFlushTimer = null;
            }
            _disposed = true;
            _transport = null;
        }

        public void Dispose()
        {
            Stop();
        }

        /// <summary>发送输入事件到服务端。</summary>
        public void SendInput(InputEventMessage msg)
        {
            if (_disposed || _transport == null)
            {
                Logger.Warn("SendInput skipped: disposed={0} transport={1} type={2} keyCode={3}",
                    _disposed, _transport == null ? "null" : "set", msg.Type, msg.KeyCode);
                return;
            }

            uint frameId = (uint)Interlocked.Increment(ref _sendFrameId);
            byte[] payload = msg.Pack();
            // 诊断：记录每个发送的输入事件（Type/KeyCode/frameId），
            // 与服务端 HandleInput 入口日志对照可定位消息丢失环节。
            // MouseDown=4 MouseUp=5 KeyDown=1 KeyUp=2 MouseMove=3 MouseWheel=6
            if (msg.Type != InputEventType.MouseMove)
                Logger.Debug("SendInput: type={0} keyCode={1} x={2} y={3} frameId={4} payloadLen={5}",
                    msg.Type, msg.KeyCode, msg.X, msg.Y, frameId, payload.Length);
            bool ok = false;
            MessageReassembler.FragAndSend(
                frameId, (byte)MessageType.InputEvent, payload,
                (sid, data) => ok = _transport.Send(data), 0);
            if (!ok && msg.Type != InputEventType.MouseMove)
                Logger.Warn("SendInput transport.Send failed: type={0} keyCode={1} frameId={2}",
                    msg.Type, msg.KeyCode, frameId);
        }

        /// <summary>
        /// 记录待发送的鼠标坐标（节流队列），由内部定时器按 ~120Hz 合并发送。
        /// 比 WPF 原始事件频率低一个数量级，同时保证坐标始终是最新的。
        /// </summary>
        public void QueueMouseMove(int x, int y)
        {
            lock (_mouseLock)
            {
                _pendingMouseX = x;
                _pendingMouseY = y;
                _hasPendingMouse = true;
            }
            // 及时性：距上次发送已超过 ~8ms 时立即发送，连续快速移动仍由定时器合并发送
            if (Stopwatch.GetTimestamp() - Volatile.Read(ref _lastMouseSendTicks) >= ImmediateFlushTicks)
                FlushPendingMouse();
        }

        /// <summary>立即发送待处理的鼠标移动（在鼠标按键/滚轮事件前调用，保证点击位置准确）。</summary>
        public void FlushPendingMouse()
        {
            int x, y;
            lock (_mouseLock)
            {
                if (!_hasPendingMouse) return;
                x = _pendingMouseX;
                y = _pendingMouseY;
                _hasPendingMouse = false;
                // 记录即将发送的坐标（与发送同一临界区，供诊断读取）
                _lastSentMouseX = x;
                _lastSentMouseY = y;
            }
            SendInput(new InputEventMessage { Type = InputEventType.MouseMove, X = x, Y = y });
            Volatile.Write(ref _lastMouseSendTicks, Stopwatch.GetTimestamp());
        }

        /// <summary>获取最近一次发送给服务端的鼠标坐标（诊断用，线程安全读）。</summary>
        public void GetLastSentMouse(out int x, out int y)
        {
            lock (_mouseLock)
            {
                x = _lastSentMouseX;
                y = _lastSentMouseY;
            }
        }

        /// <summary>把客户端控件坐标映射到服务端屏幕坐标。</summary>
        public void MapCoordinates(double controlX, double controlY, double controlW, double controlH,
            out int serverX, out int serverY)
        {
            if (_screenWidth <= 0 || _screenHeight <= 0 || controlW <= 0 || controlH <= 0)
            {
                serverX = (int)controlX;
                serverY = (int)controlY;
                return;
            }

            // 客户端 Image 用 Stretch=Uniform，宽高比不一致时内容居中并留有黑边。
            // 先算出实际绘制区域（居中），黑边内的坐标钳制到绘制区边缘，
            // 再把绘制区坐标映射到服务端像素坐标，避免比例不一致时光标位置失真。
            double aspect = (double)_screenWidth / _screenHeight;
            double drawW = controlW;
            double drawH = controlH;
            if (controlW / controlH > aspect)
            {
                drawH = controlH;
                drawW = controlH * aspect;
            }
            else
            {
                drawW = controlW;
                drawH = controlW / aspect;
            }
            double offX = (controlW - drawW) / 2.0;
            double offY = (controlH - drawH) / 2.0;
            double px = controlX - offX;
            double py = controlY - offY;
            if (px < 0) px = 0;
            else if (px > drawW) px = drawW;
            if (py < 0) py = 0;
            else if (py > drawH) py = drawH;
            serverX = (int)(px / drawW * _screenWidth);
            serverY = (int)(py / drawH * _screenHeight);
        }
    }
}
