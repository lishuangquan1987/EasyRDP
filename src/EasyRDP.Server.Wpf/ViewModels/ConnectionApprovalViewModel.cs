#nullable disable
using System;
using System.ComponentModel;
using System.Windows.Threading;
using EasyRDP.Server.Wpf.Services;
using EasyRDP.Shared;

namespace EasyRDP.Server.Wpf.ViewModels
{
    /// <summary>
    /// 连接审批对话框 ViewModel。
    /// 展示客户端信息与倒计时；Allow/Deny 通过 CloseRequested 事件通知 View 关闭。
    /// 倒计时结束自动拒绝，避免无人值守时传输线程被长期占用。
    /// </summary>
    public class ConnectionApprovalViewModel : INotifyPropertyChanged
    {
        private readonly DispatcherTimer _timer;
        private readonly int _timeoutSeconds;
        private int _remainingSeconds;

        /// <summary>客户端远端地址。</summary>
        public string RemoteEndPoint { get; private set; }
        /// <summary>客户端自报用户名。</summary>
        public string Username { get; private set; }
        /// <summary>客户端协议版本描述。</summary>
        public string ProtocolVersion { get; private set; }

        private string _countdownText;
        /// <summary>倒计时文本。</summary>
        public string CountdownText
        {
            get { return _countdownText; }
            private set
            {
                _countdownText = value;
                OnPropertyChanged(nameof(CountdownText));
            }
        }

        /// <summary>用户做出选择（true=允许）。View 订阅后关闭对话框。</summary>
        public event Action<bool> CloseRequested;

        /// <summary>允许本次连接。</summary>
        public RelayCommand AllowCommand { get; private set; }
        /// <summary>拒绝本次连接。</summary>
        public RelayCommand DenyCommand { get; private set; }

        public ConnectionApprovalViewModel(ConnectionApprovalRequest request)
        {
            request = request ?? new ConnectionApprovalRequest();
            RemoteEndPoint = string.IsNullOrEmpty(request.RemoteEndPoint) ? "?" : request.RemoteEndPoint;
            Username = string.IsNullOrEmpty(request.Username) ? "(none)" : request.Username;
            ProtocolVersion = "v" + request.ClientVersion;
            _timeoutSeconds = request.TimeoutSeconds > 0 ? request.TimeoutSeconds : 60;
            _remainingSeconds = _timeoutSeconds;

            AllowCommand = new RelayCommand(() => Decide(true));
            DenyCommand = new RelayCommand(() => Decide(false));
            UpdateCountdownText();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += OnTimerTick;
            _timer.Start();
        }

        private void OnTimerTick(object sender, EventArgs e)
        {
            _remainingSeconds--;
            if (_remainingSeconds <= 0)
            {
                Decide(false);
                return;
            }
            UpdateCountdownText();
        }

        private void UpdateCountdownText()
        {
            CountdownText = string.Format("This request will be denied automatically in {0} seconds.", _remainingSeconds);
        }

        private void Decide(bool allow)
        {
            StopTimer();
            var handler = CloseRequested;
            if (handler != null) handler(allow);
        }

        /// <summary>停止倒计时（窗口关闭时调用，避免计时器泄漏）。</summary>
        public void StopTimer()
        {
            if (_timer == null) return;
            _timer.Stop();
            _timer.Tick -= OnTimerTick;
        }

        /// <summary>属性变化通知（仅 CountdownText 使用）。</summary>
        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged(string name)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }
    }
}
