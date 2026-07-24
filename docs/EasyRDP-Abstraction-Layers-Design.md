# EasyRDP 五层抽象 + 编排层下沉设计

> 版本：1.0
> 状态：已确认，待实现
> 最后更新：2026-07-23
> 关联文档：`EasyRDP-Codec-Plan-B.md`（B1–B4 编码后端）、`EasyRDP-Protocol-v1.md`（协议规范）

---

## 1. 背景与目标

### 1.1 问题

当前 EasyRDP 已存在三个独立的底层抽象：捕获（`IScreenCapturer`，EasyDesk 子模块）、编码（`IVideoEncoder`/`IFrameEncoder`）、传输（`ITransportClient`/`ITransportServer`）。
但将三者串联的编排逻辑全部焊死在 UI 项目里（`EasyRDP.Server.Wpf/Services/CaptureEngine.cs` 的 `CaptureLoop`、`EasyRDP.Client.Common/ConnectionManager.cs`），导致：

1. 未来的 Avalonia 服务端/客户端无法复用同一条 pipeline，必须重写
2. 编排逻辑不通过接口暴露，码率自适应（ABR）、帧调度等横切机制无处挂载
3. 客户端渲染逻辑层（`FrameBuffer`）与平台层（`WpfRenderEngine`）耦合在 `Client.Common` 中，Avalonia 客户端无法复用

### 1.2 目标

参照 ScottPlot 的分层思路（逻辑层不依赖 UI，平台层只负责把数据推到屏幕），把 EasyRDP 的数据通路抽象为**五层零件 + 一层编排**：

```
编排层 (Session)
   │
   ▼
① 截屏 → ② 编码 → ③ 传输 → ④ 解码 → ⑤ 渲染
```

- 五层零件全部以接口暴露，具体实现可插拔
- 编排层只依赖接口，不依赖具体实现，可被 WPF / Avalonia 共享
- net40 (XP) / net8.0 双目标兼容（C# 5.0 约束在 net40 路径）
- **放弃图片传输兼容**：协议成为纯视频流协议，不再保留 Bitmap 兜底路径

### 1.3 非目标

- 不在本设计内实现客户端编排层（`IClientStreamSession`/`IClientInputSession` 只定义接口，不实现）
- 不引入新的视频编码后端（VP8/VP9 等留待后续）
- 不引入码率自适应实现（`IRateController` 留作未来抽象，本次不落地）

---

## 2. 关键决策

| # | 决策 | 说明 |
|---|---|---|
| D1 | **放弃图片传输兼容** | 去除 `CodecId.Bitmap`、`IFrameEncoder`、`BitmapEncoder`、`ScreenFrameMessage`、`DirtyRectDetector` 调用路径。协议成为纯视频流协议，仅保留 `VideoFrameMessage`。**net40/XP 路径不再支持服务端**（H264 编码仅 net8.0 可用，且不再有 Bitmap 兜底）；net40/XP 仍可作客户端（解码侧可后续补 x264 软解，或限制为 net8.0 客户端连接 net8.0 服务端） |
| D2 | **光标独立传输** | 光标不混入视频帧像素，作为独立消息流（`CursorUpdate` + `CursorTracker`）。`FrameBuffer` 不感知光标，光标视觉叠加由平台渲染后端负责 |
| D3 | **`FrameBuffer` 下沉到 Core** | 从 `EasyRDP.Client.Common` 移到 `EasyRDP.Core/Rendering/`，命名空间 `EasyRDP.Core.Rendering`。代价：服务端引用到客户端专属类（但不使用）。收益：避免新建 `EasyRDP.Client.Core` 项目，工程结构最简，Avalonia 客户端直接复用 |
| D4 | **立即补 `IVideoDecoder` 抽象** | 镜像 `IVideoEncoder`，`H264Decoder` 改为实现该接口。新增 `DecoderFactory` 镜像 `EncoderFactory`。为未来 VP8/VP9 解码后端铺路 |
| D5 | **`IRenderTarget` 包含 `Resize`** | 平台渲染后端接口包含完整生命周期：渲染帧、更新光标、尺寸变更 |
| D6 | **客户端编排先不实现** | `IClientStreamSession`/`IClientInputSession` 只定义接口，留待后续实现。客户端现状（`ConnectionManager` + `MessageDispatcher`）暂不动 |
| D7 | **编排层方案 B** | 服务端编排按流的方向拆成 `IServerStreamSession`（视频流）+ `IServerInputSession`（输入）两个对称接口，光标归入 StreamSession |

