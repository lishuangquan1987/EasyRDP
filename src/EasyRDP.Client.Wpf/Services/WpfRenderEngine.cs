using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EasyRDP.Client.Wpf.Services
{
    /// <summary>
    /// WPF WriteableBitmap 渲染引擎。
    /// BGRA32 像素直接映射（PixelFormats.Bgra32 与 EasyDesk 格式一致）。
    /// </summary>
    public class WpfRenderEngine
    {
        private WriteableBitmap _bitmap;
        public ImageSource Source { get { return _bitmap; } }

        public void Render(byte[] bgraPixels, int w, int h)
        {
            if (_bitmap == null || _bitmap.PixelWidth != w || _bitmap.PixelHeight != h)
            {
                _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
            }
            try
            {
                _bitmap.WritePixels(new Int32Rect(0, 0, w, h), bgraPixels, w * 4, 0);
            }
            catch { }
        }

        public void Resize(int w, int h)
        {
            _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        }
    }
}
