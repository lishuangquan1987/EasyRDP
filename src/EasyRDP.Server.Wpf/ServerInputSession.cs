using System;
using EasyDesk.Core;
using EasyDesk.Core.Models;
using EasyRDP.Core.Protocol;
using EasyRDP.Core.Session;

namespace EasyRDP.Server.Wpf
{
    /// <summary>
    /// 服务端输入会话。事件驱动同步调用，无独立线程。
    /// </summary>
    public class ServerInputSession : IServerInputSession
    {
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
            catch (Exception)
            {
                return false;
            }
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }
}