---

## 3. 五层抽象总览

### 3.1 数据流

**服务端**（编排 = `IServerStreamSession` 实现）：
```
IScreenCapturer → IVideoEncoder → ITransportServer
                  ↑ 光标走 CursorTracker 独立线程 → ITransportServer
```

**客户端**（编排 = `IClientStreamSession` 实现，本次仅定义接口）：
```
ITransportClient → IVideoDecoder → FrameBuffer → IRenderTarget
                                   ↑ 光标消息 → IRenderTarget.UpdateCursor
```

### 3.2 目录结构（目标态）

```
EasyRDP.Core/
├── Protocol/                      [已有，需清理]
│   ├── IVideoEncoder.cs           ✓ 已有
│   ├── IVideoDecoder.cs           ★ 新增（D4）
│   ├── H264Encoder.cs             ✓ 已有（net8.0，`#if` 隔离）
│   ├── H264Decoder.cs             ◆ 改为实现 IVideoDecoder
│   ├── EncoderFactory.cs          ◆ 移除 Bitmap 分支
│   ├── DecoderFactory.cs          ★ 新增（D4）
│   ├── CodecNegotiator.cs         ◆ 移除 Bitmap 兜底
│   ├── CodecCapabilities.cs       ◆ 移除 Bitmap 位
│   ├── CodecId.cs                 ◆ 移除 Bitmap 枚举值
│   ├── CursorTracker.cs           ✓ 已有（光标独立流，D2）
│   ├── VideoFrameMessage.cs       ✓ 已有（纯视频流协议，D1）
│   ├── ─────────────────────────  以下为 D1 删除项
│   ├── IFrameEncoder.cs           ✗ 删除
│   ├── BitmapEncoder.cs           ✗ 删除
│   ├── Messages/ScreenFrame.cs    ✗ 删除
│   ├── DirtyRectDetector.cs       ✗ 删除（服务端不再做帧间 diff）
│   └── CompressHelper.cs          ✗ 删除（仅服务 Bitmap 路径用）
│
├── Transport/                     [已有，不动]
│   ├── ITransportClient.cs        ✓
│   ├── ITransportServer.cs        ✓
│   └── TCP/UDP 实现               ✓
│
├── Rendering/                     ★ 新目录（D3）
│   ├── FrameBuffer.cs             ◆ 从 Client.Common 下沉，移除 Bitmap/脏矩形合并逻辑（D1）
│   └── IRenderTarget.cs           ★ 新增（D5）
│
└── Session/                       ★ 新目录（编排层，D7）
    ├── IServerStreamSession.cs    ★ 新增
    ├── IServerInputSession.cs     ★ 新增
    ├── IClientStreamSession.cs    ★ 新增（D6，仅定义）
    └── IClientInputSession.cs     ★ 新增（D6，仅定义）

EasyDesk/                          [子模块]
└── IScreenCapturer                ✓ 已有（捕获抽象，第①层）

平台层（以 WPF 为例）:
EasyRDP.Client.Wpf/Services/
├── WpfRenderTarget.cs             ◆ 现有 WpfRenderEngine 改为实现 IRenderTarget
└── ...

EasyRDP.Server.Wpf/Services/
├── ServerStreamSession.cs         ◆ 现有 CaptureEngine 拆分 + 实现 IServerStreamSession
├── ServerInputSession.cs          ◆ 从 CaptureEngine.HandleInput 抽出
└── TransportHost.cs               ◆ 现有 ServerEngine 正名（仅传输薄封装）
```

图例：✓ 已有 ★ 新增 ◆ 改造 ✗ 删除

---

## 4. 各层接口设计

> 所有接口遵循现有约定：`using` 在 `namespace` 内、XML doc 注释、net40 路径 C# 5.0（无 `async/await`/`$""`/`?.`/`nameof`）、net8.0 专有代码用 `#if NET8_0_OR_GREATER` 隔离。

