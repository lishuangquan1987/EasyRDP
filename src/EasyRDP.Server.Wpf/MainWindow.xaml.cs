using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Forms;
using AlyClient.CSharpSDK;
using EasyRDP.Server.Wpf.ViewModels;

namespace EasyRDP.Server.Wpf
{
    public partial class MainWindow
    {
        private NotifyIcon _tray;
        private bool _forceExit;
        private MainViewModel _vm;

        public MainWindow()
        {
            InitializeComponent();
            _vm = (MainViewModel)Resources["VM"];
            _tray = new NotifyIcon { Text = "EasyRDP Server", Visible = true, Icon = System.Drawing.SystemIcons.Application };
            _tray.ContextMenuStrip = new ContextMenuStrip();
            _tray.ContextMenuStrip.Items.Add("显示窗口", null, (s, e) => { Show(); WindowState = WindowState.Normal; ShowInTaskbar = true; Activate(); });
            _tray.ContextMenuStrip.Items.Add("退出", null, (s, e) => _vm.ExitCommand.Execute(null));
            _tray.DoubleClick += (s, e) => { Show(); WindowState = WindowState.Normal; ShowInTaskbar = true; Activate(); };
        }

        private void OnWindowStateChanged(object sender, System.EventArgs e)
        {
            if (WindowState == WindowState.Minimized) { Hide(); ShowInTaskbar = false; }
        }

        private void OnWindowClosing(object sender, CancelEventArgs e)
        {
            if (!_forceExit) { e.Cancel = true; WindowState = WindowState.Minimized; return; }
            if (_vm.IsRunning) _vm.StopCommand.Execute(null);
            _tray.Visible = false; _tray.Dispose();
        }
    }

    /// <summary>
    /// 将 AlyClientStatus 转换为 Visibility：仅在 DiscoveredUpdate/DownloadingUpdate/DownloadedUpdate 时可见。
    /// </summary>
    public class AlyStatusToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var status = (AlyClientStatus)value;
            return (status == AlyClientStatus.DiscoveredUpdate ||
                    status == AlyClientStatus.DownloadingUpdate ||
                    status == AlyClientStatus.DownloadedUpdate)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return null;
        }
    }
}
