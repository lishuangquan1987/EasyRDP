namespace EasyRDP.Core.Transport
{
    using System;
    using System.Diagnostics;
    using System.Threading;

    /// <summary>
    /// 文件通道自适应用令牌桶。目标：不抢占远程桌面的带宽。
    /// 速率区间 256KB/s ~ 32MB/s，无固定上限：
    /// - 视频发送队列有积压（远程画面在等带宽）→ 立即降速让路；
    /// - 单次 Send 阻塞时间过长（链路拥塞）→ 乘性降速；
    /// - 链路通畅（Send 很快返回）→ 加性提速。
    /// 令牌上限为 250ms 的突发量，避免长期空闲后一次性灌满接收端。
    /// </summary>
    public class AdaptivePacer
    {
        /// <summary>最低发送速率（字节/秒）。</summary>
        public const double MinBytesPerSec = 256 * 1024;

        /// <summary>最高发送速率（字节/秒）。</summary>
        public const double MaxBytesPerSec = 32.0 * 1024 * 1024;

        /// <summary>起始发送速率（字节/秒）。</summary>
        public const double StartBytesPerSec = 4.0 * 1024 * 1024;

        /// <summary>Send 阻塞超过该毫秒数即视为链路拥塞。</summary>
        private const long CongestedSendMs = 30;

        // 令牌桶状态（Acquire 可能被多个传输线程调用，全部加锁保护）
        private readonly object _lock = new object();
        private readonly Func<bool> _videoBacklogged;
        private double _rate = StartBytesPerSec;
        private double _tokens;
        private long _lastTicks;

        /// <summary>创建调速器。<paramref name="videoBacklogged"/> 返回 true 表示视频流正在等带宽。</summary>
        public AdaptivePacer(Func<bool> videoBacklogged)
        {
            _videoBacklogged = videoBacklogged;
            _lastTicks = Stopwatch.GetTimestamp();
        }

        /// <summary>当前发送速率（字节/秒），用于日志与 UI 展示。</summary>
        public double RateBytesPerSec
        {
            get { lock (_lock) return _rate; }
        }

        /// <summary>
        /// 申请发送配额（阻塞至获得许可）。调用方拿到配额后应立即发送该块数据。
        /// </summary>
        public void Acquire(int bytes)
        {
            if (bytes <= 0) return;
            while (true)
            {
                int waitMs;
                lock (_lock)
                {
                    long now = Stopwatch.GetTimestamp();
                    double elapsed = (now - _lastTicks) / (double)Stopwatch.Frequency;
                    _lastTicks = now;
                    _tokens = Math.Min(_tokens + elapsed * _rate, _rate * 0.25);

                    // 视频积压：说明远程画面正在抢带宽，清零令牌并降速让路
                    if (_videoBacklogged != null && _videoBacklogged())
                    {
                        _rate = Math.Max(MinBytesPerSec, _rate * 0.6);
                        _tokens = 0;
                    }

                    if (_tokens >= bytes)
                    {
                        _tokens -= bytes;
                        return;
                    }
                    double needSeconds = (bytes - _tokens) / _rate;
                    waitMs = (int)Math.Max(1, Math.Min(200, needSeconds * 1000));
                }
                Thread.Sleep(waitMs);
            }
        }

        /// <summary>上报发送该块实际阻塞的耗时（毫秒），用于 AIMD 调整速率。</summary>
        public void ReportSendTime(long elapsedMs)
        {
            lock (_lock)
            {
                if (elapsedMs > CongestedSendMs)
                {
                    // 乘性降速：链路已饱和，快速让路
                    _rate = Math.Max(MinBytesPerSec, _rate * 0.6);
                }
                else if (elapsedMs < 5)
                {
                    // 加性提速：每块 +64KB/s（256KB 块下约等于每秒 +1MB/s 上限）
                    _rate = Math.Min(MaxBytesPerSec, _rate + 64 * 1024);
                }
            }
        }
    }
}
