#nullable disable
using System;
using System.Windows;
using EasyRDP.Server.Wpf.Services;
using EasyRDP.Server.Wpf.ViewModels;

namespace EasyRDP.Server.Wpf.Views
{
    /// <summary>
    /// 连接审批窗口（免密码模式）：显示客户端信息与倒计时，Allow/Deny 返回结果。
    /// </summary>
    public partial class ConnectionApprovalWindow : Window
    {
        private readonly ConnectionApprovalViewModel _vm;

        public ConnectionApprovalWindow(ConnectionApprovalRequest request)
        {
            InitializeComponent();
            _vm = new ConnectionApprovalViewModel(request);
            _vm.CloseRequested += OnCloseRequested;
            DataContext = _vm;
        }

        private void OnCloseRequested(bool allow)
        {
            // 倒计时可能在工作线程/计时器上触发 Decide，统一回到 UI 线程关闭
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action<bool>(OnCloseRequested), allow);
                return;
            }
            try
            {
                DialogResult = allow;
            }
            catch
            {
                // 窗口已被用户关闭（IsCancel）时设置 DialogResult 会抛异常，忽略即可
            }
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            _vm.CloseRequested -= OnCloseRequested;
            _vm.StopTimer();
            base.OnClosed(e);
        }
    }
}
