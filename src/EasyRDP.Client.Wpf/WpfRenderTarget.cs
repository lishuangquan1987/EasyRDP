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
        // 双缓冲拷贝槽：RenderFrame 先把像素从 FrameBuffer 读槽拷出再异步送 UI，
        // 避免 (a) 同步 Invoke 与 UI 线程 Stop/Join 死锁；(b) BeginInvoke 异步执行时
        // 写槽复用同一数组导致画面撕裂。最多缓存 2 帧，超出即丢旧帧（直播语义）。
        private readonly object _renderLock = new object();
        private byte[]? _pendingCopyA;
        private byte[]? _pendingCopyB;
        private int _pendingCount;

        /// <summary>脏矩形局部更新的最大矩形数：超过则回退全帧渲染（WritePixels 调用开销权衡）。</summary>
        private const int MaxDirtyRects = 16;

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
        /// 可在任意线程调用 — 内部先拷贝像素，再通过 BeginInvoke 异步转发到 UI 线程执行
        /// Lock/WritePixels/Unlock。异步转发不会阻塞渲染线程，杜绝 Stop/Join 死锁。</summary>
        public void RenderFrame(byte[] bgraPixels, int w, int h)
        {
            // 已释放时静默忽略而非抛 ObjectDisposedException：
            // 抛异常会杀死渲染线程，且 Stop 竞态下这是正常路径。
            if (_disposed)
                return;
            if (bgraPixels == null || w <= 0 || h <= 0)
                return;

            if (_bitmap == null || w != _width || h != _height)
            {
                if (_uiDispatcher.CheckAccess())
                {
                    DoResize(w, h);
                }
                else
                {
                    // 跨线程尺寸变更（如分辨率切换）：异步创建新 bitmap，本帧丢弃。
                    // 分辨率切换罕见，短暂丢帧远好于同步 Invoke 造成 UI/渲染线程互相等待。
                    _uiDispatcher.BeginInvoke(new Action(() => DoResize(w, h)), DispatcherPriority.Render);
                    return;
                }
            }

            // 拷贝像素到私有槽位（在 ReleaseReadFrame 之前完成，读槽内容仍然有效）
            byte[] slot = null;
            lock (_renderLock)
            {
                if (_pendingCount < 2)
                {
                    slot = _pendingCount == 0 ? _pendingCopyA : _pendingCopyB;
                    if (slot == null || slot.Length < bgraPixels.Length)
                        slot = new byte[bgraPixels.Length];
                    Buffer.BlockCopy(bgraPixels, 0, slot, 0, bgraPixels.Length);
                    _pendingCount++;
                }
            }
            if (slot == null)
                return; // 双缓冲满：UI 线程繁忙，丢弃本帧（直播语义，不积压）

            byte[] posted = slot;
            _uiDispatcher.BeginInvoke(new Action(() =>
            {
                lock (_renderLock)
                {
                    if (_pendingCount > 0)
                        _pendingCount--;
                }
                if (_disposed)
                    return;
                var bmp = _bitmap;
                if (bmp == null || w != _width || h != _height)
                    return;
                bmp.Lock();
                try
                {
                    int stride = w * 4;
                    bmp.WritePixels(
                        new Int32Rect(0, 0, w, h),
                        posted,
                        stride,
                        0);
                }
                finally
                {
                    bmp.Unlock();
                }
            }), DispatcherPriority.Render);
        }

        /// <summary>
        /// 渲染一帧 BGRA 像素（带脏矩形局部更新）。
        /// 可在任意线程调用 — 内部先拷贝像素，再通过 BeginInvoke 异步转发到 UI 线程执行。
        /// 
        /// 语义：
        /// - dirtyRects 为 null（H264 整帧路径）→ 回退到全帧 RenderFrame；
        /// - 空数组（ZRLE 无变化帧）→ 画面无变化，跳过渲染（省 CPU）；
        /// - 超过 MaxDirtyRects 个矩形 → 回退全帧（大量小矩形 WritePixels 调用开销更大）；
        /// - 否则仅对每个脏矩形调用 WritePixels 局部更新。
        /// </summary>
        public void RenderFrame(byte[] bgraPixels, int w, int h, ScreenRect[] dirtyRects)
        {
            if (_disposed || bgraPixels == null || w <= 0 || h <= 0)
                return;

            // H264 整帧路径或过多矩形：回退全帧渲染
            if (dirtyRects == null || dirtyRects.Length > MaxDirtyRects)
            {
                RenderFrame(bgraPixels, w, h);
                return;
            }
            // ZRLE 无变化帧（0 区域）：画面无变化，跳过渲染
            if (dirtyRects.Length == 0)
                return;

            if (_bitmap == null || w != _width || h != _height)
            {
                if (_uiDispatcher.CheckAccess())
                {
                    DoResize(w, h);
                }
                else
                {
                    // 跨线程尺寸变更：异步创建新 bitmap，本帧丢弃（同全帧路径）
                    _uiDispatcher.BeginInvoke(new Action(() => DoResize(w, h)), DispatcherPriority.Render);
                    return;
                }
            }

            // 拷贝像素到私有槽位（局部更新也需要完整源像素作 WritePixels 数据源）
            byte[] slot = null;
            lock (_renderLock)
            {
                if (_pendingCount < 2)
                {
                    slot = _pendingCount == 0 ? _pendingCopyA : _pendingCopyB;
                    if (slot == null || slot.Length < bgraPixels.Length)
                        slot = new byte[bgraPixels.Length];
                    Buffer.BlockCopy(bgraPixels, 0, slot, 0, bgraPixels.Length);
                    _pendingCount++;
                }
            }
            if (slot == null)
                return; // 双缓冲满：UI 线程繁忙，丢弃本帧（直播语义）

            byte[] posted = slot;
            ScreenRect[] rects = dirtyRects;
            _uiDispatcher.BeginInvoke(new Action(() =>
            {
                lock (_renderLock)
                {
                    if (_pendingCount > 0)
                        _pendingCount--;
                }
                if (_disposed)
                    return;
                var bmp = _bitmap;
                if (bmp == null || w != _width || h != _height)
                    return;
                bmp.Lock();
                try
                {
                    int stride = w * 4;
                    for (int i = 0; i < rects.Length; i++)
                    {
                        ScreenRect rect = rects[i];
                        // 脏矩形边界裁剪到帧范围（防御：编码器边缘瓦片可能越界）
                        int x = rect.X < 0 ? 0 : rect.X;
                        int y = rect.Y < 0 ? 0 : rect.Y;
                        int rw = rect.Width;
                        int rh = rect.Height;
                        if (x + rw > w) rw = w - x;
                        if (y + rh > h) rh = h - y;
                        if (rw <= 0 || rh <= 0) continue;
                        bmp.WritePixels(
                            new Int32Rect(x, y, rw, rh),
                            posted,
                            stride,
                            y * stride + x * 4);
                    }
                }
                finally
                {
                    bmp.Unlock();
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
                return;
            if (w <= 0 || h <= 0)
                return;

            if (_uiDispatcher.CheckAccess())
            {
                // 已在 UI 线程，直接执行
                DoResize(w, h);
            }
            else
            {
                // 跨线程调用 — 异步转发，避免同步 Invoke 阻塞调用线程
                // （如接收线程在 Stop 期间调用 Resize 时与 UI 线程互相等待）。
                _uiDispatcher.BeginInvoke(new Action(() => DoResize(w, h)), DispatcherPriority.Normal);
            }
        }

        /// <summary>实际执行 Resize 的内部方法（必须在 UI 线程调用）。
        /// 创建新 WriteableBitmap 后触发 BitmapChanged 事件，确保 ViewModel 的
        /// RenderBitmap 绑定属性同步更新（避免 Image.Source 指向旧 bitmap 导致黑屏）。</summary>
        private void DoResize(int w, int h)
        {
            if (_disposed)
                return; // 防御：Dispose 后排队中的 BeginInvoke(DoResize) 不得重建 bitmap
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
