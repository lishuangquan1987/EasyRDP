#nullable disable
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace EasyRDP.Client.Wpf;

/// <summary>
/// 客户端主窗口（View 层）。仅负责初始化 ViewModel 和路由鼠标事件。
/// 所有业务逻辑在 MainWindowViewModel 中。
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainWindowViewModel();
        DataContext = _vm;
        // PasswordBox 不支持绑定 Password（安全设计），初始值在 XAML 构造后同步一次，
        // 后续变化由 PasswordChanged 事件同步到 ViewModel。
        PasswordBox.Password = _vm.Password ?? string.Empty;

        // 连接状态指示灯：连接成功后状态点变绿
        _vm.PropertyChanged += (s, e) => OnViewModelPropertyChanged(e.PropertyName);
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
            if (window != null && window.WindowStyle == WindowStyle.None)
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
