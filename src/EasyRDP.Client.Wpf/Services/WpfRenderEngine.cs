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
    /// </summary>
    public class WpfRenderEngine
    {
        private WriteableBitmap _bitmap;
        public ImageSource Source { get { return _bitmap; } }

        /// <summary>
        /// 渲染一帧。每次创建新 WriteableBitmap → 写入像素 → 替换 Source。
        /// </summary>
        public void Render(byte[] bgraPixels, int w, int h)
        {
            try
            {
                var bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
                bitmap.WritePixels(new Int32Rect(0, 0, w, h), bgraPixels, w * 4, 0);
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
            _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        }
    }
}
