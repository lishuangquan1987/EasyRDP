#nullable disable
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EasyRDP.Core.Rendering;
using NLog;

namespace EasyRDP.Client.Wpf;

/// <summary>
/// 客户端主窗口（View 层）。仅负责初始化 ViewModel 和路由鼠标事件。
/// 所有业务逻辑在 MainWindowViewModel 中。
/// </summary>
public partial class MainWindow : Window
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly MainWindowViewModel _vm;
    // 全屏状态：由 SetFullscreenMode 维护，WndProc 据此决定 WM_GETMINMAXINFO 返回值。
    // 全屏 = WindowStyle.None + Maximized + 最大化尺寸覆盖整个监视器（含任务栏区域），
    // 且绝不使用 Topmost（置顶窗口会锁死用户切换前台窗口，断连后尤其危险）。
    private bool _fullscreen;

    private const int WM_GETMINMAXINFO = 0x0024;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    // 剪贴板变化通知（及时性）：WM_CLIPBOARDUPDATE（Vista/Win7 起可用）
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll")]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    // 远程光标叠加状态
    private WriteableBitmap? _cursorBitmap;
    private bool _remoteCursorVisible;
    private int _cursorHotX;
    private int _cursorHotY;
    // 点击位置标记（诊断）及其隐藏定时器
    private DispatcherTimer _clickMarkerTimer;
    // 剪贴板监听窗口句柄（OnSourceInitialized 注册，OnClosing 注销）
    private IntPtr _clipboardListenerHwnd;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    /// <summary>Win32 MONITORINFO（40 字节）。RECT 平铺为 4 个 int。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public int rcMonitorLeft;
        public int rcMonitorTop;
        public int rcMonitorRight;
        public int rcMonitorBottom;
        public int rcWorkLeft;
        public int rcWorkTop;
        public int rcWorkRight;
        public int rcWorkBottom;
        public uint dwFlags;
    }

    public MainWindow()
    {
        InitializeComponent();
        // 窗口句柄创建后挂接原生消息钩子（处理 WM_GETMINMAXINFO 实现真正的全屏）
        SourceInitialized += OnSourceInitialized;
        _vm = new MainWindowViewModel();
        DataContext = _vm;
        // PasswordBox 不支持绑定 Password（安全设计），初始值在 XAML 构造后同步一次，
        // 后续变化由 PasswordChanged 事件同步到 ViewModel。
        PasswordBox.Password = _vm.Password ?? string.Empty;

        // 连接状态指示灯：连接成功后状态点变绿
        _vm.PropertyChanged += (s, e) => OnViewModelPropertyChanged(e.PropertyName);
        // 远程光标更新（接收线程触发，内部转 UI 线程）
        _vm.RemoteCursorChanged += OnRemoteCursorChanged;
        // 显示区尺寸变化时重算远程光标叠加层位置。
        // RenderImage 用 Stretch=Uniform，尺寸由布局系统按 RenderBorder 自动管理。
        RenderBorder.SizeChanged += (s, e) =>
        {
            if (_remoteCursorVisible)
            {
                if (_hasLocalCursorPos)
                {
                    var pos = Mouse.GetPosition(RenderImage);
                    _localCursorX = pos.X;
                    _localCursorY = pos.Y;
                    PositionRemoteCursorAtLocal();
                }
                else
                    UpdateCursorPosition(_lastRemoteCursorX, _lastRemoteCursorY);
            }
        };
    }

    // 最近一次远程光标坐标（SizeChanged 重定位用）
    private int _lastRemoteCursorX;
    private int _lastRemoteCursorY;
    // 本地鼠标位置（RenderImage 坐标系）：远程光标叠加层直接锚定于此。
    // 前提是服务端坐标映射已修正（主屏捕获 + 主屏原点换算），
    // 此时"本地指针位置"与"远程操作落点"严格一致，指针显示不再等待回显，零延迟。
    private double _localCursorX;
    private double _localCursorY;
    /// <summary>尺寸诊断日志计数器（每 50 次 MouseMove 打印一次 Border/Image/bitmap/DPI）。</summary>
    private int _sizeDiagCounter;
    private bool _hasLocalCursorPos;

    /// <summary>当前是否处于全屏模式（供 ViewModel/快捷键判断）。</summary>
    public bool IsFullscreenMode
    {
        get { return _fullscreen; }
    }

    /// <summary>
    /// 切换全屏模式：进入全屏时用 WindowStyle.None + Maximized，
    /// 并通过 WM_GETMINMAXINFO 把最大化尺寸撑到整个监视器（含任务栏区域）。
    /// 退出全屏恢复普通窗口。绝不使用 Topmost，保证用户随时能切走。
    /// </summary>
    /// <param name="fullscreen">true=进入全屏，false=退出全屏。</param>
    public void SetFullscreenMode(bool fullscreen)
    {
        if (_fullscreen == fullscreen) return;
        _fullscreen = fullscreen;

        if (fullscreen)
        {
            // 先无边框再最大化：无边框窗口最大化默认只铺工作区（任务栏仍可见），
            // WndProc 拦截 WM_GETMINMAXINFO 返回 rcMonitor 后才会盖住任务栏。
            WindowStyle = WindowStyle.None;
            // 若窗口此前已是 Maximized（工作区尺寸），改样式不会触发重新查询 MINMAXINFO，
            // 必须先复位 Normal 再 Maximized，保证每次进全屏都重新走最大化流程 → 稳定盖住任务栏。
            if (WindowState == WindowState.Maximized)
                WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
            Topmost = false;
            SetFullscreenUI(true);
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = WindowState.Normal;
            Topmost = false;
            SetFullscreenUI(false);
        }
    }

    /// <summary>窗口句柄就绪：挂接原生消息钩子。</summary>
    private void OnSourceInitialized(object sender, System.EventArgs e)
    {
        var source = (HwndSource)HwndSource.FromVisual(this);
        if (source != null)
        {
            source.AddHook(WndProc);
            // 注册剪贴板变化监听：复制/剪切时收到 WM_CLIPBOARDUPDATE，及时同步到服务端
            try
            {
                AddClipboardFormatListener(source.Handle);
                _clipboardListenerHwnd = source.Handle;
            }
            catch (Exception ex) { Logger.Warn(ex, "AddClipboardFormatListener failed"); }
        }
    }

    /// <summary>
    /// 原生窗口消息处理。全屏模式下拦截 WM_GETMINMAXINFO：
    /// 把最大窗口尺寸/位置改为当前监视器的完整矩形（rcMonitor），
    /// 这样 Maximized 窗口会覆盖任务栏区域，实现真正的"全屏"。
    /// MINMAXINFO 布局：ptReserved(8B) + ptMaxSize(8B) + ptMaxPosition(8B) + 其余 16B。
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            // 及时通知 ViewModel 检查并同步剪贴板（UI 线程）
            _vm.NotifyLocalClipboardChanged();
            return IntPtr.Zero;
        }
        if (msg == WM_GETMINMAXINFO && _fullscreen)
        {
            IntPtr hMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO();
            mi.cbSize = (uint)Marshal.SizeOf(typeof(MONITORINFO));
            if (GetMonitorInfo(hMonitor, ref mi))
            {
                int width = mi.rcMonitorRight - mi.rcMonitorLeft;
                int height = mi.rcMonitorBottom - mi.rcMonitorTop;
                // ptMaxSize（offset 8）：最大化窗口尺寸
                Marshal.WriteInt32(lParam, 8, width);
                Marshal.WriteInt32(lParam, 12, height);
                // ptMaxPosition（offset 16）：最大化窗口左上角
                Marshal.WriteInt32(lParam, 16, mi.rcMonitorLeft);
                Marshal.WriteInt32(lParam, 20, mi.rcMonitorTop);
                handled = true;
                return IntPtr.Zero;
            }

            // 兜底：GetMonitorInfo 失败时用虚拟屏幕（跨显示器总范围）
            Marshal.WriteInt32(lParam, 8, (int)SystemParameters.VirtualScreenWidth);
            Marshal.WriteInt32(lParam, 12, (int)SystemParameters.VirtualScreenHeight);
            Marshal.WriteInt32(lParam, 16, (int)SystemParameters.VirtualScreenLeft);
            Marshal.WriteInt32(lParam, 20, (int)SystemParameters.VirtualScreenTop);
            handled = true;
            return IntPtr.Zero;
        }
        return IntPtr.Zero;
    }

    /// <summary>ViewModel 属性变化 → 同步仅 View 层拥有的 UI 元素（确保在 UI 线程执行）。</summary>
    private void OnViewModelPropertyChanged(string propertyName)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnViewModelPropertyChanged(propertyName)));
            return;
        }

        // 选中不同配置时，把 ViewModel 的密码同步回 PasswordBox（Password 不支持绑定）
        if (propertyName == nameof(MainWindowViewModel.Password)
            && PasswordBox.Password != (_vm.Password ?? string.Empty))
        {
            PasswordBox.Password = _vm.Password ?? string.Empty;
            return;
        }
        // 断开连接 → 隐藏远程光标、恢复本地系统光标
        if (propertyName == nameof(MainWindowViewModel.IsConnected) && !_vm.IsConnected)
        {
            HideRemoteCursor();
            // 断连后清除本地位置标记：重连时先以服务端回显位置兜底，
            // 直到用户下一次移动鼠标重新锚定本地位置
            _hasLocalCursorPos = false;
        }
            if (propertyName == nameof(MainWindowViewModel.IsConnected))
            {
                var okBrush = TryFindResource("StatusOkBrush") as Brush;
                var idleBrush = TryFindResource("StatusIdleBrush") as Brush;
                StatusDot.Foreground = _vm.IsConnected
                    ? (okBrush ?? idleBrush ?? StatusDot.Foreground)
                    : (idleBrush ?? okBrush ?? StatusDot.Foreground);
            }
    }

    /// <summary>
    /// 远程光标更新（可能来自接收线程）：合成光标位图并定位到显示区。
    /// 光标数据为 Windows AND/XOR 掩码格式：ImageData = [AND 掩码(1bpp, 行对齐 2B)] + [XOR 掩码(BGRA32)]。
    /// </summary>
    private void OnRemoteCursorChanged(CursorInfo cursor)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action<CursorInfo>(OnRemoteCursorChanged), cursor);
            return;
        }

        try
        {
            if (!_vm.IsConnected)
            {
                HideRemoteCursor();
                return;
            }

            _remoteCursorVisible = cursor.Visible;
            _cursorHotX = cursor.HotX;
            _cursorHotY = cursor.HotY;

            // 形状数据变化（含首次连接）→ 重建光标位图
            if (cursor.RgbaPixels != null && cursor.Width > 0 && cursor.Height > 0)
            {
                byte[] bgra = ComposeCursorBgra(cursor);
                // 空形状（全部透明，如捕获端数据异常）时保留上一帧位图，
                // 避免光标在编辑等场景下凭空消失；仅记录诊断日志。
                if (bgra != null && HasOpaquePixels(bgra))
                {
                    if (_cursorBitmap == null
                        || _cursorBitmap.PixelWidth != cursor.Width
                        || _cursorBitmap.PixelHeight != cursor.Height)
                    {
                        _cursorBitmap = new WriteableBitmap(
                            cursor.Width, cursor.Height, 96, 96, PixelFormats.Bgra32, null);
                    }
                    _cursorBitmap.WritePixels(
                        new Int32Rect(0, 0, cursor.Width, cursor.Height),
                        bgra, cursor.Width * 4, 0);
                    RemoteCursorImage.Source = _cursorBitmap;
                }
                else
                {
                    Logger.Warn("Remote cursor shape empty, keeping previous bitmap (size={0}x{1})",
                        cursor.Width, cursor.Height);
                }
            }

            if (!_remoteCursorVisible)
            {
                HideRemoteCursor();
                return;
            }
            // 无可见的远程光标位图时回退到本地光标，避免"鼠标看不见"：
            // 服务端光标被应用隐藏/捕获失败时会发送空形状（Width=0）且恒为 Visible=true，
            // 此时远程叠加层无可渲染内容，隐藏本地光标会导致用户完全看不到鼠标。
            if (_cursorBitmap == null)
            {
                HideRemoteCursor();
                return;
            }

            _lastRemoteCursorX = cursor.X;
            _lastRemoteCursorY = cursor.Y;
            RemoteCursorImage.Visibility = Visibility.Visible;
            // 隐藏本地箭头光标，只显示远程光标形状
            RenderImage.Cursor = Cursors.None;
            // 位置策略（关键，曾导致"非全屏水平偏移 150~200px"视觉错位）：
            // - _hasLocalCursorPos=true（用户移动过鼠标）：锚定本地鼠标位置（零延迟）
            //   不重新调用 PositionRemoteCursorAtLocal —— 旧代码在每次回显到达时
            //   用陈旧的 _localCursorX 重定位叠加层，造成"叠加层跳到旧位置"的闪烁
            // - _hasLocalCursorPos=false（刚连接、鼠标未移动）：用服务端回显位置兜底
            // 形状更新（cursor.RgbaPixels）始终处理，与位置无关
            if (!_hasLocalCursorPos)
                UpdateCursorPosition(cursor.X, cursor.Y);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Remote cursor update failed");
        }
    }

    /// <summary>
    /// 把远程光标叠加层定位到本地鼠标位置：指针显示与手的移动同帧完成。
    /// 服务端坐标映射已保证本地位置 == 远程操作落点，因此锚定不会造成位置偏差。
    /// </summary>
    /// <remarks>
    /// 坐标系说明（关键，曾导致非全屏水平偏移 / 全屏垂直偏移）：
    /// - RenderImage 默认 HorizontalAlignment=Stretch，ActualWidth=Grid 宽度，
    ///   元素左上角 = Grid 左上角（无外偏移）
    /// - Stretch=Uniform 在元素内部居中绘制视频画面，留 letterbox 黑边在元素内
    /// - _localCursorX = e.GetPosition(RenderImage)，相对元素左上角（含黑边区域）
    /// - 叠加层 Margin 相对 Grid = 相对元素左上角
    /// - 但叠加层应显示在视频画面上的鼠标位置，需要加上 rect.X/Y（letterbox 偏移）
    ///   使叠加层从视频画面左上角开始计算位置
    /// </remarks>
    private void PositionRemoteCursorAtLocal()
    {
        if (!_remoteCursorVisible || RemoteCursorImage.Visibility != Visibility.Visible)
            return;
        // 防御：握手前 RemoteScreenWidth/Height 可能为 0（GetRenderImageRect 也会兜底）
        if (_vm.RemoteScreenWidth <= 0 || _vm.RemoteScreenHeight <= 0)
            return;
        Rect rect = GetRenderImageRect();
        if (rect.IsEmpty) return;
        // 热区偏移按视频缩放比例换算（与 UpdateCursorPosition 一致）
        double scaleX = rect.Width / _vm.RemoteScreenWidth;
        double scaleY = rect.Height / _vm.RemoteScreenHeight;
        ResizeRemoteCursor(scaleX, scaleY);
        // 关键：_localCursorX 是相对 RenderImage 元素左上角的坐标（含黑边区域），
        // 但叠加层应定位到视频画面上的鼠标位置。
        // rect.X/Y 是视频画面在元素内的 letterbox 偏移，
        // 叠加层位置 = rect.X + (_localCursorX - rect.X) = _localCursorX（等等，这似乎不需要加 rect.X？）
        //
        // 实际分析：_localCursorX 已经是元素坐标系下的绝对位置（包括黑边区域），
        // 叠加层 Margin 也是相对元素左上角，所以直接用 _localCursorX 是对的。
        // 但为了与 UpdateCursorPosition 保持一致（后者用 rect.X + remoteX * scaleX），
        // 并且确保 ClickMarker 也用相同坐标系，这里保持直接用 _localCursorX。
        RemoteCursorImage.Margin = new Thickness(
            _localCursorX - _cursorHotX * scaleX,
            _localCursorY - _cursorHotY * scaleY,
            0, 0);
    }

    /// <summary>把远程光标坐标（含热区偏移）映射到显示区实际渲染矩形。</summary>
    private void UpdateCursorPosition(int remoteX, int remoteY)
    {
        Rect rect = GetRenderImageRect();
        if (rect.IsEmpty) return;
        double scaleX = rect.Width / _vm.RemoteScreenWidth;
        double scaleY = rect.Height / _vm.RemoteScreenHeight;
        ResizeRemoteCursor(scaleX, scaleY);
        RemoteCursorImage.Margin = new Thickness(
            rect.X + remoteX * scaleX - _cursorHotX * scaleX,
            rect.Y + remoteY * scaleY - _cursorHotY * scaleY,
            0, 0);
    }

    /// <summary>
    /// 按视频缩放比例调整光标叠加层尺寸：光标形状与画面内容等比例缩放，
    /// 避免客户端窗口与远程分辨率不一致时光标形状偏大/偏小造成错位感。
    /// </summary>
    private void ResizeRemoteCursor(double scaleX, double scaleY)
    {
        if (_cursorBitmap == null) return;
        RemoteCursorImage.Width = _cursorBitmap.PixelWidth * scaleX;
        RemoteCursorImage.Height = _cursorBitmap.PixelHeight * scaleY;
        RemoteCursorImage.Stretch = Stretch.Fill;
    }

    /// <summary>计算 RenderImage 在 Uniform 拉伸下实际渲染的矩形（处理黑边 letterbox）。</summary>
    private Rect GetRenderImageRect()
    {
        double iw = RenderImage.ActualWidth;
        double ih = RenderImage.ActualHeight;
        int sw = _vm.RemoteScreenWidth;
        int sh = _vm.RemoteScreenHeight;
        if (iw <= 0 || ih <= 0 || sw <= 0 || sh <= 0)
            return Rect.Empty;
        double scale = Math.Min(iw / sw, ih / sh);
        double w = sw * scale;
        double h = sh * scale;
        return new Rect((iw - w) / 2, (ih - h) / 2, w, h);
    }

    /// <summary>隐藏远程光标叠加层并恢复本地系统光标。</summary>
    private void HideRemoteCursor()
    {
        _remoteCursorVisible = false;
        RemoteCursorImage.Visibility = Visibility.Collapsed;
        RenderImage.Cursor = null;
    }

    /// <summary>
    /// 把光标掩码（AND 1bpp + XOR BGRA32）合成为 BGRA32 像素。
    /// AND=1 → 透明覆盖；AND=0 → 直接使用 XOR 像素（alpha 即透明度）。
    /// </summary>
    private static byte[] ComposeCursorBgra(CursorInfo cursor)
    {
        int w = cursor.Width;
        int h = cursor.Height;
        byte[] src = cursor.RgbaPixels;
        if (w <= 0 || h <= 0 || src == null) return null;

        int andStride = ((w + 15) / 16) * 2; // 1bpp 行对齐到 2 字节
        int xorStride = w * 4;
        int expected = andStride * h + xorStride * h;
        if (src.Length < expected) return null;

        byte[] dst = new byte[w * h * 4];
        int xorBase = andStride * h;
        for (int row = 0; row < h; row++)
        {
            for (int col = 0; col < w; col++)
            {
                int andByteIdx = row * andStride + (col >> 3);
                int andBit = 7 - (col & 7);
                bool andSet = ((src[andByteIdx] >> andBit) & 1) != 0;

                int si = xorBase + row * xorStride + col * 4;
                byte b = src[si];
                byte g = src[si + 1];
                byte r = src[si + 2];
                byte a = src[si + 3];
                if (andSet)
                {
                    b = g = r = a = 0; // 挖空区域全透明
                }
                // AND=0 时直接使用 XOR 像素的 BGRA/alpha：
                // 单色光标已由 EasyDesk 捕获端转换为真实 alpha（透明=0，黑白=255），
                // 彩色光标 alpha 即透明度。旧规则把 alpha=0 强制为不透明黑，
                // 会把现代 alpha 光标的透明区域涂黑，或掩盖空形状数据。

                int di = (row * w + col) * 4;
                dst[di] = b;
                dst[di + 1] = g;
                dst[di + 2] = r;
                dst[di + 3] = a;
            }
        }
        return dst;
    }

    /// <summary>判断合成后的 BGRA 位图是否含有非透明像素（用于空形状兜底）。</summary>
    private static bool HasOpaquePixels(byte[] bgra)
    {
        if (bgra == null) return false;
        for (int i = 3; i < bgra.Length; i += 4)
        {
            if (bgra[i] != 0) return true;
        }
        return false;
    }

    /// <summary>PasswordBox 密码变化 → 同步到 ViewModel（UI 不直接绑定敏感属性）。</summary>
    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        _vm.Password = PasswordBox.Password;
    }

    /// <summary>窗口关闭前停止 aly 自动更新后台循环，避免阻塞线程残留。</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_clickMarkerTimer != null)
        {
            try { _clickMarkerTimer.Stop(); } catch { }
            _clickMarkerTimer = null;
        }
        if (_clipboardListenerHwnd != IntPtr.Zero)
        {
            try { RemoveClipboardFormatListener(_clipboardListenerHwnd); }
            catch (Exception ex) { Logger.Warn(ex, "RemoveClipboardFormatListener failed"); }
            _clipboardListenerHwnd = IntPtr.Zero;
        }
        try
        {
            _vm.CleanupUpdateClient();
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Cleanup aly update client failed");
        }
        base.OnClosing(e);
    }

    /// <summary>将鼠标事件路由到 ViewModel。</summary>
    private void RenderImage_MouseMove(object sender, MouseEventArgs e)
    {
        RefreshLocalCursorPosition(e);
    }

    /// <summary>
    /// 将鼠标按下事件路由到 ViewModel（Preview 隧道阶段捕获，保证右键等所有按键
    /// 在元素级处理/ContextMenu 服务介入之前就被转发，右键不再被吞）。
    /// 按下时捕获鼠标，确保拖出窗口也能收到 MouseUp，避免远端按键卡在"按下"状态。
    /// </summary>
    private void RenderImage_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed
            || e.RightButton == MouseButtonState.Pressed
            || e.MiddleButton == MouseButtonState.Pressed
            || e.XButton1 == MouseButtonState.Pressed
            || e.XButton2 == MouseButtonState.Pressed)
        {
            Mouse.Capture((IInputElement)sender);
        }
        // 按下瞬间刷新本地位置并发送最新坐标：快速移动时点击落点精确到按下瞬间，
        // 不再依赖按下前最后一次 MouseMove 的旧坐标
        RefreshLocalCursorPosition(e);
        ShowClickMarker(_localCursorX, _localCursorY);
        // 诊断：点击瞬间的本地坐标与最近回显坐标（差值即"可见光标 vs 实际落点"偏差）
        Logger.Debug("Click down local=({0:F0},{1:F0}) size={2:F0}x{3:F0} lastEcho=({4},{5})",
            _localCursorX, _localCursorY,
            RenderImage.ActualWidth, RenderImage.ActualHeight,
            _lastRemoteCursorX, _lastRemoteCursorY);
        _vm.HandleMouseDown(e.ChangedButton);
    }

    /// <summary>将鼠标释放事件路由到 ViewModel，并释放鼠标捕获。</summary>
    private void RenderImage_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        RefreshLocalCursorPosition(e);
        _vm.HandleMouseUp(e.ChangedButton);
        Mouse.Capture(null);
    }

    /// <summary>
    /// 在按下位置显示红点标记 1.5 秒（诊断用）：用户点击后可直接对照红点
    /// 与远端操作生效位置，量化残余水平偏移。
    /// </summary>
    private void ShowClickMarker(double x, double y)
    {
        if (_clickMarkerTimer == null)
        {
            _clickMarkerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _clickMarkerTimer.Tick += (s, e) =>
            {
                _clickMarkerTimer.Stop();
                ClickMarker.Visibility = Visibility.Collapsed;
            };
        }
        ClickMarker.Margin = new Thickness(x - 5, y - 5, 0, 0);
        ClickMarker.Visibility = Visibility.Visible;
        _clickMarkerTimer.Stop();
        _clickMarkerTimer.Start();
    }

    /// <summary>
    /// 用当前鼠标事件位置刷新本地光标锚点并同步给服务端。
    /// 光标叠加层始终锚定本地位置（零延迟），服务端回显坐标只在尚未跟踪到
    /// 本地鼠标时兜底，绝不用于交互中的光标显示，避免"光标可见位置"与
    /// "点击落点"相差 150~200px（回显滞后造成的视觉错位）。
    /// </summary>
    private void RefreshLocalCursorPosition(MouseEventArgs e)
    {
        var pos = e.GetPosition(RenderImage);
        _hasLocalCursorPos = true;
        _localCursorX = pos.X;
        _localCursorY = pos.Y;
        // 尺寸诊断日志（每 50 次）：Border/Image/bitmap/DPI 四组尺寸对照。
        // 若 Image 未铺满 Border（宽或高小于 Border）则存在黑框区，
        // 用户在该区域操作会产生越界坐标 → 钳制偏移。
        if ((++_sizeDiagCounter % 50) == 0)
        {
            var bmp = _vm.RenderBitmap;
            Logger.Debug("SizeDiag: border={0:F0}x{1:F0} image={2:F0}x{3:F0} bitmap={4}x{5} dpi={6}",
                RenderBorder.ActualWidth, RenderBorder.ActualHeight,
                RenderImage.ActualWidth, RenderImage.ActualHeight,
                bmp != null ? bmp.PixelWidth : 0, bmp != null ? bmp.PixelHeight : 0,
                System.Windows.Media.VisualTreeHelper.GetDpi(this));
        }
        _vm.HandleMouseMove(pos.X, pos.Y, RenderImage.ActualWidth, RenderImage.ActualHeight);
        PositionRemoteCursorAtLocal();
    }

    /// <summary>将滚轮事件路由到 ViewModel。</summary>
    private void RenderImage_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _vm.HandleMouseWheel(e.Delta);
    }

    /// <summary>键盘按下 — 路由到 ViewModel。</summary>
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        // F11 切换全屏（业界惯例，类似浏览器全屏快捷键）
        if (e.Key == Key.F11)
        {
            _vm.ToggleFullscreen();
            e.Handled = true;
            return;
        }

        // Esc 退出全屏（仅在已全屏时生效；非全屏时 Esc 不拦截，正常转发输入）
        if (e.Key == Key.Escape)
        {
            var window = Application.Current?.MainWindow as MainWindow;
            if (window != null && window.IsFullscreenMode)
            {
                _vm.ToggleFullscreen();
                e.Handled = true;
                return;
            }
        }

        // 焦点在 TextBox/PasswordBox 上时不拦截（让用户正常输入 IP、端口和密码）
        if (System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.TextBox
            || System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.PasswordBox)
            return;

        // WPF 中 Alt 键通过 SystemKey 传递
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        _vm.HandleKeyDown(key);
        e.Handled = true;
    }

    /// <summary>键盘释放 — 路由到 ViewModel。</summary>
    private void Window_KeyUp(object sender, KeyEventArgs e)
    {
        // 焦点在 TextBox/PasswordBox 上时不拦截
        if (System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.TextBox
            || System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.PasswordBox)
            return;

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        _vm.HandleKeyUp(key);
        e.Handled = true;
    }

    /// <summary>
    /// 切换全屏 UI：全屏时隐藏顶部配置区和底部状态栏，让桌面显示区填满整个窗口。
    /// 由 MainWindowViewModel.ToggleFullscreen 调用。
    /// </summary>
    /// <param name="fullscreen">true=进入全屏，false=退出全屏。</param>
    public void SetFullscreenUI(bool fullscreen)
    {
        TopBar.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
        ActionBar.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
        BottomBar.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
    }
}
