using System;
using System.Threading;
using EasyDesk.Core;
using EasyDesk.Windows;
using EasyRDP.Core.Protocol;

namespace EasyRDP.Server.Wpf.Services
{
    /// <summary>
    /// 服务端剪贴板同步服务。单个 STA 线程运行全部剪贴板操作。
    /// </summary>
    public class ClipboardSyncService
    {
        private IClipboardService _clipboard;
        private string _lastSentText;
        private DateTime _cooldownUntil;
        private volatile bool _running;
        private Thread _thread;

        public Action<byte[]> BroadcastToAll { get; set; }
        public Action<string> OnLog { get; set; }

        public ClipboardSyncService()
        {
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
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.IsBackground = true;
            _thread.Name = "EasyRDP-Clipboard";
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            if (_thread != null && _thread.IsAlive) _thread.Join(1000);
        }

        public void OnRemoteClipboard(ClipboardDataMessage msg)
        {
            if (msg == null || msg.Format != ClipboardFormat.UnicodeText) return;
            _lastSentText = msg.Text;
            _cooldownUntil = DateTime.Now.AddMilliseconds(500);
            // STA 线程在 MonitorLoop 中直接操作剪贴板，这里只更新状态
        }

        private void MonitorLoop()
        {
            while (_running)
            {
                try
                {
                    if (DateTime.Now < _cooldownUntil)
                    {
                        // 处理远程剪贴板写入（OnRemoteClipboard 设置了 _lastSentText，这里写入本地）
                        if (_lastSentText != null)
                        {
                            try { _clipboard.SetText(_lastSentText); } catch { }
                            _lastSentText = null;
                        }
                        Thread.Sleep(300);
                        continue;
                    }

                    // 读取本地剪贴板并比较
                    string text = null;
                    try { text = _clipboard.GetText(); } catch { }

                    if (!string.IsNullOrEmpty(text) && text != _lastSentText)
                    {
                        _lastSentText = text;
                        var clipMsg = new ClipboardDataMessage { Format = ClipboardFormat.UnicodeText, Text = text };
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
