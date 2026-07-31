#nullable disable
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace EasyRDP.Client.Wpf;

/// <summary>
/// 客户端主窗口（View 层）。仅负责初始化 ViewModel 和路由鼠标事件。
/// 所有业务逻辑在 MainWindowViewModel 中。
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _vm;
    // 全屏状态：由 SetFullscreenMode 维护，WndProc 据此决定 WM_GETMINMAXINFO 返回值。
    // 全屏 = WindowStyle.None + Maximized + 最大化尺寸覆盖整个监视器（含任务栏区域），
    // 且绝不使用 Topmost（置顶窗口会锁死用户切换前台窗口，断连后尤其危险）。
    private bool _fullscreen;

    private const int WM_GETMINMAXINFO = 0x0024;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

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
    }

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
            source.AddHook(WndProc);
    }

    /// <summary>
    /// 原生窗口消息处理。全屏模式下拦截 WM_GETMINMAXINFO：
    /// 把最大窗口尺寸/位置改为当前监视器的完整矩形（rcMonitor），
    /// 这样 Maximized 窗口会覆盖任务栏区域，实现真正的"全屏"。
    /// MINMAXINFO 布局：ptReserved(8B) + ptMaxSize(8B) + ptMaxPosition(8B) + 其余 16B。
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
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
            if (propertyName == nameof(MainWindowViewModel.IsConnected))
            {
                var okBrush = TryFindResource("StatusOkBrush") as Brush;
                var idleBrush = TryFindResource("StatusIdleBrush") as Brush;
                StatusDot.Foreground = _vm.IsConnected
                    ? (okBrush ?? idleBrush ?? StatusDot.Foreground)
                    : (idleBrush ?? okBrush ?? StatusDot.Foreground);
            }
    }

    /// <summary>PasswordBox 密码变化 → 同步到 ViewModel（UI 不直接绑定敏感属性）。</summary>
    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        _vm.Password = PasswordBox.Password;
    }

    /// <summary>将鼠标事件路由到 ViewModel。</summary>
    private void RenderImage_MouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(RenderImage);
        _vm.HandleMouseMove(pos.X, pos.Y, RenderImage.ActualWidth, RenderImage.ActualHeight);
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
        _vm.HandleMouseDown(e.ChangedButton);
    }

    /// <summary>将鼠标释放事件路由到 ViewModel，并释放鼠标捕获。</summary>
    private void RenderImage_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _vm.HandleMouseUp(e.ChangedButton);
        Mouse.Capture(null);
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
