using System;
using System.Threading;
using EasyDesk.Core;
using EasyDesk.Windows;
using EasyRDP.Core.Logging;
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
            LogHelper.Info("剪贴板同步已启动");
        }

        public void Stop()
        {
            _running = false;
            if (_thread != null && _thread.IsAlive) _thread.Join(1000);
            LogHelper.Info("剪贴板同步已停止");
        }

        public void OnRemoteClipboard(ClipboardDataMessage msg)
        {
            if (msg == null || msg.Format != ClipboardFormat.UnicodeText) return;
            _lastSentText = msg.Text;
            _cooldownUntil = DateTime.Now.AddMilliseconds(500);
        }

        private void MonitorLoop()
        {
            while (_running)
            {
                try
                {
                    if (DateTime.Now < _cooldownUntil)
                    {
                        if (_lastSentText != null)
                        {
                            try { _clipboard.SetText(_lastSentText); }
                            catch (Exception ex) { LogHelper.Warn(string.Format("剪贴板写入失败: {0}", ex.Message)); }
                            _lastSentText = null;
                        }
                        Thread.Sleep(300);
                        continue;
                    }

                    string text = null;
                    try { text = _clipboard.GetText(); }
                    catch (Exception ex) { LogHelper.Warn(string.Format("剪贴板读取失败: {0}", ex.Message)); }

                    if (!string.IsNullOrEmpty(text) && text != _lastSentText)
                    {
                        _lastSentText = text;
                        var clipMsg = new ClipboardDataMessage { Format = ClipboardFormat.UnicodeText, Text = text };
                        byte[] data = MessageCodec.Encode(MessageType.ClipboardData, 0, clipMsg);
                        var broadcast = BroadcastToAll;
                        if (broadcast != null) broadcast(data);
                    }
                }
                catch (Exception ex)
                {
                    LogHelper.Warn(string.Format("剪贴板同步异常: {0}", ex.Message));
                }
                try { Thread.Sleep(300); } catch { break; }
            }
        }
    }
}
