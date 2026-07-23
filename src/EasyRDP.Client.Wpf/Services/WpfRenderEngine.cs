using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EasyRDP.Core.Logging;
using EasyRDP.Core.Protocol;

namespace EasyRDP.Client.Wpf.Services
{
    /// <summary>
    /// WPF WriteableBitmap 渲染引擎。
    /// 复用单个 WriteableBitmap，仅尺寸变化时重建，避免每帧分配带来的 GC 压力。
    /// BGRA32 像素直接映射（PixelFormats.Bgra32 与 EasyDesk 格式一致）。
    /// 支持按脏矩形局部 WritePixels，减少每帧刷新面积。
    /// 支持自绘光标叠加，消除系统光标闪烁。
    /// </summary>
    public class WpfRenderEngine
    {
        private WriteableBitmap _bitmap;
        private int _screenW, _screenH;

        // 光标状态
        private byte[] _cursorRgba;
        private int _cursorW, _cursorH;
        private int _cursorHotX, _cursorHotY;
        private int _cursorX, _cursorY;
        private bool _cursorVisible;

        public ImageSource Source { get { return _bitmap; } }

        /// <summary>
        /// 渲染一帧（全屏刷新）。保留旧 API 兼容。
        /// </summary>
        public void Render(byte[] bgraPixels, int w, int h)
        {
            Render(bgraPixels, w, h, null);
        }

        /// <summary>
        /// 渲染一帧。复用 WriteableBitmap，仅尺寸变化时重建。
        /// 当 dirtyRects 非空时按脏区逐块 WritePixels，否则全屏 WritePixels。
        /// 写入像素后叠加自绘光标。
        /// </summary>
        /// <param name="dirtyRects">自上次渲染后变化的屏幕区域；null 或空表示全屏</param>
        public void Render(byte[] bgraPixels, int w, int h, ScreenRect[] dirtyRects)
        {
            if (bgraPixels == null || w <= 0 || h <= 0)
                return;

            try
            {
                _screenW = w;
                _screenH = h;

                // 尺寸变化或首次：重建位图
                if (_bitmap == null || _bitmap.PixelWidth != w || _bitmap.PixelHeight != h)
                {
                    _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
                    dirtyRects = null; // 重建后强制全屏
                }

                int stride = w * 4;

                if (dirtyRects == null || dirtyRects.Length == 0)
                {
                    // 全屏
                    _bitmap.WritePixels(new Int32Rect(0, 0, w, h), bgraPixels, stride, 0);
                }
                else
                {
                    // 逐脏区写入。脏区坐标来自服务端 ScreenRect（屏幕像素坐标）
                    // 全帧时 dirtyRects 含一个整屏矩形，等价全屏 WritePixels
                    for (int i = 0; i < dirtyRects.Length; i++)
                    {
                        var r = dirtyRects[i];
                        if (r.Width <= 0 || r.Height <= 0) continue;
                        if (r.X < 0 || r.Y < 0 || r.X + r.Width > w || r.Y + r.Height > h) continue;

                        int srcOffset = (r.Y * w + r.X) * 4;
                        _bitmap.WritePixels(
                            new Int32Rect(r.X, r.Y, r.Width, r.Height),
                            bgraPixels, stride, srcOffset);
                    }
                }

                // 叠加自绘光标
                DrawCursorOverlay(_bitmap);
            }
            catch (Exception ex)
            {
                LogHelper.Error(ex, string.Format("WpfRenderEngine.Render 失败: w={0} h={1} pixels={2}", w, h, bgraPixels != null ? bgraPixels.Length : 0));
            }
        }

        /// <summary>
        /// 预分配指定尺寸的空白 WriteableBitmap（连接成功时调用）。
        /// </summary>
        public void Resize(int w, int h)
        {
            _screenW = w;
            _screenH = h;
            if (_bitmap == null || _bitmap.PixelWidth != w || _bitmap.PixelHeight != h)
                _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        }

