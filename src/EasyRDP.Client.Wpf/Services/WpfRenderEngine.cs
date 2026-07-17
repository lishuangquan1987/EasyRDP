using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EasyRDP.Core.Logging;

namespace EasyRDP.Client.Wpf.Services
{
    /// <summary>
    /// WPF WriteableBitmap 渲染引擎。
    /// 每帧创建新 WriteableBitmap，避免 .NET 4.0 下引用相等导致 Image 不刷新。
    /// BGRA32 像素直接映射（PixelFormats.Bgra32 与 EasyDesk 格式一致）。
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
        /// 渲染一帧。每次创建新 WriteableBitmap → 写入像素 → 叠加光标 → 替换 Source。
        /// </summary>
        public void Render(byte[] bgraPixels, int w, int h)
        {
            try
            {
                _screenW = w;
                _screenH = h;
                var bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
                bitmap.WritePixels(new Int32Rect(0, 0, w, h), bgraPixels, w * 4, 0);

                // 叠加自绘光标
                DrawCursorOverlay(bitmap);

                _bitmap = bitmap;
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
