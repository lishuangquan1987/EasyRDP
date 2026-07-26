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

        public ServerInputSession(IInputSimulator inputSimulator)
        {
            if (inputSimulator == null)
                throw new ArgumentNullException("inputSimulator");
            _inputSimulator = inputSimulator;
        }

        public bool HandleInput(InputEventMessage msg)
        {
            if (_disposed) return false;
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
                        _inputSimulator.SendMouseMove(msg.X, msg.Y, true);
                        return true;
                    case InputEventType.MouseDown:
                        _inputSimulator.SendMouseButton((MouseButton)msg.KeyCode, true);
                        return true;
                    case InputEventType.MouseUp:
                        _inputSimulator.SendMouseButton((MouseButton)msg.KeyCode, false);
                        return true;
                    case InputEventType.MouseWheel:
                        _inputSimulator.SendMouseWheel(msg.WheelDelta);
                        return true;
                    default:
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
