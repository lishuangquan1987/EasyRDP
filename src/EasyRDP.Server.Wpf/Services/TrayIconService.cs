#nullable disable
using System;
using System.Drawing;
using System.Windows.Forms;
using NLog;

namespace EasyRDP.Server.Wpf.Services
{
    /// <summary>
    /// 服务端托盘图标（WinForms NotifyIcon，net40/XP 可用）。
    /// 关闭主窗口时隐藏到托盘继续监听，托盘菜单可恢复窗口、启停监听、退出进程。
    /// </summary>
    public class TrayIconService : IDisposable
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private readonly NotifyIcon _notifyIcon;
        private readonly ToolStripMenuItem _toggleItem;
        private readonly Action _onShow;
        private readonly Action _onToggleListening;
        private readonly Action _onExit;
        private bool _disposed;

        /// <summary>
        /// 创建托盘图标。
        /// </summary>
        /// <param name="onShow">显示主窗口（双击图标 / 菜单 Open）。</param>
        /// <param name="onToggleListening">开始或停止监听（菜单 Start/Stop listening）。</param>
        /// <param name="onExit">退出进程（菜单 Exit）。</param>
        public TrayIconService(Action onShow, Action onToggleListening, Action onExit)
        {
            _onShow = onShow;
            _onToggleListening = onToggleListening;
            _onExit = onExit;

            var menu = new ContextMenuStrip();
            var openItem = new ToolStripMenuItem("Open EasyRDP Server");
            openItem.Font = new Font(openItem.Font, FontStyle.Bold);
            openItem.Click += (s, e) => Invoke(_onShow, "Open");

            _toggleItem = new ToolStripMenuItem("Start listening");
            _toggleItem.Click += (s, e) => Invoke(_onToggleListening, "ToggleListening");

            var exitItem = new ToolStripMenuItem("Exit");
            exitItem.Click += (s, e) => Invoke(_onExit, "Exit");

            menu.Items.Add(openItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_toggleItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);

            _notifyIcon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "EasyRDP Server",
                ContextMenuStrip = menu,
                Visible = true
            };
            _notifyIcon.DoubleClick += (s, e) => Invoke(_onShow, "Show");
            Logger.Info("Tray icon created");
        }

        /// <summary>刷新菜单文案（是否正在监听）。</summary>
        public void UpdateListeningState(bool isRunning)
        {
            try
            {
                _toggleItem.Text = isRunning ? "Stop listening" : "Start listening";
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Update tray menu failed");
            }
        }

        /// <summary>显示气泡提示（窗口隐藏/审批请求/首次最小化提示）。</summary>
        public void ShowBalloon(string title, string text)
        {
            try
            {
                _notifyIcon.BalloonTipTitle = title ?? "EasyRDP Server";
                _notifyIcon.BalloonTipText = text ?? "";
                _notifyIcon.ShowBalloonTip(4000);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Show tray balloon failed");
            }
        }

        private static void Invoke(Action action, string name)
        {
            if (action == null) return;
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Tray action failed: {0}", name);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Dispose tray icon failed");
            }
            Logger.Info("Tray icon disposed");
        }
    }
}
