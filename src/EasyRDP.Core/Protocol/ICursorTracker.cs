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

        /// <summary>停止所有客户端的光标追踪并结束线程。仅 TransportHost 在停机时调用。</summary>
        void StopAll();
    }
}
