namespace EasyRDP.Core.Rendering
{
    /// <summary>
    /// 屏幕矩形。当前纯 H.264 整帧路径下无脏区用途（dirty 机制已移除）；
    /// 保留此结构供未来分块编码/区域裁剪复用。
    /// </summary>
    public struct ScreenRect
    {
        /// <summary>X origin of the rectangle in screen coordinates.</summary>
        public int X;
        /// <summary>Y origin of the rectangle in screen coordinates.</summary>
        public int Y;
        /// <summary>Width of the rectangle in pixels.</summary>
        public int Width;
        /// <summary>Height of the rectangle in pixels.</summary>
        public int Height;
    }
}
