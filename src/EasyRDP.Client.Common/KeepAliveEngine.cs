using System;
using System.Threading;

namespace EasyRDP.Client.Common
{
    /// <summary>
    /// 客户端心跳引擎。
    /// 定时发送 KeepAlive 并检测 Ack 超时。
    /// </summary>
    public class KeepAliveEngine
    {
        private readonly int _intervalMs;
        private readonly int _timeoutMs;
        private DateTime _lastAckTime;
        private volatile bool _running;
        private Thread _thread;
        private CancellationTokenSource _cts;

        /// <summary>心跳超时时触发。</summary>
        public event Action Timeout;

        /// <summary>
        /// 创建心跳引擎。
        /// </summary>
        /// <param name="intervalMs">发送间隔（毫秒）。默认 5000。</param>
        /// <param name="timeoutMs">超时时间（毫秒）。默认 15000。</param>
        public KeepAliveEngine(int intervalMs = 5000, int timeoutMs = 15000)
        {
            _intervalMs = intervalMs;
            _timeoutMs = timeoutMs;
        }

        /// <summary>
        /// 启动心跳线程。
        /// </summary>
        /// <param name="sendAction">发送 KeepAlive 的动作（返回是否发送成功）</param>
        public void Start(Func<bool> sendAction)
        {
            if (_running)
                return;

            _lastAckTime = DateTime.Now;
            _running = true;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            _thread = new Thread(() => Loop(sendAction, token));
            _thread.IsBackground = true;
            _thread.Name = "EasyRDP-KeepAlive";
            _thread.Start();
        }

        /// <summary>
        /// 停止心跳线程。
        /// </summary>
        public void Stop()
        {
            _running = false;

            if (_cts != null)
            {
                _cts.Cancel();
                _cts.Dispose();
                _cts = null;
            }

            if (_thread != null && _thread.IsAlive)
            {
                _thread.Join(1000);
                _thread = null;
            }
        }

        /// <summary>
        /// 收到 KeepAliveAck 时调用，更新最近响应时间。
        /// </summary>
        public void OnAckReceived()
        {
            _lastAckTime = DateTime.Now;
        }

        /// <summary>
        /// 是否已超时。
        /// </summary>
        public bool IsTimeout
        {
            get
            {
                return (DateTime.Now - _lastAckTime).TotalMilliseconds > _timeoutMs;
            }
        }

        private void Loop(Func<bool> sendAction, CancellationToken ct)
        {
            while (_running && !ct.IsCancellationRequested)
            {
                // 发送心跳
                try
                {
                    sendAction();
                }
                catch
                {
                    // 发送失败不计
                }

                // 等待间隔（分段等待以响应取消）
                int waited = 0;
                while (waited < _intervalMs && _running && !ct.IsCancellationRequested)
                {
                    Thread.Sleep(100);
                    waited += 100;
                }

                // 检查超时
                if (_running && IsTimeout)
                {
                    _running = false;
                    var handler = Timeout;
                    if (handler != null)
                        handler();
                    break;
                }
            }
        }
    }
}
