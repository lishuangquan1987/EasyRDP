namespace EasyRDP.Client.Wpf.Models
{
    /// <summary>控制窗口画面缩放模式（RealVNC 风格三态循环）。</summary>
    public enum ZoomMode
    {
        /// <summary>自适应：等比缩放到窗口（保持宽高比，可能有黑边）。</summary>
        Fit = 0,

        /// <summary>原始大小：不缩放，超出部分裁切。</summary>
        Actual = 1,

        /// <summary>拉伸：填满窗口（不保持宽高比）。</summary>
        Stretch = 2
    }
}