### 4.1 第①层：捕获抽象 `IScreenCapturer`

已存在于 EasyDesk 子模块，本次设计不改。仅记录依赖关系：

```csharp
// 位置：EasyDesk/src/EasyDesk.Core/IScreenCapturer.cs（已有）
namespace EasyDesk.Core
{
    public interface IScreenCapturer
    {
        ScreenFrame CaptureScreen();
        DesktopBounds GetPrimaryScreen();
    }
}
```

- `ScreenFrame.Scan0` 为非托管 BGRA32 像素，调用方负责 `Marshal.FreeHGlobal`
- 实现：`GdiScreenCapturer`（net40，XP 兼容）、`DxgiScreenCapturer`（net8.0，未来）

### 4.2 第②层：编码抽象 `IVideoEncoder`

已存在，本次设计不改，仅记录：

```csharp
// 位置：EasyRDP.Core/Protocol/IVideoEncoder.cs（已有）
namespace EasyRDP.Core.Protocol
{
    public interface IVideoEncoder
    {
        CodecId Codec { get; }
        bool IsAvailable { get; }
        void Initialize(int width, int height, int targetBitrate = 2000000);
        VideoFrameMessage Encode(byte[] pixels, bool forceKeyframe);
        void Reset();
        void Dispose();
    }
}
```

- 实现：`H264Encoder`（net8.0，openh264 P/Invoke）
- 工厂：`EncoderFactory.CreateVideo(codec)`，移除 `CreateFrame` 分支（D1）
- 未来扩展：VP8Encoder、H264HardwareEncoder

### 4.3 第③层：传输抽象 `ITransportClient` / `ITransportServer`

已存在，本次设计不改：

```csharp
// 位置：EasyRDP.Core/Transport/ITransportClient.cs（已有）
public interface ITransportClient : IDisposable
{
    bool Connect(string host, int port, int timeoutMs);
    void Disconnect();
    bool Send(byte[] data);
    bool IsConnected { get; }
    event EventHandler<MessageReceivedEventArgs> MessageReceived;
    event EventHandler Disconnected;
    LogCallback OnLog { get; set; }
}

// 位置：EasyRDP.Core/Transport/ITransportServer.cs（已有）
public interface ITransportServer : IDisposable
{
    void Start(int port);
    void Stop();
    void SendTo(uint sessionId, byte[] data);
    void Disconnect(uint sessionId);
    event EventHandler<ConnectionEventArgs> ClientConnected;
    event EventHandler<ConnectionEventArgs> ClientDisconnected;
    event EventHandler<MessageReceivedEventArgs> MessageReceived;
    LogCallback OnLog { get; set; }
}
```

- 实现：TCP 双工、UDP（`PacketFramer` 处理分帧）

### 4.4 第④层：解码抽象 `IVideoDecoder`（D4，新增）

镜像 `IVideoEncoder`，接口本身 net40 可见，实现用 `#if` 隔离：

```csharp
// 位置：EasyRDP.Core/Protocol/IVideoDecoder.cs（★ 新增）
namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 视频解码器抽象。镜像 <see cref="IVideoEncoder"/>。
    /// 接口本身在 net40 / net8.0 均可见；具体解码后端用条件编译隔离。
    /// </summary>
    public interface IVideoDecoder
    {
        /// <summary>解码后端标识。</summary>
        CodecId Codec { get; }

        /// <summary>当前平台是否可用（如 native 库加载失败返回 false）。</summary>
        bool IsAvailable { get; }

        /// <summary>初始化解码器。</summary>
        /// <param name="width">帧宽度</param>
        /// <param name="height">帧高度</param>
        void Initialize(int width, int height);

        /// <summary>
        /// 解码一帧。返回 BGRA32 像素；返回 null 表示本帧无输出（如 B 帧前向参考未就绪）。
        /// </summary>
        byte[] Decode(byte[] data);

        /// <summary>重置解码器内部状态（如发生丢包后）。</summary>
        void Reset();

        /// <summary>释放 native 资源。</summary>
        void Dispose();
    }
}
```

