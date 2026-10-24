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
        // 上次按时间提速的时刻（秒）：速度按"每秒 +10%"增长，避免按块 +64KB/s 时升速过慢
        private double _lastGrowSeconds;
        private long _lastTicks;

        /// <summary>创建调速器。<paramref name="videoBacklogged"/> 返回 true 表示视频流正在等带宽。</summary>
        public AdaptivePacer(Func<bool> videoBacklogged)
        {
            _videoBacklogged = videoBacklogged;
            _lastTicks = Stopwatch.GetTimestamp();
            _lastGrowSeconds = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
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
                    // 乘性降速：链路已饱和，快速让路；重置升速计时，避免刚降速就立刻回升
                    _rate = Math.Max(MinBytesPerSec, _rate * 0.6);
                    _lastGrowSeconds = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                }
                else if (elapsedMs < 5)
                {
                    // 加性提速（按时间）：每 100ms 提升当前速率的 10%，约 1 秒翻倍。
                    // 旧的按块 +64KB/s 在 4MB/s 起步时要 ~4 秒才翻倍，且 ACK 节奏会掩盖升速空间。
                    double nowSeconds = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                    double dt = nowSeconds - _lastGrowSeconds;
                    if (dt >= 0.1)
                    {
                        _rate = Math.Min(MaxBytesPerSec, _rate * (1.0 + 0.1 * dt));
                        _lastGrowSeconds = nowSeconds;
                    }
                }
            }
        }
    }
}
