using System;
using EasyDesk.Core;
using EasyDesk.Core.Models;
using EasyRDP.Core.Protocol;
using EasyRDP.Core.Session;
using NLog;

namespace EasyRDP.Server.Wpf.Services
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
        // 采集显示器相对主屏的原点偏移：客户端坐标是"选中显示器局部坐标"，
        // EasyDesk 的绝对鼠标映射会加主屏原点，因此这里先补偿 (显示器原点 - 主屏原点)。
        private volatile int _displayOffsetX;
        private volatile int _displayOffsetY;
        // 左键是否按下：拖动（窗口移动/边框缩放、拖拽文件、滚动条）期间改用相对位移。
        // Windows 的移动/缩放模态循环依赖连续的相对鼠标位移，绝对定位（MOUSEEVENTF_ABSOLUTE）
        // 会让光标被"钉住"，表现为鼠标形状变成缩放箭头却拖不动窗口。
        private volatile bool _leftButtonDown;
        private int _relativeMoveCounter;

        public ServerInputSession(IInputSimulator inputSimulator)
        {
            if (inputSimulator == null)
                throw new ArgumentNullException("inputSimulator");
            _inputSimulator = inputSimulator;
        }

        /// <summary>更新采集显示器相对主屏的坐标偏移（切换显示器/热插拔时由 TransportHost 调用）。</summary>
        public void SetDisplayOffset(int offsetX, int offsetY)
        {
            _displayOffsetX = offsetX;
            _displayOffsetY = offsetY;
            Logger.Info("Input display offset set to ({0},{1})", offsetX, offsetY);
        }

        /// <summary>
        /// 注入一次完整按键（按下 + 抬起）。用于剪贴板 Ctrl+V 快路径：
        /// 客户端按键被扣住，文件就绪后由服务端主动回放。
        /// </summary>
        public void InjectKeyTap(VirtualKeyCode key)
        {
            if (_disposed) return;
            try
            {
                _inputSimulator.SendKeyDown(key);
                _inputSimulator.SendKeyUp(key);
                Logger.Info("Injected key tap after clipboard prefetch: vk=0x{0:X2}", (int)key);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "InjectKeyTap failed: vk=0x{0:X2}", (int)key);
            }
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
                        // 客户端坐标是选中显示器的局部坐标；加偏移换算到"主屏坐标系"，
                        // EasyDesk 随后再加主屏原点得到虚拟桌面绝对坐标。
                        int absX = msg.X + _displayOffsetX;
                        int absY = msg.Y + _displayOffsetY;
                        int prevX = _lastRequestedX;
                        int prevY = _lastRequestedY;
                        _lastRequestedX = absX;
                        _lastRequestedY = absY;
                        // 统一使用绝对定位：每个像素直接设置光标，不受系统鼠标加速影响，
                        // 精细拖动（1px 细线、边框缩放）才能跟手。相对位移会被加速曲线放大/压缩。
                        if (_leftButtonDown && (++_relativeMoveCounter % 20) == 0)
                        {
                            Logger.Info("DragMove abs=({0},{1}) from=({2},{3})", absX, absY, prevX, prevY);
                        }
                        else if ((_mouseMoveLogCounter++ % 20) == 0)
                        {
                            Logger.Debug("MouseMove requested=({0},{1}) offset=({2},{3})",
                                msg.X, msg.Y, _displayOffsetX, _displayOffsetY);
                        }
                        _inputSimulator.SendMouseMove(absX, absY, true);
                        return true;
                    case InputEventType.MouseDown:
                        // 客户端在 MouseDown 中携带了映射坐标 (X,Y)。
                        // 先移动光标到该坐标再点击：光标可能因编码负载滞后于客户端鼠标位置，
                        // 若不校正，点击会落在旧光标位置（"点击无效果"假象）。
                        // X=Y=0 表示旧版客户端不携带坐标，回退到 lastRequested。
                        int downX = msg.X != 0 || msg.Y != 0 ? msg.X + _displayOffsetX : _lastRequestedX;
                        int downY = msg.X != 0 || msg.Y != 0 ? msg.Y + _displayOffsetY : _lastRequestedY;
                        Logger.Debug("MouseDown button={0} at ({1},{2}) lastRequested=({3},{4})",
                            msg.KeyCode, downX, downY, _lastRequestedX, _lastRequestedY);
                        if (msg.X != 0 || msg.Y != 0)
                        {
                            _inputSimulator.SendMouseMove(downX, downY, true);
                            // 关键：同步字段，否则拖动期间的增量/诊断基于过期坐标
                            _lastRequestedX = downX;
                            _lastRequestedY = downY;
                        }
                        _inputSimulator.SendMouseButton((MouseButton)msg.KeyCode, true);
                        if (msg.KeyCode == (int)MouseButton.Left)
                        {
                            _leftButtonDown = true;
                            _relativeMoveCounter = 0;
                        }
                        // Info 级别：便于用户回传日志确认输入链路（每键一条，量可控）
                        Logger.Info("MouseDown button={0} at ({1},{2})", msg.KeyCode, downX, downY);
                        return true;
                    case InputEventType.MouseUp:
                        int upX = msg.X != 0 || msg.Y != 0 ? msg.X + _displayOffsetX : _lastRequestedX;
                        int upY = msg.X != 0 || msg.Y != 0 ? msg.Y + _displayOffsetY : _lastRequestedY;
                        Logger.Debug("MouseUp button={0} at ({1},{2}) lastRequested=({3},{4})",
                            msg.KeyCode, upX, upY, _lastRequestedX, _lastRequestedY);
                        // 关键：抬起时不移动光标。旧实现在抬起前把光标移到 up 坐标——
                        // down 与 up 的映射坐标常差 1~2px（取整/移动中），目标应用会把
                        // "按下后有位移"识别为拖动起点，普通点击被当成微型拖动而失效。
                        // 光标位置由之前的 MouseMove 序列保证已到位。
                        _inputSimulator.SendMouseButton((MouseButton)msg.KeyCode, false);
                        if (msg.KeyCode == (int)MouseButton.Left)
                            _leftButtonDown = false;
                        Logger.Info("MouseUp button={0} (cursor stays at {1},{2})",
                            msg.KeyCode, _lastRequestedX, _lastRequestedY);
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