- 实现：`H264Decoder`（net8.0，现有 [H264Decoder.cs](../src/EasyRDP.Core/Protocol/H264Decoder.cs) 改为 `: IVideoDecoder`）
- 工厂：`DecoderFactory.Create(codec)`，形状镜像 `EncoderFactory`，含 `#if NET8_0_OR_GREATER` 隔离 + `IsAvailable` 探测

```csharp
// 位置：EasyRDP.Core/Protocol/DecoderFactory.cs（★ 新增）
public static class DecoderFactory
{
    public static IVideoDecoder Create(CodecId codec)
    {
        switch (codec)
        {
#if NET8_0_OR_GREATER
            case CodecId.H264Software:
                H264Decoder d = new H264Decoder();
                return d.IsAvailable ? d : null;
            case CodecId.H264Hardware:
                // B-4 阶段实现
                return null;
#endif
            default:
                return null;
        }
    }

    public static CodecId GetAvailableCodec(CodecId preferred)
    {
        var d = Create(preferred);
        if (d != null)
        {
            // 探测后释放
            d.Dispose();
            return preferred;
        }
        // 无兜底——握手失败由调用方处理
        return (CodecId)(-1);
    }
}
```

### 4.5 第⑤层：渲染抽象

分两部分：**渲染逻辑层**（`FrameBuffer`，不依赖 UI）+ **平台适配接口**（`IRenderTarget`，依赖具体 UI 框架）。

#### 4.5.1 渲染逻辑层 `FrameBuffer`（D3，下沉 + 清理）

从 `EasyRDP.Client.Common/FrameBuffer.cs` 下沉到 `EasyRDP.Core/Rendering/FrameBuffer.cs`。
**清理（D1）**：移除与 Bitmap 路径相关的方法——`ProcessFrame(ScreenFrameMessage)`、`CopyRegion`（CopyRect 仅服务 Bitmap 路径）、纯色块检测分支。
**保留**：`ProcessFullFrame(width, height, pixels)` 作为唯一入口（视频解码后调用）、`TryGetFrame`、`Reset`。

```csharp
// 位置：EasyRDP.Core/Rendering/FrameBuffer.cs（◆ 下沉 + 清理）
namespace EasyRDP.Core.Rendering
{
    /// <summary>
    /// 客户端本地帧缓冲（渲染逻辑层，不依赖任何 UI）。
    /// 维护 BGRA32 像素缓冲，提供全帧替换与脏区累积消费。
    /// 线程安全：读写通过 lock 保护。
    /// </summary>
    /// <remarks>
    /// 设计参照 ScottPlot.Plot：逻辑层不感知 UI，平台层通过 <see cref="IRenderTarget"/> 推屏。
    /// 视频流为被动更新流（网络源源推送），比 ScottPlot 主动绘制多一层"脏区累积合并"职责。
    /// </remarks>
    public class FrameBuffer
    {
        // 状态字段：_buffer / _width / _height / _isDirty / _pendingDirty（保留）

        /// <summary>处理一帧 BGRA32 全帧像素（视频解码后调用）。</summary>
        public void ProcessFullFrame(int width, int height, byte[] pixels) { /* 保留现有实现 */ }

        /// <summary>
        /// 尝试获取最新帧的像素副本及自上次消费后累积的脏区。
        /// 消费后 IsDirty=false，脏区清空。
        /// </summary>
        public bool TryGetFrame(out byte[] pixels, out int w, out int h, out ScreenRect[] dirtyRects) { /* 保留 */ }

        public int Width { get; }
        public int Height { get; }
        public bool IsDirty { get; }
        public int FrameCount { get; }

        /// <summary>重置（断连时调用）。</summary>
        public void Reset() { /* 保留 */ }
    }
}
```

#### 4.5.2 平台适配接口 `IRenderTarget`（D5，新增）

