namespace EasyRDP.Core.Rendering
{
    /// <summary>
    /// 光标状态。RGBA 像素 + 位置 + 热区。RgbaPixels 为 null 时仅更新位置。
    /// </summary>
    public struct CursorInfo
    {
        /// <summary>Whether the cursor is visible.</summary>
        public bool Visible;
        /// <summary>Cursor X position on the screen.</summary>
        public int X;
        /// <summary>Cursor Y position on the screen.</summary>
        public int Y;
        /// <summary>RGBA pixel data for the cursor bitmap. Null when only position is updated.</summary>
        public byte[] RgbaPixels;
        /// <summary>Width of the cursor bitmap in pixels.</summary>
        public int Width;
        /// <summary>Height of the cursor bitmap in pixels.</summary>
        public int Height;
        /// <summary>Horizontal hot spot offset relative to the cursor bitmap origin.</summary>
        public int HotX;
        /// <summary>Vertical hot spot offset relative to the cursor bitmap origin.</summary>
        public int HotY;
    }
}
