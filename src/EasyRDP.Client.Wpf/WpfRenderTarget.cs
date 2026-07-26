using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EasyRDP.Core.Rendering;

namespace EasyRDP.Client.Wpf
{
    /// <summary>
    /// WPF 渲染目标。通过 WriteableBitmap 推 BGRA32 像素到 WPF Image 控件。
    /// WriteableBitmap 是 DispatcherObject，只能在创建它的线程（UI 线程）上访问。
    /// 本类捕获 UI 线程的 Dispatcher，将所有 bitmap 操作转发到 UI 线程执行，
    /// 这样渲染线程（RenderLoop）可以安全调用 RenderFrame。
    /// </summary>
    public class WpfRenderTarget : IRenderTarget
    {
        private WriteableBitmap? _bitmap;
        private int _width;
        private int _height;
        private bool _disposed;
        // 捕获构造时（UI 线程）的 Dispatcher，后续 RenderFrame/Resize 通过它转发到 UI 线程
        private readonly Dispatcher _uiDispatcher;

        /// <summary>渲染目标位图，WPF 窗口通过 Image.Source 绑定此属性。
        /// 必须在 UI 线程访问。</summary>
        public WriteableBitmap? Bitmap
        {
            get { return _bitmap; }
        }

        /// <summary>CursorInfo changed event — WPF window can subscribe to update cursor overlay.</summary>
        public event Action<CursorInfo>? CursorChanged;

        /// <summary>Bitmap 被替换（Resize 创建新 WriteableBitmap）时触发。
        /// ViewModel 订阅此事件以同步更新 RenderBitmap 绑定属性。
        /// 事件在 UI 线程触发（DoResize 在 UI 线程执行）。</summary>
        public event Action<WriteableBitmap?>? BitmapChanged;

        /// <summary>构造渲染目标。必须在 UI 线程调用，以便捕获正确的 Dispatcher。</summary>
        public WpfRenderTarget()
        {
            _uiDispatcher = Dispatcher.CurrentDispatcher;
        }

        /// <summary>渲染一帧 BGRA 像素到 WriteableBitmap。
        /// 可在任意线程调用 — 内部通过 Dispatcher 转发到 UI 线程执行 Lock/WritePixels/Unlock。</summary>
        public void RenderFrame(byte[] bgraPixels, int w, int h)
        {
            if (_disposed)
                throw new ObjectDisposedException("WpfRenderTarget");

            // 尺寸不匹配时先同步 Resize（必须完成才能继续渲染）
            if (_bitmap == null || w != _width || h != _height)
            {
                Resize(w, h);
            }

            // 异步转发到 UI 线程执行 WritePixels — 不阻塞渲染线程
            // 注意：bitmap 可能在 Dispose 后被置 null，需在 delegate 内再次检查
            var bitmap = _bitmap;
            if (bitmap == null) return;

            _uiDispatcher.BeginInvoke(new Action(() =>
            {
                if (_disposed || bitmap != _bitmap) return;
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
            }), DispatcherPriority.Render);
        }

        /// <summary>更新光标信息。通过事件通知订阅者（MainWindow）。</summary>
        public void UpdateCursor(CursorInfo cursor)
        {
            if (_disposed)
                return;
            var handler = CursorChanged;
            if (handler != null)
                handler(cursor);
        }

        /// <summary>调整 bitmap 尺寸。同步转发到 UI 线程执行，确保调用返回后 bitmap 已就绪。</summary>
        public void Resize(int w, int h)
        {
            if (_disposed)
                throw new ObjectDisposedException("WpfRenderTarget");

            if (_uiDispatcher.CheckAccess())
            {
                // 已在 UI 线程，直接执行
                DoResize(w, h);
            }
            else
            {
                // 跨线程调用 — 同步转发，确保返回后 bitmap 已就绪
                _uiDispatcher.Invoke(new Action(() => DoResize(w, h)), DispatcherPriority.Normal);
            }
        }

        /// <summary>实际执行 Resize 的内部方法（必须在 UI 线程调用）。
        /// 创建新 WriteableBitmap 后触发 BitmapChanged 事件，确保 ViewModel 的
        /// RenderBitmap 绑定属性同步更新（避免 Image.Source 指向旧 bitmap 导致黑屏）。</summary>
        private void DoResize(int w, int h)
        {
            _width = w;
            _height = h;
            _bitmap = new WriteableBitmap(
                w, h, 96, 96, PixelFormats.Bgra32, null);
            var handler = BitmapChanged;
            if (handler != null) handler(_bitmap);
        }

        /// <summary>释放资源。必须在 UI 线程调用。</summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _bitmap = null;
        }
    }
}