平台渲染后端接口。包含完整生命周期：渲染帧、更新光标、尺寸变更（D2 + D5）。

```csharp
// 位置：EasyRDP.Core/Rendering/IRenderTarget.cs（★ 新增）
namespace EasyRDP.Core.Rendering
{
    /// <summary>
    /// 平台渲染后端接口。参照 ScottPlot.IPlotControl：
    /// 逻辑层（FrameBuffer）不依赖 UI，平台层通过本接口把像素推到屏幕。
    /// 光标视觉叠加由平台层负责（D2）——不同平台用不同混合方式
    /// （WPF: WriteableBitmap alpha 混合；Avalonia: SKBitmap；D3D: 纹理）。
    /// </summary>
    public interface IRenderTarget
    {
        /// <summary>
        /// 渲染一帧。复用底层位图/纹理，仅尺寸变化时重建。
        /// 当 dirtyRects 非空时按脏区逐块刷新，否则全屏刷新。
        /// </summary>
        /// <param name="bgraPixels">BGRA32 像素</param>
        /// <param name="w">帧宽度</param>
        /// <param name="h">帧高度</param>
        /// <param name="dirtyRects">自上次渲染后变化的区域；null 或空表示全屏</param>
        void RenderFrame(byte[] bgraPixels, int w, int h, ScreenRect[] dirtyRects);

        /// <summary>
        /// 更新光标状态（D2）。光标不混入帧像素，由平台层独立叠加。
        /// </summary>
        /// <param name="visible">是否可见</param>
        /// <param name="x">屏幕 X 坐标</param>
        /// <param name="y">屏幕 Y 坐标</param>
        /// <param name="rgbaPixels">RGBA 像素（null 表示形状未变，仅更新位置）</param>
        /// <param name="cursorW">光标宽度</param>
        /// <param name="cursorH">光标高度</param>
        /// <param name="hotX">热区 X</param>
        /// <param name="hotY">热区 Y</param>
        void UpdateCursor(bool visible, int x, int y,
            byte[] rgbaPixels, int cursorW, int cursorH, int hotX, int hotY);

        /// <summary>
        /// 预分配指定尺寸（连接成功时调用，D5）。
        /// </summary>
        void Resize(int w, int h);
    }
}
```

- 实现：`WpfRenderTarget`（现有 [WpfRenderEngine.cs](../src/EasyRDP.Client.Wpf/Services/WpfRenderEngine.cs) 改名 + `: IRenderTarget`，方法 `Render`→`RenderFrame`、`SetCursor`→`UpdateCursor`、保留 `Resize`）
- 未来：`AvaloniaRenderTarget`、`D3D11RenderTarget`

---

## 5. 编排层设计（方案 B）

### 5.1 设计原则

- 按流的方向拆分：视频流（生产-消费者 + 独立线程）、输入（事件驱动同步调用）并发模型完全不同，不混在一个类
- 客户端对称：`IServerStreamSession` ↔ `IClientStreamSession`，`IServerInputSession` ↔ `IClientInputSession`
- 光标归入 StreamSession：本质是第二条视频流（形状+位置），[CursorTracker](../src/EasyRDP.Core/Protocol/CursorTracker.cs) 已独立 60Hz 线程
- Session 不绑死传输实现：通过 `Action<uint, byte[]> sendTo` 回调解耦（服务端）/ `ITransportClient` 注入（客户端）

### 5.2 服务端接口（本次实现）

