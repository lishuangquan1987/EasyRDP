namespace EasyRDP.Server.Wpf.Models
{
    /// <summary>
    /// 显示器下拉项（服务端采集目标）。
    /// Index 语义与 EasyDesk CaptureOptions.TargetDisplay 一致：
    /// 0=主屏，1..n=按枚举顺序的副屏。
    /// </summary>
    public class DisplayItem
    {
        /// <summary>显示器索引（0=主屏）。</summary>
        public int Index { get; set; }
        /// <summary>显示器描述（如 "Display 1 · 1920×1080 (Primary)"）。</summary>
        public string DisplayText { get; set; }
        /// <summary>宽度（像素）。</summary>
        public int Width { get; set; }
        /// <summary>高度（像素）。</summary>
        public int Height { get; set; }
        /// <summary>是否主显示器。</summary>
        public bool IsPrimary { get; set; }
    }
}
