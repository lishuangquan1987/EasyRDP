using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EasyRDP.Core.Rendering;

namespace EasyRDP.Client.Wpf
{
    /// <summary>
    /// WPF 渲染目标。通过 WriteableBitmap 推 BGRA32 像素到 WPF Image 控件。
    /// </summary>
    public class WpfRenderTarget : IRenderTarget
    {
        private WriteableBitmap? _bitmap;
        private int _width;
        private int _height;
        private bool _disposed;

        /// <summary>渲染目标位图，WPF 窗口通过 Image.Source 绑定此属性。</summary>
        public WriteableBitmap? Bitmap
        {
            get { return _bitmap; }
        }

        /// <summary>CursorInfo changed event — WPF window can subscribe to update cursor overlay.</summary>
        public event Action<CursorInfo>? CursorChanged;

        public WpfRenderTarget()
        {
        }

        public void RenderFrame(byte[] bgraPixels, int w, int h)
        {
            if (_disposed)
                throw new ObjectDisposedException("WpfRenderTarget");
            if (_bitmap == null || w != _width || h != _height)
                Resize(w, h);

            var bitmap = _bitmap!;
            bitmap.Lock();
            try
            {
                int stride = w * 4;
                bitmap.WritePixels(
                    new Int32Rect(0, 0, w, h),
                    bgraPixels,
                    stride,
                    0);
            }
            finally
            {
                bitmap.Unlock();
            }
        }

        public void UpdateCursor(CursorInfo cursor)
        {
            if (_disposed)
                return;
            var handler = CursorChanged;
            if (handler != null)
                handler(cursor);
        }

        public void Resize(int w, int h)
        {
            if (_disposed)
                throw new ObjectDisposedException("WpfRenderTarget");
            _width = w;
            _height = h;
            _bitmap = new WriteableBitmap(
                w, h, 96, 96, PixelFormats.Bgra32, null);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _bitmap = null;
        }
    }
}