```csharp
// 位置：EasyRDP.Core/Session/IServerStreamSession.cs（★ 新增）
namespace EasyRDP.Core.Session
{
    /// <summary>
    /// 服务端视频流会话：管理单客户端的"截屏→编码→发送"循环与光标追踪。
    /// 每个客户端连接对应一个实例。
    /// </summary>
    public interface IServerStreamSession : IDisposable
    {
        /// <summary>编码后端（握手协商结果）。</summary>
        CodecId Codec { get; set; }

        /// <summary>目标帧间隔（毫秒）。</summary>
        int FrameDelayMs { get; set; }

        /// <summary>
        /// 启动视频流。
        /// </summary>
        /// <param name="sessionId">客户端会话 ID</param>
        /// <param name="sendTo">发送回调（桥接 ITransportServer.SendTo，解耦传输实现）</param>
        void Start(uint sessionId, Action<uint, byte[]> sendTo);

        /// <summary>停止视频流并释放编码器。</summary>
        void Stop();

        /// <summary>
        /// 光标追踪器（独立 60Hz 线程，与帧循环解耦）。
        /// 启动视频流时一并启动。
        /// </summary>
        CursorTracker CursorTracker { get; }
    }

    // 位置：EasyRDP.Core/Session/IServerInputSession.cs（★ 新增）
    /// <summary>
    /// 服务端输入会话：接收客户端输入消息并模拟本地桌面操作。
    /// 事件驱动同步调用，无独立线程。
    /// </summary>
    public interface IServerInputSession : IDisposable
    {
        /// <summary>处理一条输入消息（鼠标/键盘）。调用方为消息分发线程。</summary>
        void HandleInput(InputEventMessage msg);
    }
}
```

**实现**（`EasyRDP.Server.Wpf/Services/`）：
- `ServerStreamSession` ← 从 [CaptureEngine.cs](../src/EasyRDP.Server.Wpf/Services/CaptureEngine.cs) 拆出 `CaptureLoop` + 双缓冲 + 自适应帧率 + 发送队列 + `CursorTracker`
- `ServerInputSession` ← 从 `CaptureEngine.HandleInput` 抽出
- `TransportHost` ← 现 [ServerEngine.cs](../src/EasyRDP.Server.Wpf/Services/ServerEngine.cs) 正名（仅 `TcpTransportServer` 薄封装，避免与编排层混淆）
- `CaptureEngine` 废弃，逻辑全部迁移到上述三个类

### 5.3 客户端接口（本次仅定义，D6）

```csharp
// 位置：EasyRDP.Core/Session/IClientStreamSession.cs（★ 新增，仅定义）
namespace EasyRDP.Core.Session
{
    /// <summary>
    /// 客户端视频流会话：管理"接收→解码→FrameBuffer→触发渲染"循环。
    /// 本次仅定义接口，不实现。客户端现状（ConnectionManager + MessageDispatcher）暂不动。
    /// </summary>
    public interface IClientStreamSession : IDisposable
    {
        /// <summary>协商后的解码后端。</summary>
        CodecId Codec { get; }

        /// <summary>本地帧缓冲（渲染逻辑层）。</summary>
        FrameBuffer FrameBuffer { get; }

        /// <summary>平台渲染后端。</summary>
        IRenderTarget RenderTarget { get; set; }

        /// <summary>启动接收循环。transport 由调用方注入。</summary>
        void Start(ITransportClient transport);

        /// <summary>停止并释放解码器。</summary>
        void Stop();
    }

    // 位置：EasyRDP.Core/Session/IClientInputSession.cs（★ 新增，仅定义）
    /// <summary>
    /// 客户端输入会话：捕获本地输入事件并发送到服务端。
    /// 本次仅定义接口，不实现。
    /// </summary>
    public interface IClientInputSession : IDisposable
    {
        /// <summary>启动本地输入捕获。</summary>
        /// <param name="transport">传输层（用于发送输入消息）</param>
        /// <param name="screenWidth">远程屏幕宽度（坐标映射用）</param>
        /// <param name="screenHeight">远程屏幕高度</param>
        void Start(ITransportClient transport, int screenWidth, int screenHeight);

        /// <summary>停止捕获。</summary>
        void Stop();
    }
}
```

---

## 6. D1（放弃图片传输）连带改动清单

放弃 Bitmap 兼容是本次设计的最大破坏性变更，连带改动如下：

### 6.1 删除文件

