using System;
using System.Threading;
using EasyDesk.Core;
using EasyDesk.Windows;
using EasyRDP.Core.Protocol;

namespace EasyRDP.Server.Wpf.Services
{
    /// <summary>
    /// 服务端剪贴板同步服务。
    /// 监控本地剪贴板变更并广播给所有客户端，接收远程剪贴板更新本地。
    /// </summary>
    public class ClipboardSyncService
    {
        private IClipboardService _clipboard;
        private string _lastSentText;
        private DateTime _cooldownUntil;
        private volatile bool _running;
        private Thread _thread;

        /// <summary>发送回调。参数: (data) — 广播给所有客户端。</summary>
        public Action<byte[]> BroadcastToAll { get; set; }

        /// <summary>日志回调。</summary>
        public Action<string> OnLog { get; set; }

        public ClipboardSyncService()
        {
            // 在 STA 线程初始化剪贴板
            var initThread = new Thread(() =>
            {
                var factory = new WindowsDesktopFactory();
                _clipboard = factory.CreateClipboardService();
            });
            initThread.SetApartmentState(ApartmentState.STA);
            initThread.Start();
            initThread.Join();
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(MonitorLoop);
            _thread.IsBackground = true;
            _thread.Name = "EasyRDP-WPF-Clipboard";
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            if (_thread != null && _thread.IsAlive)
                _thread.Join(1000);
        }

        /// <summary>处理收到的远程剪贴板。</summary>
        public void OnRemoteClipboard(ClipboardDataMessage msg)
        {
            if (msg == null || msg.Format != ClipboardFormat.UnicodeText)
                return;

            _lastSentText = msg.Text;
            _cooldownUntil = DateTime.Now.AddMilliseconds(500);

            var t = new Thread(() =>
            {
                try { _clipboard.SetText(msg.Text); }
                catch { }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join(2000);
        }

        private void MonitorLoop()
        {
            while (_running)
            {
                try
                {
                    // 静默期检查
                    if (DateTime.Now < _cooldownUntil)
                    {
                        Thread.Sleep(300);
                        continue;
                    }

                    string text = null;
                    var t = new Thread(() =>
                    {
                        try { text = _clipboard.GetText(); }
                        catch { }
                    });
                    t.SetApartmentState(ApartmentState.STA);
                    t.Start();
                    t.Join(1000);

                    if (!string.IsNullOrEmpty(text) && text != _lastSentText)
                    {
                        _lastSentText = text;
                        var clipMsg = new ClipboardDataMessage
                        {
                            Format = ClipboardFormat.UnicodeText,
                            Text = text
                        };
                        byte[] data = MessageCodec.Encode(MessageType.ClipboardData, 0, clipMsg);
                        var broadcast = BroadcastToAll;
                        if (broadcast != null) broadcast(data);
                    }
                }
                catch { }
                try { Thread.Sleep(300); } catch { break; }
            }
        }
    }
}
