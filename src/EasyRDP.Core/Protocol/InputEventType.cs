namespace EasyRDP.Core.Protocol
{
    /// <summary>输入事件类型。</summary>
    public enum InputEventType : byte
    {
        KeyDown    = 1,
        KeyUp      = 2,
        MouseMove  = 3,
        MouseDown  = 4,
        MouseUp    = 5,
        MouseWheel = 6
    }
}