| 文件 | 理由 |
|---|---|
| [IFrameEncoder.cs](../src/EasyRDP.Core/Protocol/IFrameEncoder.cs) | Bitmap 编码器接口，纯视频流协议不需要 |
| [BitmapEncoder.cs](../src/EasyRDP.Core/Protocol/BitmapEncoder.cs) | Bitmap 编码器实现 |
| [Messages/ScreenFrame.cs](../src/EasyRDP.Core/Protocol/Messages/ScreenFrame.cs) | `ScreenFrameMessage` + `ScreenRect`，仅服务 Bitmap 路径（注意：`ScreenRect` 被 `FrameBuffer._pendingDirty` 复用，需保留 `ScreenRect` 类本身或迁移到 `Rendering/`）|
| [DirtyRectDetector.cs](../src/EasyRDP.Core/Protocol/DirtyRectDetector.cs) | 帧间 diff，服务端不再做（视频编码器内部已做帧间预测） |
| [CompressHelper.cs](../src/EasyRDP.Core/Protocol/CompressHelper.cs) | Zlib/JPEG 压缩，仅服务 Bitmap 路径 |
| [CopyRectMessage.cs](../src/EasyRDP.Core/Protocol/CopyRectMessage.cs) | CopyRect 仅服务 Bitmap 路径 |

### 6.2 修改文件

| 文件 | 改动 |
|---|---|
| [CodecId.cs](../src/EasyRDP.Core/Protocol/CodecId.cs) | 移除 `Bitmap = 0` 枚举值。`H264Software`/`H264Hardware` 值不变（向后兼容握手字节） |
| [CodecCapabilities.cs](../src/EasyRDP.Core/Protocol/CodecCapabilities.cs) | 移除 `Bitmap` 位，`All` 改为 `H264Software \| H264Hardware` |
| [CodecNegotiator.cs](../src/EasyRDP.Core/Protocol/CodecNegotiator.cs) | 移除 Bitmap 兜底分支。交集为空时返回失败（不再是 `CodecId.Bitmap`） |
| [EncoderFactory.cs](../src/EasyRDP.Core/Protocol/EncoderFactory.cs) | 移除 `CreateFrame` 方法 + `Bitmap` 分支 |
| [H264Decoder.cs](../src/EasyRDP.Core/Protocol/H264Decoder.cs) | 改为 `: IVideoDecoder`（D4） |
| [FrameBuffer.cs](../src/EasyRDP.Client.Common/FrameBuffer.cs) | 下沉到 Core/Rendering，移除 `ProcessFrame(ScreenFrameMessage)`/`CopyRegion`/纯色块分支，保留 `ProcessFullFrame`/`TryGetFrame`/`Reset` |
| [CaptureEngine.cs](../src/EasyRDP.Server.Wpf/Services/CaptureEngine.cs) | 拆分为 `ServerStreamSession` + `ServerInputSession`，移除 `IFrameEncoder`/`BuildFullFrame`/`BuildDeltaFrame` 分支 |
| [MessageDispatcher](../src/EasyRDP.Client.Common/MessageDispatcher.cs) 注册项 | 移除 `ScreenFrameMessage` 处理器注册（仅保留 `VideoFrameMessage`） |
| [ConnectionManager](../src/EasyRDP.Client.Common/ConnectionManager.cs) 握手 | `Capabilities` 不再声明 `Bitmap` 位 |
| [HandshakeReq.cs](../src/EasyRDP.Core/Protocol/Messages/HandshakeReq.cs) / [HandshakeRes.cs](../src/EasyRDP.Core/Protocol/Messages/HandshakeRes.cs) | 协商失败语义：交集为空 → `HandshakeResult.NoCommonCodec`（不再是自动降级 Bitmap） |

### 6.3 `ScreenRect` 的去留

`ScreenRect` 当前定义在 `Messages/ScreenFrame.cs` 里，但被 `FrameBuffer._pendingDirty` 复用（渲染脏区表达）。
纯视频流协议下，`ScreenRect` 的"协议消息"语义消失，但"渲染脏区"语义仍需要。
**决策**：把 `ScreenRect` 类迁移到 `EasyRDP.Core/Rendering/ScreenRect.cs`，从协议消息降级为渲染层内部数据结构。

### 6.4 `Constants.cs` / `MessageType.cs`

