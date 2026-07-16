namespace EasyRDP.Client.Common
{
    /// <summary>
    /// 输入事件编码器。
    /// 接收 UI 层已映射好的原始值，编码为 InputEventMessage 并序列化为字节数组。
    /// 不负责键盘/鼠标按键映射——映射逻辑在 UI 层（WPF Key→VK 和 Avalonia Key→VK 的映射表不同）。
    /// </summary>
    public class InputEncoder
    {
        /// <summary>编码鼠标移动事件。</summary>
        /// <param name="sequence">消息序号</param>
        /// <param name="absolute">true=绝对坐标，false=相对偏移</param>
        /// <param name="x">X 坐标</param>
        /// <param name="y">Y 坐标</param>
        public byte[] EncodeMouseMove(uint sequence, bool absolute, short x, short y)
        {
            var unit = new EasyRDP.Core.Protocol.InputUnit
            {
                Absolute = absolute,
                X = x,
                Y = y
            };

            var msg = new EasyRDP.Core.Protocol.InputEventMessage
            {
                EventType = EasyRDP.Core.Protocol.InputEventType.MouseMove,
                Units = new[] { unit }
            };

            return EasyRDP.Core.Protocol.MessageCodec.Encode(
                EasyRDP.Core.Protocol.MessageType.InputEvent, sequence, msg);
        }

        /// <summary>编码鼠标按键事件。</summary>
        /// <param name="sequence">消息序号</param>
        /// <param name="isDown">true=按下，false=释放</param>
        /// <param name="button">0=左键 1=右键 2=中键 3=X1 4=X2</param>
        public byte[] EncodeMouseButton(uint sequence, bool isDown, byte button)
        {
            var unit = new EasyRDP.Core.Protocol.InputUnit
            {
                Button = button
            };

            var eventType = isDown
                ? EasyRDP.Core.Protocol.InputEventType.MouseDown
                : EasyRDP.Core.Protocol.InputEventType.MouseUp;

            var msg = new EasyRDP.Core.Protocol.InputEventMessage
            {
                EventType = eventType,
                Units = new[] { unit }
            };

            return EasyRDP.Core.Protocol.MessageCodec.Encode(
                EasyRDP.Core.Protocol.MessageType.InputEvent, sequence, msg);
        }

        /// <summary>编码鼠标滚轮事件。</summary>
        /// <param name="sequence">消息序号</param>
        /// <param name="delta">滚轮增量（正值向上，WHEEL_DELTA=120）</param>
        public byte[] EncodeMouseWheel(uint sequence, short delta)
        {
            var unit = new EasyRDP.Core.Protocol.InputUnit
            {
                WheelDelta = delta
            };

            var msg = new EasyRDP.Core.Protocol.InputEventMessage
            {
                EventType = EasyRDP.Core.Protocol.InputEventType.MouseWheel,
                Units = new[] { unit }
            };

            return EasyRDP.Core.Protocol.MessageCodec.Encode(
                EasyRDP.Core.Protocol.MessageType.InputEvent, sequence, msg);
        }

        /// <summary>编码键盘按键事件。</summary>
        /// <param name="sequence">消息序号</param>
        /// <param name="isDown">true=按下，false=释放</param>
        /// <param name="virtualKey">Windows VK 码</param>
        /// <param name="flags">扩展标志（0x0001=扩展键）</param>
        public byte[] EncodeKey(uint sequence, bool isDown, byte virtualKey, ushort flags)
        {
            var unit = new EasyRDP.Core.Protocol.InputUnit
            {
                VirtualKey = virtualKey,
                KeyFlags = flags
            };

            var eventType = isDown
                ? EasyRDP.Core.Protocol.InputEventType.KeyDown
                : EasyRDP.Core.Protocol.InputEventType.KeyUp;

            var msg = new EasyRDP.Core.Protocol.InputEventMessage
            {
                EventType = eventType,
                Units = new[] { unit }
            };

            return EasyRDP.Core.Protocol.MessageCodec.Encode(
                EasyRDP.Core.Protocol.MessageType.InputEvent, sequence, msg);
        }

        /// <summary>编码 Unicode 文本输入事件。</summary>
        /// <param name="sequence">消息序号</param>
        /// <param name="text">要发送的 Unicode 文本</param>
        public byte[] EncodeUnicodeText(uint sequence, string text)
        {
            var unit = new EasyRDP.Core.Protocol.InputUnit
            {
                Text = text ?? string.Empty
            };

            var msg = new EasyRDP.Core.Protocol.InputEventMessage
            {
                EventType = EasyRDP.Core.Protocol.InputEventType.UnicodeText,
                Units = new[] { unit }
            };

            return EasyRDP.Core.Protocol.MessageCodec.Encode(
                EasyRDP.Core.Protocol.MessageType.InputEvent, sequence, msg);
        }
    }
}
