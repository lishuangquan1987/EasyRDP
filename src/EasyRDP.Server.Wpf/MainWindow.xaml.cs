using System.ComponentModel;
using System.Windows;
using System.Windows.Forms;
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
}