检查并移除 `MessageType.ScreenFrame`（保留 `MessageType.VideoFrame`）、`CompressType` 枚举（若仅服务 Bitmap 路径）。

---

## 7. 实现节奏

| 阶段 | 内容 | 验证 |
|---|---|---|
| P1 | D4: 新增 `IVideoDecoder` + `DecoderFactory`，`H264Decoder` 实现接口 | 编译通过，现有 H264 解码路径不回归 |
| P2 | D1 清理：删除 Bitmap 相关文件，修改 `CodecId`/`CodecCapabilities`/`CodecNegotiator`/`EncoderFactory` | 编译通过 |
| P3 | D3: `FrameBuffer` 下沉到 Core/Rendering，清理 Bitmap 逻辑，`ScreenRect` 迁移 | 编译通过 |
| P4 | D5: 新增 `IRenderTarget`，`WpfRenderEngine` → `WpfRenderTarget : IRenderTarget` | 客户端渲染不回归 |
| P5 | D7: 新增 `Session/` 四接口，`CaptureEngine` 拆分为 `ServerStreamSession` + `ServerInputSession`，`ServerEngine` → `TransportHost` | 服务端启动 + 客户端连接 + 视频流 + 输入不回归 |
| P6 | D6: 新增 `IClientStreamSession`/`IClientInputSession` 接口（仅定义） | 编译通过 |
| P7 | 端到端验证 | WPF 服务端 + WPF 客户端，H264 软编路径全流程跑通 |

每阶段独立可验证，失败可回滚。P5 是风险最高阶段（动编排核心），单独验证。

---

## 8. 风险与缓解

| 风险 | 缓解 |
|---|---|
| D1 删除 Bitmap 路径后，net40/XP **服务端**无可用编码器（H264 仅 net8.0） | 本设计明确：D1 后 net40/XP 不再支持服务端角色。net40/XP 仅保留客户端能力（解码侧后续补 x264 软解，或暂限定 net8.0 客户端连 net8.0 服务端）。`EasyRDP.Server.Wpf` 项目目标框架可考虑从 net40 下沉为仅 net8.0 |
| P5 拆分 `CaptureEngine` 引入回归 | 拆分前先在 git 打 tag，拆分后逐项验证：启动、连接、视频流、输入、断连 |
| `FrameBuffer` 下沉后 `Client.Common` 仍引用 Core（不存在反向依赖） | 下沉是单向的，`Client.Common` 引用 `Core/Rendering` 合法 |
| `IVideoDecoder` 接口在 net40 可见但无实现 | 与 `IVideoEncoder` 现状一致，`#if` 隔离实现，接口本身跨目标可见 |

---

## 9. 与既有文档的关系（本设计落地后需同步更新，不在本次实现范围内）

- **`EasyRDP-Codec-Plan-B.md`**：B1（Bitmap 兜底）被 D1 取消；B2（Codec 协商）保留但移除 Bitmap 位；B3/B4（H264 软/硬编）不变
- **`EasyRDP-Protocol-v1.md`**：`ScreenFrameMessage`/`CopyRectMessage`/`CompressType` 等 Bitmap 路径协议元素将被移除，协议版本需升号（v1.2 → v2.0）
- **`AGENTS.md`**：架构图需更新，反映 Session/Rendering 新目录与编排层

> 以上文档更新属于 P1–P7 实现完成后的收尾工作，本设计文档不涵盖其具体改动内容。

---

## 10. 开放问题（实现阶段决策）

1. `ScreenRect` 迁移到 `Rendering/` 后，命名空间从 `EasyRDP.Core.Protocol` 改为 `EasyRDP.Core.Rendering`，是否需要全局 `using` 别名减少改动？
2. `MessageType.ScreenFrame` 移除后，老客户端连接新服务端如何处理？是否需要协议版本号握手拒绝？（当前协议 `Version=0x01`，可升 `0x02`）
3. `IRenderTarget.Resize` 在连接成功时调用一次，分辨率变更时再调用——分辨率变更检测由谁负责（`FrameBuffer` 还是 `IRenderTarget` 自己）？
