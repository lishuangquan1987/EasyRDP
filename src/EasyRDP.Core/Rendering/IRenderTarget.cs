namespace EasyRDP.Core.Rendering
{
    using System;
    /// <summary>
    /// 平台渲染后端接口。输入 BGRA32 原始像素，不预设渲染方式。
    /// WPF: WriteableBitmap.WritePixels；Avalonia: SKBitmap 或 WriteableBitmap。
    /// 光标叠加方式由各平台自行决定，接口不规定。
    /// V1 纯 H.264 整帧路径下恒为全屏刷新。
    /// </summary>
    public interface IRenderTarget : IDisposable
    {
        /// <summary>渲染一帧 BGRA32 像素到平台渲染目标。</summary>
        void RenderFrame(byte[] bgraPixels, int w, int h);

        /// <summary>更新光标状态。光标与视频帧在不同渲染层，无同步问题。</summary>
        void UpdateCursor(CursorInfo cursor);

        /// <summary>预分配/重建渲染资源（连接成功或分辨率变更时调用）。</summary>
        void Resize(int w, int h);
    }
}