        /// <summary>
        /// 更新光标状态。
        /// </summary>
        /// <param name="visible">是否可见</param>
        /// <param name="x">屏幕 X 坐标</param>
        /// <param name="y">屏幕 Y 坐标</param>
        /// <param name="rgbaPixels">RGBA 像素数据（可为 null 表示形状未变）</param>
        /// <param name="cursorW">光标宽度（有形状数据时有效）</param>
        /// <param name="cursorH">光标高度（有形状数据时有效）</param>
        /// <param name="hotX">热区 X（有形状数据时有效）</param>
        /// <param name="hotY">热区 Y（有形状数据时有效）</param>
        public void SetCursor(bool visible, int x, int y,
            byte[] rgbaPixels, int cursorW, int cursorH, int hotX, int hotY)
        {
            _cursorVisible = visible;
            _cursorX = x;
            _cursorY = y;

            if (rgbaPixels != null && cursorW > 0 && cursorH > 0)
            {
                _cursorRgba = rgbaPixels;
                _cursorW = cursorW;
                _cursorH = cursorH;
                _cursorHotX = hotX;
                _cursorHotY = hotY;
            }
        }

        /// <summary>
        /// 在 WriteableBitmap 上叠加绘制光标（Alpha 混合）。
        /// 使用 CopyPixels → 混合 → WritePixels，兼容 .NET 4.0 WPF。
        /// </summary>
        private void DrawCursorOverlay(WriteableBitmap bmp)
        {
            if (!_cursorVisible || _cursorRgba == null || _cursorW <= 0 || _cursorH <= 0)
                return;

            try
            {
                int drawX = _cursorX - _cursorHotX;
                int drawY = _cursorY - _cursorHotY;

                // 计算光标与位图的交集
                int srcStartX = Math.Max(0, -drawX);
                int srcStartY = Math.Max(0, -drawY);
                int destStartX = Math.Max(0, drawX);
                int destStartY = Math.Max(0, drawY);
                // 计算光标与位图的交集（fallback to bitmap dimensions）
                int bmpW = _screenW > 0 ? _screenW : bmp.PixelWidth;
                int bmpH = _screenH > 0 ? _screenH : bmp.PixelHeight;
                int copyW = Math.Min(_cursorW - srcStartX, bmpW - destStartX);
                int copyH = Math.Min(_cursorH - srcStartY, bmpH - destStartY);

                if (copyW <= 0 || copyH <= 0)
                    return;

                // 读取目标区域现有像素
                int stride = copyW * 4;
                byte[] destPixels = new byte[copyH * stride];
                bmp.CopyPixels(new Int32Rect(destStartX, destStartY, copyW, copyH), destPixels, stride, 0);

                // Alpha 混合
                for (int cy = 0; cy < copyH; cy++)
                {
                    for (int cx = 0; cx < copyW; cx++)
                    {
                        int srcIdx = ((srcStartY + cy) * _cursorW + (srcStartX + cx)) * 4;
                        byte a = _cursorRgba[srcIdx + 3]; // RGBA → A

                        if (a == 0) continue; // 完全透明

                        int destIdx = (cy * copyW + cx) * 4;
                        if (a == 255)
                        {
                            // 完全不透明：RGBA → BGRA
                            destPixels[destIdx] = _cursorRgba[srcIdx + 2];     // B
                            destPixels[destIdx + 1] = _cursorRgba[srcIdx + 1]; // G
                            destPixels[destIdx + 2] = _cursorRgba[srcIdx];     // R
                            destPixels[destIdx + 3] = 255;                     // A
                        }
                        else
                        {
                            // Alpha 混合
                            float alpha = a / 255f;
                            destPixels[destIdx] = (byte)(_cursorRgba[srcIdx + 2] * alpha + destPixels[destIdx] * (1 - alpha));
                            destPixels[destIdx + 1] = (byte)(_cursorRgba[srcIdx + 1] * alpha + destPixels[destIdx + 1] * (1 - alpha));
                            destPixels[destIdx + 2] = (byte)(_cursorRgba[srcIdx] * alpha + destPixels[destIdx + 2] * (1 - alpha));
                            destPixels[destIdx + 3] = 255;
                        }
                    }
                }

                // 写回混合后的像素
                bmp.WritePixels(new Int32Rect(destStartX, destStartY, copyW, copyH), destPixels, stride, 0);
            }
            catch { /* 光标绘制失败不应影响帧渲染 */ }
        }
    }
}
