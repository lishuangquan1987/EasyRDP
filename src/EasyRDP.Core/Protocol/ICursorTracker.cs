namespace EasyRDP.Core.Protocol
{
    using System;

    /// <summary>
    /// 光标追踪全局抽象。全局单例（与 ICaptureService 同生命周期，由 TransportHost 持有），
    /// 拥有独立线程检测光标位置与形状变化。多客户端共用一个线程。
    /// 接口拆分：本接口仅含全局生命周期管理，由 TransportHost 调用；
    /// 会话级控制通过 ICursorTrackerSession 暴露给单个 Session。
    /// </summary>
    public interface ICursorTracker : IDisposable
    {
        /// <summary>光标检测间隔（毫秒），默认 16（≈60Hz）。</summary>
        int IntervalMs { get; set; }

        /// <summary>是否捕获光标形状（RGBA 像素）。false 时仅追踪位置。</summary>
        bool EnableShape { get; set; }

        /// <summary>
        /// 为本连接创建一个光标追踪会话。由 ServerStreamSession.Start 调用。
        /// 返回的 ICursorTrackerSession 仅控制本会话的光标订阅，不影响其他客户端。
        /// </summary>
        ICursorTrackerSession CreateSession();

        /// <summary>启动光标轮询线程。</summary>
        void Start();

        /// <summary>移除已停止的光标追踪会话并释放其资源。由 ServerStreamSession.Stop 调用。</summary>
        void RemoveSession(ICursorTrackerSession session);

        /// <summary>停止所有客户端的光标追踪并结束线程。仅 TransportHost 在停机时调用。</summary>
        void StopAll();
    }
}
