using System;
using EasyDesk.Core;
using EasyDesk.Core.Models;
using EasyRDP.Core.Protocol;
using EasyRDP.Core.Session;
using NLog;

namespace EasyRDP.Server.Wpf
{
    /// <summary>
    /// 服务端输入会话。事件驱动同步调用，无独立线程。
    /// </summary>
    public class ServerInputSession : IServerInputSession
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly IInputSimulator _inputSimulator;
        private bool _disposed;
        // 鼠标移动诊断计数：每 20 条记录一次请求坐标（Debug），
        // 与 CursorTracker 的回显位置对比可定位"远端光标偏移"问题。
        private int _mouseMoveLogCounter;
        // 最近一次客户端请求的鼠标位置（点击诊断用：点击落点 = 光标所在位置）
        private int _lastRequestedX;
        private int _lastRequestedY;

        public ServerInputSession(IInputSimulator inputSimulator)
        {
            if (inputSimulator == null)
                throw new ArgumentNullException("inputSimulator");
            _inputSimulator = inputSimulator;
        }

        public bool HandleInput(InputEventMessage msg)
        {
            if (_disposed) return false;
            // 诊断入口日志：记录每个到达 HandleInput 的消息类型，
            // 与客户端 SendInput 日志对照可定位消息丢失环节。
            // MouseDown=4 MouseUp=5 KeyDown=1 KeyUp=2 MouseMove=3 MouseWheel=6
            if (msg.Type != InputEventType.MouseMove)
                Logger.Debug("HandleInput entry: type={0} keyCode={1} x={2} y={3} wheel={4}",
                    msg.Type, msg.KeyCode, msg.X, msg.Y, msg.WheelDelta);
            try
            {
                switch (msg.Type)
                {
                    case InputEventType.KeyDown:
                        _inputSimulator.SendKeyDown((VirtualKeyCode)msg.KeyCode);
                        return true;
                    case InputEventType.KeyUp:
                        _inputSimulator.SendKeyUp((VirtualKeyCode)msg.KeyCode);
                        return true;
                    case InputEventType.MouseMove:
                        _lastRequestedX = msg.X;
                        _lastRequestedY = msg.Y;
                        if ((_mouseMoveLogCounter++ % 20) == 0)
                            Logger.Debug("MouseMove requested=({0},{1})", msg.X, msg.Y);
                        _inputSimulator.SendMouseMove(msg.X, msg.Y, true);
                        return true;
                    case InputEventType.MouseDown:
                        // 客户端在 MouseDown 中携带了映射坐标 (X,Y)。
                        // 先移动光标到该坐标再点击：光标可能因编码负载滞后于客户端鼠标位置，
                        // 若不校正，点击会落在旧光标位置（"点击无效果"假象）。
                        // X=Y=0 表示旧版客户端不携带坐标，回退到 lastRequested。
                        int downX = msg.X != 0 || msg.Y != 0 ? msg.X : _lastRequestedX;
                        int downY = msg.X != 0 || msg.Y != 0 ? msg.Y : _lastRequestedY;
                        Logger.Debug("MouseDown button={0} at ({1},{2}) lastRequested=({3},{4})",
                            msg.KeyCode, downX, downY, _lastRequestedX, _lastRequestedY);
                        if (msg.X != 0 || msg.Y != 0)
                            _inputSimulator.SendMouseMove(downX, downY, true);
                        _inputSimulator.SendMouseButton((MouseButton)msg.KeyCode, true);
                        return true;
                    case InputEventType.MouseUp:
                        int upX = msg.X != 0 || msg.Y != 0 ? msg.X : _lastRequestedX;
                        int upY = msg.X != 0 || msg.Y != 0 ? msg.Y : _lastRequestedY;
                        Logger.Debug("MouseUp button={0} at ({1},{2}) lastRequested=({3},{4})",
                            msg.KeyCode, upX, upY, _lastRequestedX, _lastRequestedY);
                        if (msg.X != 0 || msg.Y != 0)
                            _inputSimulator.SendMouseMove(upX, upY, true);
                        _inputSimulator.SendMouseButton((MouseButton)msg.KeyCode, false);
                        return true;
                    case InputEventType.MouseWheel:
                        _inputSimulator.SendMouseWheel(msg.WheelDelta);
                        return true;
                    default:
                        Logger.Warn("HandleInput unknown type={0} keyCode={1}", msg.Type, msg.KeyCode);
                        return false;
                }
            }
            catch (Exception ex)
            {
                // 输入模拟失败时记录日志，便于诊断（之前是静默吞掉，无法排查）
                Logger.Warn(ex, "HandleInput failed: type={0} keyCode=0x{1:X2} x={2} y={3} wheel={4}",
                    msg.Type, msg.KeyCode, msg.X, msg.Y, msg.WheelDelta);
                return false;
            }
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }
}
