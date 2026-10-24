using System;
using System.Windows;
using System.Windows.Threading;
using EasyRDP.Client.Wpf.ViewModels;
using NLog;

namespace EasyRDP.Client.Wpf.Views
{
    /// <summary>
    /// 粘贴前的文件准备进度窗（模态）。
    /// 显示期间内部消息泵保持运行，后台下载线程继续拉取文件；
    /// 下载结束（或超时）后自动关闭，调用方随即把真实 CF_HDROP 交给系统。
    /// 说明：本窗口在 WM_RENDERFORMAT 处理中同步弹出，不能阻塞系统剪贴板会话。
    /// </summary>
    public partial class FilePasteProgressWindow : Window
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        // 兜底超时：下载异常时也不让模态窗口永久停留
        private const int MaxWaitMs = 65000;

        private readonly Func<bool> _isDone;
        private readonly DispatcherTimer _timer;
        private readonly DateTime _startedUtc = DateTime.UtcNow;

        /// <summary>创建进度窗；<paramref name="isDone"/> 返回 true 时自动关闭。</summary>
        public FilePasteProgressWindow(MainWindowViewModel viewModel, Func<bool> isDone)
        {
            InitializeComponent();
            DataContext = viewModel;
            _isDone = isDone;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _timer.Tick += (s, e) =>
            {
                try
                {
                    if (_isDone != null && _isDone())
                    {
                        _timer.Stop();
                        Close();
                        return;
                    }
                    if ((DateTime.UtcNow - _startedUtc).TotalMilliseconds > MaxWaitMs)
                    {
                        Logger.Warn("File paste progress window timed out after {0}ms", MaxWaitMs);
                        _timer.Stop();
                        Close();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "File paste progress tick failed");
                }
            };
            _timer.Start();
        }

        /// <summary>关闭窗口时停止轮询定时器。</summary>
        protected override void OnClosed(EventArgs e)
        {
            try { _timer.Stop(); } catch { }
            base.OnClosed(e);
        }
    }
}
