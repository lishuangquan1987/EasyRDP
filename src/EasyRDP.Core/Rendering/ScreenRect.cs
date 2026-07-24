namespace EasyRDP.Core.Rendering
{
    /// <summary>
    /// 屏幕矩形。当前纯 H.264 整帧路径下无脏区用途（dirty 机制已移除）；
    /// 保留此结构供未来分块编码/区域裁剪复用。
    /// </summary>
    public struct ScreenRect
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;
    }
}
