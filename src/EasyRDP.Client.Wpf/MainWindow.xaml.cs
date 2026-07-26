using System.Windows;
using System.Windows.Input;

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
    }

    /// <summary>将鼠标事件路由到 ViewModel。</summary>
    private void RenderImage_MouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(RenderImage);
        _vm.HandleMouseMove(pos.X, pos.Y, RenderImage.ActualWidth, RenderImage.ActualHeight);
    }

    /// <summary>将鼠标按下事件路由到 ViewModel。</summary>
    private void RenderImage_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _vm.HandleMouseDown(e.ChangedButton);
    }

    /// <summary>将鼠标释放事件路由到 ViewModel。</summary>
    private void RenderImage_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _vm.HandleMouseUp(e.ChangedButton);
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

        // 焦点在 TextBox 上时不拦截（让用户正常输入 IP 和端口）
        if (System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.TextBox)
            return;

        // WPF 中 Alt 键通过 SystemKey 传递
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        _vm.HandleKeyDown(key);
        e.Handled = true;
    }

    /// <summary>键盘释放 — 路由到 ViewModel。</summary>
    private void Window_KeyUp(object sender, KeyEventArgs e)
    {
        // 焦点在 TextBox 上时不拦截
        if (System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.TextBox)
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
        BottomBar.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
    }
}
