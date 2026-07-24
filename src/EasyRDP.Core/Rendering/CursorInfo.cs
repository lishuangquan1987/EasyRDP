namespace EasyRDP.Core.Rendering
{
    /// <summary>
    /// 光标状态。RGBA 像素 + 位置 + 热区。RgbaPixels 为 null 时仅更新位置。
    /// </summary>
    public struct CursorInfo
    {
        public bool Visible;
        public int X;
        public int Y;
        public byte[] RgbaPixels;
        public int Width;
        public int Height;
        public int HotX;
        public int HotY;
    }
}
