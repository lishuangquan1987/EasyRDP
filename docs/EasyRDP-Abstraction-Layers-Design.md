# EasyRDP 五层抽象 + 编排层设计规范

> 版本：2.7
> 状态：设计终版，待实现（分层职责闭合修订版）
> 最后更新：2026-07-24
> 修订：
> - v1.2 → v2.0 文档自包含化——不再引用现有代码，所有接口完整定义，目录结构仅展示目标态
> - v2.0 → v2.1 可行性审核修订：补 XP 用例（D10/D11）、net40 H.264 原生后端保证、协议 Framing 与消息结构、修正 A1 线程模型、补 C1–C6 并发与生命周期漏洞
> - v2.1 → v2.2 接口设计审核修订：H1–H5 接口原则；M1–M5 封装/对称/契约；L1–L8 构造注入/省拷贝/结构体等
> - v2.2 → v2.3 详细设计完备性修订：A1 章节重排；B1/B2/D1 一致性与对称；C1–C4 补未定义类型；D2–D7 完善；E1–E4 补全
> - v2.3 → v2.4 运行时健壮性 + 协议完备性修订：D12 并发上限+全局负载感知；Stop 防竞态；Keepalive 心跳；CapturedFrame 双缓冲；发送公平性；V1 主屏+GetAllScreens 预留；FrameBuffer 读帧超时；6.8 字节级布局表
> - v2.4 → v2.5 传输健壮性 + 认证完善修订：分包机制；校验码策略；认证改用户名+密码
> - v2.5 → v2.6 传输无关协议层重构：纠正 v2.5 按传输方式分支的做法。改为协议层统一定义帧分片+顺序保证+丢帧策略，传输层只"尽力投递分片字节"
> - v2.6 → v2.7 分层职责闭合修订：新增 MessageReassembler（4.3.1）闭合传输层分片→Session 完整消息的桥接缺口；传输层事件改名 DataReceived（去"消息"歧义）；FrameId/SequenceNumber 语义澄清；修复 CommitFrame 双 summary / 目录树双 └── / 非视频消息丢帧策略说明

---

## 1. 背景与目标

### 1.1 问题

EasyRDP 需要三套底层抽象协作完成远程桌面：捕获、编码、传输。当前三者通过接口暴露，但串联编排逻辑与 UI 项目紧耦合，导致：

1. 不同 UI 框架（WPF / Avalonia）无法复用同一条数据管线，必须重写
2. 码率自适应、帧调度等横切机制无处挂载
3. 客户端渲染逻辑与平台渲染实现混在一起

### 1.2 目标

参照 ScottPlot 的分层思路（逻辑层不依赖 UI，平台层只负责推屏），把数据通路抽象为 **五层零件 + 一层编排**：

```
编排层 (Session)
   │
   ▼
① 截屏 → ② 编码 → ③ 传输 → ④ 解码 → ⑤ 渲染
```

- 五层零件全部以接口暴露，实现可插拔
- 编排层只依赖接口，可被 WPF / Avalonia 共享
- 纯视频流协议（H.264），不保留图片传输兼容路径

> **"接口可插拔"的边界澄清（L4）**：可插拔的是各层的**后端实现**——`IScreenCapturer`（BitBlt/镜像驱动）、`IVideoEncoder`/`IVideoDecoder`（x264/OpenH264/MF）、`ITransportClient`/`ITransportServer`（TCP/UDP）、`IRenderTarget`（WPF/Avalonia）。而 `FrameBuffer`、`CursorInfo`、`ScreenRect`、协议消息类等是 **Core 共享的具体类型**（双槽算法/数据结构固定，抽象意义不大），各后端与编排层共用，不要求做成接口。区分二者：**跨平台/多实现的零件用接口，单一算法/数据载体用具体类**。
>
> **命名空间归属（L7）**：编解码接口（`IVideoEncoder`/`IVideoDecoder`/Factory）虽置于 `EasyRDP.Core.Protocol` 命名空间下（因 `CodecId`/`CodecCapabilities` 与握手协议紧密相关），但其职责是数据处理而非协议线格式。`Protocol` 命名空间同时容纳"编解码抽象"与"协议消息/常量"两组职责——若未来编解码后端增多，可拆出 `EasyRDP.Core.Codec` 独立命名空间，当前保持合并以减少文件分散。

---

## 2. 关键决策

| # | 决策 | 说明 |
|---|---|---|
| D1 | 纯视频流协议 | 仅保留 H.264 编码路径，去除 Bitmap 图片传输。协议版本 `0x02`，不接受 v0x01 连接 |
| D2 | 光标独立传输 | 光标不混入视频帧像素，作为独立消息流。`FrameBuffer` 不感知光标，叠加由平台渲染层负责 |
| D3 | `FrameBuffer` 置于 Core | 渲染逻辑层不依赖 UI，放在 `EasyRDP.Core/Rendering/` 下，Avalonia 客户端直接复用 |
| D4 | `IVideoDecoder` 抽象 | 镜像 `IVideoEncoder`，新增 `DecoderFactory`。为未来 VP8/VP9 解码后端铺路 |
| D5 | `IRenderTarget : IDisposable` | 平台渲染接口包含完整生命周期。光标参数封装为 `CursorInfo` 结构体。输入为 BGRA32 `byte[]`，不预设渲染方式 |
| D6 | 客户端编排延后 | `IClientStreamSession`/`IClientInputSession` 先定义接口，后续实现 |
| D7 | 编排层按流向拆分 | 服务端：`IServerStreamSession`（视频）+ `IServerInputSession`（输入）。客户端对称 |
| D8 | 生产者-消费者线程模型 | 服务端：编码→有界队列→发送双线程。客户端：接收→有界队列→渲染双线程。队列满时生产者跳帧 |
| D9 | 单捕获多编码 | 全局一个截屏线程捕获一次，事件分发给 N 个客户端的独立编码管线。参照 TigerVNC / RustDesk / xrdp / Sunshine |
| D10 | XP 抓屏双后端运行期选择 | XP 无 DXGI Desktop Duplication（Win8+ 专属）。`IScreenCapturer` 提供两套 net40 实现：GDI BitBlt（零部署、慢）与镜像驱动 Mirror Driver（快、需装驱动）。`EasyDesk` 启动时探测镜像驱动是否安装，已装则用镜像驱动、未装回退 BitBlt。接口不变，选择对上层透明。参照 UltraVNC / TightVNC |
| D11 | CPU 自适应降级 | XP 时代硬件（P4 / 早期 Core 2）很可能跑不动 1080p30 软件编码。服务端按实测编码耗时自适应：编码跟不上时自动降分辨率（1080p→720p→540p）/ 降 fps，跟得上时回升。丢帧（D8）只防积压，D11 才是弱机流畅的杠杆。`TargetBitrate` 由静态值改为自适应输入 |
| D12 | 并发上限 + 全局负载感知 | D9 的"每 Session 独立编码线程"在 N 客户端时产生 2N+1 线程，XP 双核上 N≥3 即严重争抢。D11 是 per-Session 决策，无法感知"根因是全局过载而非单路慢"。D12 补两层：(1) `TransportHost` 设并发上限（默认 ≤2，超限新连接回 `ServerBusy`）；(2) D11 升级为全局感知——`TransportHost` 统计总 CPU 编码负载，过载时通知所有 Session 同步降级，而非各自独立决策。不改为线程池（违背 D9 独立管线初衷），仅加全局协调 |

---

## 3. 架构总览

### 3.1 数据流

**服务端**：
```
┌─ 全局截屏线程 ────────────────────────────────────────┐
│ IScreenCapturer ──→ ICaptureService.FrameCaptured 事件 │
└───────────────────────────────────────────────────────┘
                    │
        ┌───────────┼───────────┐
        ▼           ▼           ▼
  ┌─ Session₁ ─┐ ┌─ Session₂ ─┐ ┌─ Session₃ ─┐
  │ Encoder    │ │ Encoder    │ │ Encoder    │
  │ [Queue]    │ │ [Queue]    │ │ [Queue]    │
  │ SendThread │ │ SendThread │ │ SendThread │
  └────────────┘ └────────────┘ └────────────┘
         ↓              ↓              ↓
    Client₁         Client₂        Client₃

┌─ 光标线程（全局单例）─────────────────┐
│ ICursorTracker → 各 Session 内部路由   │
└────────────────────────────────────────┘
```

**客户端**：
```
[接收线程] ITransportClient → IVideoDecoder ──→ FrameBuffer (双槽=有界队列)
                                                  ↓
[渲染线程]                            TryBorrowReadFrame → IRenderTarget → ReleaseReadFrame
                                            ↑ 光标 → IRenderTarget.UpdateCursor
```
> 客户端侧 D8 所述"有界队列"即 FrameBuffer 的双槽本身（写槽满→跳帧），不再另设独立队列；双槽既是双缓冲也是容量为 2 的有界队列。

### 3.2 目标目录结构

```
EasyRDP.Core/
├── Protocol/
│   ├── IVideoEncoder.cs
│   ├── EncodedFrame.cs             (编码中性结果，H1)
│   ├── IVideoDecoder.cs            (含 DecodeResult/DecodeStatus)
│   ├── ICursorTracker.cs           (全局生命周期)
│   ├── ICursorTrackerSession.cs    (会话级光标控制，H4/H5)
│   ├── H264Encoder.cs              (#if NET8_0_OR_GREATER)
│   ├── H264Decoder.cs              (#if NET8_0_OR_GREATER)
│   ├── H264EncoderNative.cs        (#else net40: libx264/OpenH264 P/Invoke)
│   ├── H264DecoderNative.cs        (#else net40: 与编码器对称)
│   ├── CursorTracker.cs
│   ├── EncoderFactory.cs           (含 GetAvailableCodec/GetAvailableCodecs)
│   ├── DecoderFactory.cs           (含 GetAvailableCodec/GetAvailableCodecs)
│   ├── CodecNegotiator.cs          (握手编码协商)
│   ├── BinaryPacker.cs             (二进制序列化器，6.3)
│   ├── CodecCapabilities.cs
│   ├── CodecId.cs
│   ├── MessageType.cs
│   ├── Constants.cs
│   ├── VideoFrameMessage.cs
│   └── Messages/
│       ├── InputEvent.cs           (InputEventType/InputEventMessage)
│       ├── HandshakeMessages.cs    (HandshakeReq/HandshakeRes)
│       └── CursorUpdateMessage.cs
│
├── Transport/
│   ├── ITransportClient.cs
│   ├── ITransportServer.cs         (含 FragmentReceivedEventArgs/ConnectionEventArgs/LogCallback)
│   ├── MessageReassembler.cs       (分片重组桥接层，4.3.1，含 MessageReceivedEventArgs)
│   └── TCP/UDP 实现
│
├── Services/
│   └── ICaptureService.cs
│
├── Rendering/
│   ├── FrameBuffer.cs              (含 ReadFrameRef)
│   ├── ScreenRect.cs
│   ├── IRenderTarget.cs
│   └── CursorInfo.cs
│
└── Session/
    ├── IServerStreamSession.cs
    ├── IServerInputSession.cs
    ├── IClientStreamSession.cs
    ├── IClientInputSession.cs
    ├── FrameToSend.cs
    ├── CapturedFrame.cs
    └── ErrorEventArgs.cs

EasyDesk/                              (子模块)
└── src/EasyDesk.Core/
    └── IScreenCapturer.cs

EasyRDP.Client.Wpf/
└── Services/
    └── WpfRenderTarget.cs

EasyRDP.Server.Wpf/
└── Services/
    ├── CaptureService.cs
    ├── ServerStreamSession.cs
    ├── ServerInputSession.cs
    └── TransportHost.cs
```

---

## 4. 接口规范

> 所有接口遵循：`using` 在 `namespace` 内、XML doc 注释、公开 API 全覆盖。
> **net40 路径约束 C# 5.0**（无 `async/await` / `$""` / `?.` / `nameof` / 表达式体成员 / 数字分隔符 `_`）；net8.0 专有实现用 `#if NET8_0_OR_GREATER` 隔离。
>
> ⚠️ **.NET 版本与 XP 兼容性红线**：
> - XP 最高仅支持 .NET Framework **4.0**；4.5+ 要求 Vista+，装不上 XP。
> - 服务端（运行于被控 XP 机器）与希望兼容 XP 的客户端，其项目 `TargetFramework` **必须锁定 net40，禁止升号到 net45/net46/net48**，否则 XP 端直接失效。
> - **net40 必须具备可用编解码后端**：服务端跑在 XP，H.264 编码发生在服务端；若 `H264Encoder`/`H264Decoder` 仅存在于 `#if NET8_0_OR_GREATER`，net40 服务端 `EncoderFactory`/`DecoderFactory` 将全部返回 null → 握手 `Capabilities=None` → `NoCommonCodec` → 连不上。
> - net40 编解码后端通过**原生软件库 P/Invoke** 实现：`libx264`（首选，MIT，质量/速度最佳）或 `OpenH264`（Cisco，BSD）。原生 DLL 须为 **x86 + XP 兼容构建**（工具链不得引用 Vista+ API；XP 无 MediaFoundation，无硬件编码）。
> - 抽象层契约：`EncoderFactory.Create(CodecId.H264Software)` 与 `DecoderFactory.Create(CodecId.H264Software)` 在 **net40 与 net8.0 下都必须能返回可用实例**（DLL 缺失时才返回 null），否则五层抽象无法支撑 XP 用例。
>
> **日志策略（D3）**：仅传输层（`ITransportClient`/`ITransportServer`）通过 `LogCallback` 回调暴露日志，因其内部网络事件最需观测。捕获/编码/解码/会话层不设独立日志钩子，状态通过返回值与事件上报：编码失败由 `Encode` 返回 null（连续 30 帧→`FatalError`）；解码故障由 `DecodeResult.Status`/`IsAvailable=false` 上报；Session 不可恢复故障经 `FatalError` 事件。若需全层统一日志，由实现层在构造各零件时注入统一的 `LogCallback`（实现可扩展，抽象层不强加）。
>
> **生命周期契约（D6）**：所有 `IDisposable` 零件/会话遵循：
> 1. `Dispose` 必须**幂等**（重复调用安全，不抛异常）。
> 2. 有 `Start`/`Stop` 的，`Dispose` 等价于先 `Stop` 再释放资源——但**调用方应显式 `Stop`**（带超时回收线程），勿依赖 `Dispose` 去做线程 Join，以免 `Dispose` 阻塞。
> 3. `Dispose` 释放非托管资源（原生编解码句柄、Socket、非托管像素 `Scan0` 由各拥有者释放）；托管对象随 GC。
> 4. `Dispose` 后再调用实例方法行为未定义（实现可抛 `ObjectDisposedException`）。

### 4.1 第①层：捕获

#### IScreenCapturer (EasyDesk)

```csharp
namespace EasyDesk.Core
{
    /// <summary>
    /// 屏幕捕获抽象。V1 仅支持主显示器（#6）：CaptureScreen 捕获主屏，GetPrimaryScreen 返回主屏边界。
    /// 多显示器为 V2 范围——GetAllScreens 预留扩展点，V1 实现可抛 NotImplementedException 或仅返回主屏。
    /// 握手 HandshakeRes 仅携带主屏分辨率，多屏场景需 V2 扩展协议。
    /// </summary>
    public interface IScreenCapturer
    {
        /// <summary>捕获主屏幕，返回 BGRA32 非托管像素。调用方负责 Marshal.FreeHGlobal(Scan0)。</summary>
        ScreenFrame CaptureScreen();

        /// <summary>获取主显示器边界。供 ICaptureService 内部使用；编排层应调 ICaptureService.GetPrimaryScreen（D7），不直接用本方法。</summary>
        DesktopBounds GetPrimaryScreen();

        /// <summary>V2 预留：枚举所有显示器。V1 实现可抛 NotImplementedException。</summary>
        DesktopBounds[] GetAllScreens();
    }

    /// <summary>捕获帧。Scan0 为非托管 BGRA32 像素，调用方须 Marshal.FreeHGlobal 释放。</summary>
    public struct ScreenFrame
    {
        public IntPtr Scan0;     // 非托管像素缓冲
        public int Width;
        public int Height;
        public int Stride;       // 每行字节数（BGRA32 下通常 = Width*4）
    }

    /// <summary>显示器边界。</summary>
    public struct DesktopBounds
    {
        public int X, Y, Width, Height;
    }
}
```

#### ICaptureService (D9)

```csharp
namespace EasyRDP.Core.Services
{
    /// <summary>
    /// 全局捕获服务。单例，生命周期与 TransportHost 相同。
    /// 拥有独立截屏线程，通过 FrameCaptured 事件分发给所有 Session。
    /// 因 event Action 的 Invoke 是同步的——所有订阅者回调执行完毕后返回——
    /// CaptureService 在 Invoke 返回后调用 Marshal.FreeHGlobal(frame.Scan0)。
    /// 订阅方在回调中应拷贝像素，不应持有 Scan0 引用到回调之外。
    /// </summary>
    public interface ICaptureService : IDisposable
    {
        void Start();
        void Stop();
        bool IsRunning { get; }
        /// <summary>截屏间隔（毫秒）。默认 16（≈60fps），由实现层在构造时赋默认值。</summary>
        int FrameIntervalMs { get; set; }

        /// <summary>帧捕获事件。在截屏线程中触发，参数为 ScreenFrame。
        /// 不传 IScreenCapturer——订阅方无需拿到捕获器（M5），如需屏幕边界由 CaptureService 另行暴露。</summary>
        event Action<ScreenFrame> FrameCaptured;

        /// <summary>获取主显示器边界（供编排层初始化编码器尺寸）。</summary>
        DesktopBounds GetPrimaryScreen();
    }
}
```

### 4.2 第②层：编码

> **为什么 IVideoEncoder 和 IVideoDecoder 是两个独立接口？**
> 编码器在服务端，解码器在客户端——不同进程、不同生命周期、不同线程模型。
> 配对通过握手协议协商的 `CodecId` 保证：服务端 `EncoderFactory.Create(codec)` 和
> 客户端 `DecoderFactory.Create(codec)` 使用同一个协商后的 `codec` 值。
> 合并成 `ICodec` 会让服务端被迫引用解码逻辑，违反最小依赖原则。

#### IVideoEncoder

```csharp
namespace EasyRDP.Core.Protocol
{
    public interface IVideoEncoder : IDisposable
    {
        CodecId Codec { get; }
        bool IsAvailable { get; }

        /// <summary>初始化编码器。width/height 绑定后不可变——需分辨率变更时先 Reset 再 Initialize。</summary>
        void Initialize(int width, int height, int targetBitrate);

        /// <summary>
        /// 编码一帧 BGRA32 像素。返回中性 EncodedFrame（仅压缩数据，不含协议字段）；
        /// 返回 null 表示编码失败——调用方应跳过并计数。连续失败 30 帧视为编码器故障。
        /// 协议消息（VideoFrameMessage，含 SequenceNumber 等）由编排层负责包装，编码层不感知协议。
        /// </summary>
        EncodedFrame Encode(byte[] pixels, bool forceKeyframe);

        /// <summary>重置编码器内部状态（丢包恢复、分辨率变更）。Reset 后须重新 Initialize 才可编码。</summary>
        void Reset();
    }

    /// <summary>编码结果。中性数据结构，不含协议字段，由编排层包装为 VideoFrameMessage。</summary>
    public struct EncodedFrame
    {
        public byte[] Data;          // H.264 压缩字节
        public bool IsKeyframe;      // 是否 IDR 关键帧
        public int Width;            // 编码时的宽度（分辨率变更时会变化）
        public int Height;
    }
}
```

> **状态机契约（编解码器通用）**：
> 1. 构造后 `IsAvailable` 可读，但未 `Initialize` 前**禁止** Encode/Decode（实现可抛 `InvalidOperationException`）。
> 2. `Initialize` 绑定 width/height/targetBitrate；运行中分辨率变更须先 `Reset` 再 `Initialize(newW,newH,...)`。
> 3. `Reset` 清空内部参考帧与缓冲，回到"已构造未初始化"状态——Reset 后必须重新 Initialize。
> 4. 编码连续失败 30 帧 / 解码 native 致命错误（`IsAvailable` 置 false）视为故障，调用方触发断连重建。

#### EncoderFactory

```csharp
namespace EasyRDP.Core.Protocol
{
    public static class EncoderFactory
    {
        /// <summary>
        /// 创建指定编码器。返回 null 表示当前平台不支持（如原生 DLL 缺失）。
        /// net40 与 net8.0 下 H264Software 都必须有可用实现——服务端常跑在 XP/.NET4。
        /// </summary>
        public static IVideoEncoder Create(CodecId codec)
        {
            switch (codec)
            {
                case CodecId.H264Software:
#if NET8_0_OR_GREATER
                    var e8 = new H264Encoder();           // net8 管理端实现
                    return e8.IsAvailable ? e8 : null;
#else
                    var e4 = new H264EncoderNative();     // net40: libx264/OpenH264 P/Invoke
                    return e4.IsAvailable ? e4 : null;
#endif
                case CodecId.H264Hardware:
                    return null; // XP 无 MF，未来仅在 net8+ Vista 实现硬件编码
                default:
                    return null;
            }
        }

        /// <summary>探测单个编码器是否可用（创建后立即 Dispose）。与 DecoderFactory 对称。</summary>
        public static CodecId? GetAvailableCodec(CodecId preferred)
        {
            var e = Create(preferred);
            if (e != null) { e.Dispose(); return preferred; }
            return null;
        }

        /// <summary>
        /// 枚举本机所有可用编码器，返回能力位掩码。握手时服务端调用此方法广告实际能力
        /// （动态探测——仅含能实际创建的编码器，而非静态全量声明）。
        /// </summary>
        public static CodecCapabilities GetAvailableCodecs()
        {
            var caps = CodecCapabilities.None;
            foreach (CodecId c in new[] { CodecId.H264Software, CodecId.H264Hardware })
            {
                if (!GetAvailableCodec(c).HasValue) continue;
                // 显式映射，避免 CodecId 数值与 CodecCapabilities 位位置隐式耦合（D1）
                switch (c)
                {
                    case CodecId.H264Software: caps |= CodecCapabilities.H264Software; break;
                    case CodecId.H264Hardware: caps |= CodecCapabilities.H264Hardware; break;
                }
            }
            return caps;
        }
    }
}
```

### 4.3 第③层：传输

#### ITransportClient

```csharp
namespace EasyRDP.Core.Transport
{
    public interface ITransportClient : IDisposable
    {
        bool Connect(string host, int port, int timeoutMs);
        void Disconnect();
        /// <summary>
        /// 尽力发送已构好的完整线格式分片字节（含 6.3 framing 外层 + 6.3.1 分片头 + FragData）。
        /// 传输层不保证：有序到达、不丢失、不重复——由 MessageReassembler 兜底，见 4.3.1。
        /// 返回 true=已写入底层；false=连接已断/发送失败。
        /// </summary>
        bool Send(byte[] data);
        bool IsConnected { get; }
        /// <summary>收到一个线格式分片字节时触发（可能乱序/重复/丢失）。数据交由 MessageReassembler 重组，见 4.3.1。</summary>
        event EventHandler<FragmentReceivedEventArgs> DataReceived;
        event EventHandler Disconnected;
        LogCallback OnLog { get; set; }
    }
}
```

#### ITransportServer

```csharp
namespace EasyRDP.Core.Transport
{
    public interface ITransportServer : IDisposable
    {
        void Start(int port);
        void Stop();
        /// <summary>向指定客户端尽力发送一个线格式分片字节。语义同 ITransportClient.Send。</summary>
        /// <remarks>公平性约束（#5）：各 Session 的发送操作应直接写入其对应 Socket，不得有全局发送锁。
        /// 若实现层合并了发送路径（如单发送循环），须保证公平调度（Round-Robin 或按 Session 独立队列），
        /// 避免高分辨率/高频 Session 霸占通道致低分辨率 Session 饿死。</remarks>
        void SendTo(uint sessionId, byte[] data);
        void Disconnect(uint sessionId);
        event EventHandler<ConnectionEventArgs> ClientConnected;
        event EventHandler<ConnectionEventArgs> ClientDisconnected;
        event EventHandler<FragmentReceivedEventArgs> DataReceived;
        LogCallback OnLog { get; set; }
    }

    /// <summary>传输层收到一个线格式分片字节的事件参数。Data 为完整线格式分片（含 framing 外层+分片头+FragData），可能乱序/重复/丢失。</summary>
    public class FragmentReceivedEventArgs : EventArgs
    {
        public uint SessionId;   // 服务端：来源客户端；客户端实现可忽略
        public byte[] Data;      // 分片字节（Magic+Type+PayloadLen + FrameId+FragIdx+FragCount+CRC16+FragData）
    }

    /// <summary>连接事件参数。</summary>
    public class ConnectionEventArgs : EventArgs
    {
        public uint SessionId;
        public string RemoteEndPoint;  // "host:port"，可空
    }

    /// <summary>日志回调委托。传输层内部日志通过它回传，不依赖第三方日志库。</summary>
    public delegate void LogCallback(string message);
}
```

### 4.3.1 MessageReassembler（分片重组桥接层）

> **定位**：位于传输层与 Session/TransportHost 之间。传输层通过 `DataReceived` 抛分片字节，`MessageReassembler` 按 6.3.1 规则重组为完整消息，然后以 `MessageReceived` 事件通知上层。上层（TransportHost、IClientStreamSession）只看到完整消息，不感知分片/乱序/丢包。

```csharp
namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 消息分片重组器。每个 Session 独立一个实例。
    /// 订阅传输层的 FragmentReceivedEventArgs（接收）→ 按 FrameId 重组 → 校验 CRC16
    /// → 收齐全部 FragCount 个分片后拼接为完整消息 → 抛出 MessageReceived。
    /// 乱序/超时/CRC失败按 6.3.1 丢帧策略处理（丢整帧不重传，最新帧优先）。
    /// 发送侧：提供 FragAndSend 静态方法，把完整 payload 切分为分片并逐片写入指定 Action。
    /// </summary>
    public class MessageReassembler
    {
        // —— 接收侧：重组 ——

        /// <summary>收到一个线格式分片（来自传输层 DataReceived）。非线程安全，调用方须保证串行。</summary>
        public void OnFragment(FragmentReceivedEventArgs frag)
        {
            // 解析 framing 外层 → 得到 MessageType、PayloadLen（各分片一致）
            // 解析分片头 → 得到 FrameId、FragIdx、FragCount、CRC16
            // 校验 CRC16 → 失败则丢弃（=丢包）
            // 按 FrameId 维护分片缓冲：
            //   - frag.FrameId == _currentFrameId → 填入槽位，收齐则组装→抛 CompleteMessageReceived
            //   - frag.FrameId > _currentFrameId → 新帧优先，丢弃旧帧 → 开始新帧
            //   - frag.FrameId < _currentFrameId → 旧帧残余，丢弃
            // 超时检查（FragmentReassembleTimeoutMs）：当前帧超时未收齐 → 丢弃
        }

        /// <summary>完整消息组装完成事件。上层（Session/TransportHost）订阅此事件处理消息。</summary>
        public event EventHandler<MessageReceivedEventArgs> MessageReceived;

        // —— 发送侧：分片 ——

        /// <summary>
        /// 把完整消息 payload 切分为分片并逐片发送。
        /// payload 已用 BinaryPacker 序列化好（见 6.3/6.8），本方法负责装 framing 外层 + 分片头 + CRC16 + 逐片写 sendAction。
        /// </summary>
        /// <param name="frameId">本帧 ID（服务端/客户端各自的发送计数器）</param>
        /// <param name="messageType">MessageType 枚举</param>
        /// <param name="payload">序列化后的消息 payload</param>
        /// <param name="sendAction">发送回调（服务端: (sessionId, bytes) => transport.SendTo; 客户端: (_, bytes) => transport.Send）</param>
        /// <param name="sessionId">服务端 SessionId（客户端传任意值即可）</param>
        public static void FragAndSend(uint frameId, byte messageType, byte[] payload,
            Action<uint, byte[]> sendAction, uint sessionId)
        {
            int totalLen = payload.Length;
            int fragCount = (totalLen + Constants.FragmentSize - 1) / Constants.FragmentSize;
            for (int i = 0; i < fragCount; i++)
            {
                int offset = i * Constants.FragmentSize;
                int fragLen = Math.Min(Constants.FragmentSize, totalLen - offset);
                byte[] fragData = new byte[fragLen];
                Buffer.BlockCopy(payload, offset, fragData, 0, fragLen);
                // 构造线格式分片：framing 外层 + 分片头 + FragData
                byte[] wire = BuildWireFragment(frameId, (ushort)i, (ushort)fragCount, messageType, totalLen, fragData);
                sendAction(sessionId, wire);
            }
        }

        private static byte[] BuildWireFragment(uint frameId, ushort fragIdx, ushort fragCount,
            byte messageType, int totalPayloadLen, byte[] fragData)
        {
            // Magic(1)+Type(1)+PayloadLen(4)+FrameId(4)+FragIdx(2)+FragCount(2)+CRC16(2)+FragData
            // 用 BinaryPacker 或 MemoryStream 拼接
            var ms = new System.IO.MemoryStream();
            var bw = new System.IO.BinaryWriter(ms);
            bw.Write(Constants.FrameMagic);
            bw.Write(messageType);
            bw.Write((uint)totalPayloadLen);
            bw.Write(frameId);
            bw.Write(fragIdx);
            bw.Write(fragCount);
            bw.Write(Crc16(fragData));
            bw.Write(fragData);
            return ms.ToArray();
        }

        private static ushort Crc16(byte[] data)
        {
            // CRC-16/XMODEM 或等价轻量实现，net40 可用。这里仅占位，实现自行选具体多项式。
            // 校验失败在接收侧 OnFragment 中处理。
            throw new NotImplementedException();
        }
    }

    /// <summary>完整消息事件参数。MessageReassembler 组装完毕后抛出，Data 为完整消息 payload（去 framing+分片头）。</summary>
    public class MessageReceivedEventArgs : EventArgs
    {
        public uint SessionId;
        public byte MessageType;    // MessageType 枚举
        public byte[] Data;         // 完整消息 payload（不含 framing 外层与分片头）
    }
}
```

> **层次小结**：
> ```
> Session / TransportHost ── 处理 MessageReceived (完整消息)
>        │
> MessageReassembler ── 分片重组 / CRC16 / 顺序保证 / 丢帧判定
>        │
> ITransportClient/Server ── DataReceived (分片字节) / Send (线格式分片)
>        │
> TCP / UDP / WebSocket ── 物理传输
> ```
>
> **每个方向上服务端为每个活跃 Session 各创建独立的 MessageReassembler 实例**（服务端：TransportHost 为每个 Session 创建一个，订阅 ITransportServer.DataReceived 并只处理该 SessionId 的分片；客户端：IClientStreamSession 持有一个）。一个实例跟踪一路 FrameId 流，内部状态简洁，无需 per-SessionId 多路复用。

### 4.4 第④层：解码

#### IVideoDecoder

```csharp
namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 视频解码器抽象。镜像 IVideoEncoder。
    /// 接口在 net40 / net8.0 均可见；实现用 #if NET8_0_OR_GREATER 隔离（net40 走原生 P/Invoke 后端）。
    /// </summary>
    public interface IVideoDecoder : IDisposable
    {
        CodecId Codec { get; }
        bool IsAvailable { get; }

        /// <summary>初始化解码器。width/height 来自 HandshakeRes。</summary>
        void Initialize(int width, int height);

        /// <summary>
        /// 解码一帧。返回 DecodeResult：
        ///   - Status=NeedMoreInput：B 帧未就绪/解码器启动初期缓冲，正常，调用方静默等待下一帧；
        ///   - Status=Ok + Pixels：解码成功，返回 BGRA32 像素；
        ///   - Status=Failed：可恢复解码错误，调用方计数并跳过（连续失败达阈值才断连）；
        ///   native 层致命错误时实现设 IsAvailable=false，调用方检测后触发断连。
        /// </summary>
        DecodeResult Decode(byte[] data);

        /// <summary>
        /// 解码到调用方提供的输出缓冲（L3 省拷贝优化）。
        /// outputBuffer 须 >= width*height*4；解码成功时 Status=Ok 且 Pixels 引用 outputBuffer 本身
        /// （实现直接写入，不另分配），调用方可省去一次 BlockCopy。输出缓冲不足时返回 Failed。
        /// </summary>
        DecodeResult Decode(byte[] data, byte[] outputBuffer);

        void Reset();
    }

    /// <summary>解码结果。区分"无输出"与"失败"，避免把启动缓冲误判为故障。</summary>
    public struct DecodeResult
    {
        public DecodeStatus Status;
        public byte[] Pixels;   // Status=Ok 时有效
    }

    public enum DecodeStatus : byte
    {
        Ok = 0,
        NeedMoreInput = 1,   // 解码器缓冲，无输出（非错误）
        Failed = 2           // 可恢复解码错误
    }
}
```

#### DecoderFactory

```csharp
namespace EasyRDP.Core.Protocol
{
    public static class DecoderFactory
    {
        public static IVideoDecoder Create(CodecId codec)
        {
            switch (codec)
            {
                case CodecId.H264Software:
#if NET8_0_OR_GREATER
                    var d8 = new H264Decoder();           // net8 管理端实现
                    return d8.IsAvailable ? d8 : null;
#else
                    var d4 = new H264DecoderNative();     // net40: 与编码器对称的原生后端
                    return d4.IsAvailable ? d4 : null;
#endif
                case CodecId.H264Hardware:
                    return null; // 未来实现
                default:
                    return null;
            }
        }

        public static CodecId? GetAvailableCodec(CodecId preferred)
        {
            var d = Create(preferred);
            if (d != null) { d.Dispose(); return preferred; }
            return null;
        }

        /// <summary>
        /// 枚举本机所有可用解码器，返回能力位掩码。握手时客户端调用此方法广告解码能力。
        /// 与 EncoderFactory.GetAvailableCodecs 对称（B2）。
        /// </summary>
        public static CodecCapabilities GetAvailableCodecs()
        {
            var caps = CodecCapabilities.None;
            foreach (CodecId c in new[] { CodecId.H264Software, CodecId.H264Hardware })
            {
                if (!GetAvailableCodec(c).HasValue) continue;
                switch (c)
                {
                    case CodecId.H264Software: caps |= CodecCapabilities.H264Software; break;
                    case CodecId.H264Hardware: caps |= CodecCapabilities.H264Hardware; break;
                }
            }
            return caps;
        }
    }
}
```

### 4.4.5 光标追踪

#### ICursorTracker

```csharp
namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 光标追踪抽象。**全局单例**（与 ICaptureService 同生命周期，由 TransportHost 持有），
    /// 拥有独立 60Hz 线程检测光标位置与形状变化。多客户端共用一个 60Hz 线程，不随客户端数膨胀。
    ///
    /// 接口拆分（H4/H5）：本接口仅含全局生命周期管理，由 TransportHost 调用；
    /// 会话级控制（订阅/退订单客户端）通过 ICursorTrackerSession 暴露给单个 Session，
    /// 避免通过一个 Session 的属性调用到 StopAll 影响所有客户端。
    /// </summary>
    public interface ICursorTracker : IDisposable
    {
        int IntervalMs { get; set; }
        bool EnableShape { get; set; }
        /// <summary>停止所有客户端的光标追踪并结束 60Hz 线程。仅 TransportHost 在停机时调用。</summary>
        void StopAll();
    }

    /// <summary>
    /// 会话级光标控制。由 ICursorTracker 为指定 sessionId 派生，注入到对应 ServerStreamSession。
    /// 只能控制本 Session 的光标订阅，无法影响其他客户端。
    /// SendTo 回调通过本接口的 AttachSendTo 注入——这是 H4 修复：注入入口显式存在于接口。
    /// </summary>
    public interface ICursorTrackerSession
    {
        /// <summary>注入本会话的发送回调。光标变化时通过它发送 CursorUpdateMessage。</summary>
        void AttachSendTo(Action<uint, byte[]> sendTo, uint sessionId);
        void Start();
        void Stop();
    }
}
```

### 4.5 第⑤层：渲染

> **解码层与渲染层如何衔接？** `FrameBuffer` 是桥。④→⑤ 的数据通路（L3 优化后）：
> ```
> IVideoDecoder.Decode(data, writeSlot) → [直接写入 FrameBuffer 槽位，省去优化前中间的 BlockCopy] →
>   FrameBuffer.CommitFrame → [双槽交换] →
>   FrameBuffer.TryBorrowReadFrame(out ReadFrameRef) → IRenderTarget.RenderFrame → ReleaseReadFrame
> ```
> 解码线程写 FrameBuffer 的槽 A，渲染线程读槽 B。`CommitFrame` 原子交换两槽。
> 全链路 2 次数据搬运（解码器→写槽、读槽→GPU），L3 省去了优化前的中间一次 BlockCopy（原本 3 次）；`BorrowWriteBuffer` 和 `TryBorrowReadFrame` 都返回内部 `byte[]` 引用，不额外分配。

#### FrameBuffer（渲染逻辑层，零拷贝双槽）

```csharp
namespace EasyRDP.Core.Rendering
{
    /// <summary>
    /// 客户端本地帧缓冲。双槽双缓冲：解码线程写 slot A，渲染线程读 slot B，
    /// CommitFrame 原子交换。全链路 2 次数据搬运（解码器→写槽、读槽→GPU），L3 省去优化前中间的 BlockCopy（原本 3 次）。
    /// 线程安全：lock 保护所有操作（含 Sequence/FrameCount 读取——net40/XP 常为 32 位进程，
    /// long 读写非原子，必须加锁，否则 Sequence 会撕裂）。
    /// </summary>
    public class FrameBuffer
    {
        private byte[][] _slots = new byte[2][];
        private int _writeSlot = 0;
        private int _readSlot = -1;
        private int _readingSlot = -1;
        private long _readBorrowTicks;   // 读帧借用时刻（Stopwatch.GetTimestamp），#7 超时强制释放
        private const long ReadBorrowTimeoutMs = 5000; // 读帧借用超时：5s 未释放则强制回收
        private int _width, _height;
        private int _frameCount;
        private long _sequence;
        private readonly object _lock = new object();
        // 注：dirty rect 机制已移除（M4）。纯 H.264 整帧路径下无局部脏区概念，
        // 保留只会每帧产生无意义的 List/数组分配，与零拷贝低分配理念冲突。
        // 未来若引入分块编码再按需恢复。

        public int Width { get { lock (_lock) return _width; } }
        public int Height { get { lock (_lock) return _height; } }
        public int FrameCount { get { lock (_lock) return _frameCount; } }
        public long Sequence { get { lock (_lock) return _sequence; } }

        /// <summary>借用写缓冲区。返回 null 表示无可写槽位（reader 仍持有另一槽）。</summary>
        public byte[] BorrowWriteBuffer(int requiredSize)
        {
            lock (_lock)
            {
                if (_writeSlot == _readingSlot) return null;
                var slot = _slots[_writeSlot];
                if (slot == null || slot.Length < requiredSize)
                    _slots[_writeSlot] = new byte[requiredSize];
                return _slots[_writeSlot];
            }
        }

        /// <summary>
        /// 提交写入。原子交换读写槽。返回 false 表示 reader 仍持有且未超时——调用方应丢弃本帧。
        /// 读帧超时兜底（#7）：若 reader 借用超过 ReadBorrowTimeoutMs（5s，渲染层异常未 Release 的典型场景），
        /// 视为泄漏，强制回收 _readingSlot 并继续提交（丢弃那帧渲染结果，避免管线永久卡死）。
        /// </summary>
        public bool CommitFrame(int width, int height)
        {
            lock (_lock)
            {
                if (_readingSlot >= 0)
                {
                    long elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - _readBorrowTicks)
                        * 1000L / System.Diagnostics.Stopwatch.Frequency;
                    if (elapsedMs < ReadBorrowTimeoutMs) return false; // 正常占用，丢弃本帧
                    // 超时强制回收：渲染层异常未释放，丢弃残留读帧
                    _readingSlot = -1;
                }
                _width = width; _height = height;
                _sequence++; _frameCount++;
                _readSlot = _writeSlot;
                _writeSlot = (_writeSlot + 1) % 2;
                return true;
            }
        }

        /// <summary>
        /// 借用读帧。返回内部槽位引用（零拷贝）。调用方渲染后必须调用 ReleaseReadFrame。
        /// 用 ReadFrameRef 结构体返回（L5），避免 5 个 out 参数。
        /// </summary>
        public bool TryBorrowReadFrame(out ReadFrameRef frame)
        {
            lock (_lock)
            {
                if (_readSlot < 0) { frame = default(ReadFrameRef); return false; }
                frame = new ReadFrameRef
                {
                    Pixels = _slots[_readSlot],
                    Width = _width,
                    Height = _height,
                    Sequence = _sequence
                };
                _readingSlot = _readSlot; _readSlot = -1;
                _readBorrowTicks = System.Diagnostics.Stopwatch.GetTimestamp(); // 记录借用时刻（#7）
                return true;
            }
        }

        /// <summary>释放读帧。必须调用，否则 CommitFrame 永久返回 false。</summary>
        public void ReleaseReadFrame() { lock (_lock) { _readingSlot = -1; } }

        public void Reset()
        {
            lock (_lock)
            {
                _writeSlot = 0; _readSlot = -1; _readingSlot = -1;
                _width = _height = 0; _frameCount = 0; _sequence = 0;
            }
        }
    }

    /// <summary>读帧引用。借用期间有效，ReleaseReadFrame 后不得再访问 Pixels。</summary>
    public struct ReadFrameRef
    {
        public byte[] Pixels;
        public int Width;
        public int Height;
        public long Sequence;
    }
}
```

#### ScreenRect（渲染层内部数据结构）

```csharp
namespace EasyRDP.Core.Rendering
{
    /// <summary>屏幕矩形。当前纯 H.264 整帧路径下无脏区用途（dirty 机制已移除）；
    /// 保留此结构供未来分块编码/区域裁剪复用，非协议消息。</summary>
    public struct ScreenRect
    {
        public int X, Y, Width, Height;
    }
}
```

#### CursorInfo（光标数据结构）

```csharp
namespace EasyRDP.Core.Rendering
{
    /// <summary>光标状态。RGBA 像素 + 位置 + 热区。rgbaPixels 为 null 时仅更新位置。</summary>
    public struct CursorInfo
    {
        public bool Visible;
        public int X, Y;
        public byte[] RgbaPixels;
        public int Width, Height;
        public int HotX, HotY;
    }
}
```

#### IRenderTarget（平台渲染接口）

```csharp
namespace EasyRDP.Core.Rendering
{
    /// <summary>
    /// 平台渲染后端接口。输入 BGRA32 原始像素，不预设渲染方式：
    ///   WPF:     WriteableBitmap.WritePixels
    ///   Avalonia: SKBitmap 或 WriteableBitmap
    /// 光标叠加方式由各平台自行决定，接口不规定。
    /// 注：解码统一由 IVideoDecoder 完成，本接口只接收已解码的 BGRA32 像素；
    /// 不存在"VLC 解码"或"Bitmap 图片传输"路径（D1 已去除图片传输）。
    /// </summary>
    public interface IRenderTarget : IDisposable
    {
        /// <summary>渲染一帧。纯 H.264 整帧路径下恒为全屏刷新（无局部脏区，dirty 机制已移除）。</summary>
        void RenderFrame(byte[] bgraPixels, int w, int h);

        /// <summary>更新光标状态。光标与视频帧在不同渲染层，无同步问题。</summary>
        void UpdateCursor(CursorInfo cursor);

        /// <summary>预分配/重建渲染资源。连接成功时调用一次；分辨率变更时由编排层（IClientStreamSession）再次调用。平台层只执行重建，不自行检测变化。</summary>
        void Resize(int w, int h);
    }
}
```

---

## 5. 编排层

### 5.1 设计原则

- 按流向拆分：视频流（生产者-消费者）、输入（事件驱动）
- 客户端对称：`IServerStreamSession` ↔ `IClientStreamSession`，`IServerInputSession` ↔ `IClientInputSession`
- 光标归入 StreamSession，通过 `ICursorTracker` 接口管理
- 传输解耦：服务端用 `Action<uint, byte[]> sendTo` 回调 / 客户端注入 `ITransportClient`
- 单捕获多编码（D9）：全局 `ICaptureService` → 事件分发 → 每 Session 独立编码管线
- 生产者-消费者（D8）：每 Session 编码→有界队列→发送双线程
- 编码器动态探测：握手时 `EncoderFactory.Create` 实测可用性

### 5.2 服务端接口

#### IServerStreamSession

```csharp
namespace EasyRDP.Core.Session
{
    /// <summary>
    /// 服务端视频流会话。每个客户端连接对应一个实例。
    ///
    /// 线程模型（D8 + D9，修正 A1：编码不得占用截屏线程）：
    ///   截屏线程（全局，ICaptureService 拥有）回调本 Session：仅做轻量入队，不编码。
    ///     —— Scan0 在回调返回后即被 FreeHGlobal，故回调内必须拷贝一帧像素到 Session 私有缓冲。
    ///   编码线程（每 Session 一个）：从私有帧队列出队 → Encode → Enqueue(SendQueue)。
    ///   发送线程（每 Session 一个）：Dequeue(SendQueue) → 序列化 → sendTo。
    ///   两级有界队列：帧队列满跳帧；发送队列满跳帧。队列满时跳过本帧（不阻塞其他 Session）。
    ///
    /// 设计要点：N 客户端 = N 个独立编码线程并行（真"单捕获多编码"），避免在 XP 单/双核 CPU
    /// 上把 N 路 1080p 软编串行塞进截屏线程导致帧率塌陷。
    /// Scan0 拷贝权衡：截屏回调内 1 次拷贝（不可避免，因 Scan0 生命周期），各编码线程读私有副本。
    /// </summary>
    public interface IServerStreamSession : IDisposable
    {
        /// <summary>协商出的编码。Start 前为默认值 0（未协商）；Start(sessionId, codec) 后等于传入 codec。读后契约：Start 前读取无意义。</summary>
        CodecId Codec { get; }

        /// <summary>
        /// 编码节流间隔（毫秒），默认 33（≈30fps），0 表示每帧都编码。
        /// A1 修正后语义：编码线程从帧队列出队时按此间隔决定是否编码本帧（截屏线程只负责入队，不编码）。
        /// 截屏速率由 ICaptureService.FrameIntervalMs 独立控制（通常 16≈60fps）；
        /// 截屏 60fps + 编码 30fps 意味着编码线程每两次出队编码一次。
        /// </summary>
        int FrameDelayMs { get; set; }

        /// <summary>关键帧间隔（帧数），默认 30。运行时可改，下次关键帧生效。</summary>
        int KeyframeInterval { get; set; }

        /// <summary>目标码率（bps），默认 2000000。运行时可改——D11 自适应降级依赖此属性动态调整。</summary>
        int TargetBitrate { get; set; }

        /// <summary>帧队列容量（截屏→编码，帧数），默认 2。满时截屏回调跳帧。对应 A1 两级队列的第一级。</summary>
        int FrameQueueCapacity { get; set; }

        /// <summary>发送队列容量（编码→发送，帧数），默认 2。满时编码线程跳帧。对应 A1 两级队列的第二级。</summary>
        int SendQueueCapacity { get; set; }

        /// <summary>发送队列当前深度（监控用）。</summary>
        int PendingFrames { get; }

        /// <summary>启动视频流。sessionId 标识本客户端；codec 为握手协商结果。</summary>
        void Start(uint sessionId, CodecId codec);

        void Stop();

        /// <summary>会话级光标控制（仅本 Session 的光标订阅，非全局）。见 ICursorTrackerSession。</summary>
        ICursorTrackerSession CursorTracker { get; }

        /// <summary>
        /// 应用全局负载等级（D12）。TransportHost 在总编码负载过载时调用，要求本 Session 降级；
        /// 负载恢复时调用升级。与 per-Session 的 D11 自适应叠加——两者取更保守的设置。
        /// level: 0=正常, 1=轻度降级, 2=重度降级。
        /// </summary>
        void ApplyGlobalLoadLevel(int level);

        /// <summary>致命错误。订阅方应主动 Disconnect + Dispose。</summary>
        event EventHandler<ErrorEventArgs> FatalError;
    }
```

> **依赖注入（L1）**：`ICaptureService`（全局单例）与 `sendTo` 回调通过**构造函数**注入 `ServerStreamSession`，而非 `Start` 参数。Start 前对象依赖已就绪，避免"半成品"状态；`Start(sessionId, codec)` 只负责启动线程与订阅。`CursorTracker` 同样构造注入全局单例，但通过 `ICursorTrackerSession` 暴露给本 Session 的仅是会话级控制能力（见 H4/H5）。

#### IServerInputSession

```csharp
namespace EasyRDP.Core.Session
{
    /// <summary>服务端输入会话。事件驱动同步调用，无独立线程。</summary>
    public interface IServerInputSession : IDisposable
    {
        /// <summary>
        /// 处理一条输入事件。返回 true=成功执行；false=执行失败（如坐标越界、按键无效），
        /// 调用方可据此计数/告警，但通常不因单条失败断连。
        /// </summary>
        bool HandleInput(InputEventMessage msg);
    }
}
```

#### FrameToSend（队列元素）

```csharp
namespace EasyRDP.Core.Session
{
    public struct FrameToSend
    {
        public byte[] Data;               // 序列化后的 VideoFrameMessage
        public bool IsKeyframe;           // 队列满时优先丢弃非关键帧
        public long SequenceNumber;       // 单调递增，客户端检测丢帧
        public long CaptureTimestamp;     // Stopwatch.GetTimestamp()
    }

    /// <summary>
    /// 截屏线程入队的捕获帧（两级队列第一级 _frameQueue 的元素）。
    /// 截屏回调中从 ScreenFrame.Scan0 拷贝像素到此缓冲（Scan0 回调返回后即被释放），
    /// 供编码线程读取。Width/Height 用于分辨率变更检测。
    /// 内存复用（#4）：Pixels 的 byte[] 由 Session 内双缓冲交替提供，非每帧 new。
    /// </summary>
    public struct CapturedFrame
    {
        public byte[] Pixels;             // BGRA32 像素（双缓冲复用，非每帧分配）
        public int Width;
        public int Height;
        public long CaptureTimestamp;     // Stopwatch.GetTimestamp()
    }

    /// <summary>致命错误事件参数。Session 不可恢复故障时抛出，订阅方应断连+销毁。</summary>
    public class ErrorEventArgs : EventArgs
    {
        public string Message;            // 错误描述
        public System.Exception Exception; // 可空，底层异常（如编码器 native 故障）
    }
}
```

#### 实现要点

**CaptureService** (`EasyRDP.Server.Wpf/Services/CaptureService.cs`)
- 持有 `IScreenCapturer`（构造注入；D10：运行期由 EasyDesk 选择 BitBlt 或镜像驱动实现，对 CaptureService 透明）
- 截屏线程：`Thread.Sleep(FrameIntervalMs)` → `CaptureScreen()` → `FrameCaptured?.Invoke(frame)` → `FreeHGlobal(frame.Scan0)`（M5：事件只传 ScreenFrame，不传 IScreenCapturer）
- ⚠️ `FrameCaptured` 是同步 `Action` Invoke，所有订阅者回调执行完毕才返回并 `FreeHGlobal`。订阅方**不得**在回调中执行耗时操作（编码、网络），否则拖慢全局截屏——必须只拷贝像素后立即返回（见 ServerStreamSession）。

**ServerStreamSession** (`EasyRDP.Server.Wpf/Services/ServerStreamSession.cs`)
- 构造注入（L1）：`ICaptureService captureService`（全局单例）、`Action<uint, byte[]> sendTo`、`ICursorTracker cursorTracker`（全局单例）。`Start(sessionId, codec)` 只启动线程与订阅。
- `Start`：订阅 `FrameCaptured`；创建两级有界队列：
  - `_frameQueue`（`BlockingCollection<CapturedFrame>`，容量 = `FrameQueueCapacity`，默认 2）：截屏线程入队、编码线程出队
  - `_sendQueue`（`BlockingCollection<FrameToSend>`，容量 = `SendQueueCapacity`，默认 2）：编码线程入队、发送线程出队
  - 启动**编码线程**与**发送线程**（每 Session 各一个，真"单捕获多编码"）
  - 光标：从全局 `cursorTracker` 派生本会话的 `ICursorTrackerSession`，调 `AttachSendTo(sendTo, sessionId)` + `Start()`（H4/H5）
- `OnFrameCaptured` 回调（截屏线程中，必须极快）：拷贝 `frame.Scan0` 像素到 `CapturedFrame` 私有缓冲 → `_frameQueue.TryAdd(...)`，满则跳过本帧。**禁止在此回调内编码。**
  - **内存复用（#4）**：`CapturedFrame.Pixels` 不每帧 new，改用 Session 内**双缓冲**——两个 `byte[]` 槽位交替使用：截屏回调写入槽 A 时，编码线程读槽 B；下一帧反之。仅在分辨率变更致槽位不足时才重新分配。避免 1080p×30fps×N 客户端 = N×240MB/s 的 LOH 分配压力（XP/.NET4 非并发 GC 下碎片严重）。
- 编码线程：`_frameQueue.Take()` → 分辨率变更检测 → `Encode`（返回 `EncodedFrame`）→ **编排层包装为 `VideoFrameMessage`**（填 `SequenceNumber` 等，H1：编码层不感知协议）→ 构造 `FrameToSend` → `_sendQueue.TryAdd(...)`，满则跳帧
- 发送线程：`_sendQueue.Take()` → 序列化（`BinaryPacker`，6.3）→ framing 装 outer（6.3）→ **按 Constants.FragmentSize 切分片、每片加分片头+CRC16**（6.3.1）→ 逐片 `sendTo(sessionId, fragData)`
- 分辨率变更检测（编码线程内）：比较帧尺寸 → `IVideoEncoder.Reset()` → `Initialize(newW, newH, ...)` → `forceKeyframe=true`
- D11 自适应（编码线程内）：滑动窗口统计 `Encode` 实测耗时；超阈值则降分辨率/降 `FrameDelayMs`/调 `TargetBitrate`（运行时可改，H2），持续达标则回升，并触发 `forceKeyframe`
- D12 全局协调（D11 升级）：单 Session 降级阈值之外，`TransportHost` 汇总所有 Session 的编码耗时，总负载超阈值时向**所有** Session 下发降级指令（避免各 Session 独立降级时因根因是全局过载而治标不治本）。`IServerStreamSession` 暴露 `ApplyGlobalLoadLevel(level)` 供 TransportHost 调用。
- `Stop`（防竞态，见 #2）：设 `_stopping = true`（编码/发送线程循环顶部检查，下次迭代前退出）→ 退订 `FrameCaptured` → `CursorTrackerSession.Stop()` → `_frameQueue.CompleteAdding()` + `_sendQueue.CompleteAdding()` → Join 编码线程与发送线程，**带超时**（3s）。**超时处理**：编码线程可能正卡在 `Encode()` 内部（原生库无法中断），此时**不得立即 Dispose 编码器**（会访问已释放原生句柄→崩溃），而是将编码器标记为"待清理"、记入 `_pendingDispose` 列表，由 TransportHost 在进程退出或下轮 GC 时回收。正常 Join 成功才立即 Dispose。
  - ⚠️ `Encode()` 内部不可中断是已知限制：超时后残留编码线程的 Encode 返回值将被丢弃（队列已 CompleteAdding），但原生句柄延迟释放确保不崩溃。

**TransportHost** (`EasyRDP.Server.Wpf/Services/TransportHost.cs`)
- 持有全局 `ICaptureService` + `ITransportServer`，管理所有 Session 生命周期
- **并发上限（D12）**：维护活跃 Session 计数，默认上限 2（XP 双核实测安全值，可配；若服务端 ≥4 核，可调至 4–5）。超限时新握手回 `HandshakeRes.Result=ServerBusy`，不创建 Session。
- **全局负载感知（D12）**：周期汇总各 Session 的编码耗时统计；总负载超阈值时向所有 Session 调 `ApplyGlobalLoadLevel(1/2)` 同步降级，恢复时调 `ApplyGlobalLoadLevel(0)`。避免 per-Session D11 独立决策在全局过载时治标不治本。
- **握手处理**：为新连接创建 `MessageReassembler` 实例（4.3.1），订阅 `ITransportServer.DataReceived` 并过滤该 SessionId 的分片。Reassembler 的 `MessageReceived` 事件收到重组后的 `HandshakeReq` 后校验版本与认证（6.5.1），调 `CodecNegotiator.Negotiate(clientCaps, EncoderFactory.GetAvailableCodecs())` 协商编码；协商成功则回 `HandshakeRes` 并创建 `IServerStreamSession`+`IServerInputSession`（后续该 Session 的数据分片由此 Session 专属的 MessageReassembler 处理）；交集为空回 `NoCommonCodec`。
- **心跳检测（6.5.2，#3）**：为每个 Session 维护 `LastActivity`；定时器（10s）扫描超 30s 无活动的 Session 发 Keepalive ping，再 15s 无响应则触发 C5 断连联动，防僵尸 Session 空转。
- **断连联动（C5）**：订阅 `ITransportServer.ClientDisconnected`，回调中按 `sessionId` 找到对应 `IServerStreamSession` + `IServerInputSession`，依次调用 `Stop()`（带超时，见 D6）+ `Dispose()`，并从内部字典移除。确保断开的客户端不残留编码/发送线程、不继续占用 `FrameCaptured` 订阅。
- **服务端停机**：`TransportHost.Stop()` 遍历所有 Session 执行上述销毁流程，再 `Stop` CaptureService 与 TransportServer。

### 5.3 客户端接口（仅定义，后续实现）

#### IClientStreamSession

```csharp
namespace EasyRDP.Core.Session
{
    /// <summary>
    /// 客户端视频流会话。双线程：
    ///   接收线程：ITransportClient → IVideoDecoder → FrameBuffer.BorrowWriteBuffer → CommitFrame
    ///   渲染线程：FrameBuffer.TryBorrowReadFrame → IRenderTarget.RenderFrame → ReleaseReadFrame
    /// 分辨率变更闭环（接收线程内）：
    ///   视频侧（C6）：检测 VideoFrameMessage.Width/Height 变化 →
    ///     IVideoDecoder.Reset() + Initialize(newW,newH) → IRenderTarget.Resize(newW,newH) →
    ///     FrameBuffer 尺寸随之在下次 BorrowWriteBuffer 重建。
    ///   输入侧（D5）：检测到变化后调用关联 IClientInputSession.OnResolutionChanged(newW,newH)，
    ///     更新鼠标坐标映射比例，避免分辨率变化后鼠标错位。
    ///   编排层统一处理，不推给平台层。
    /// </summary>
    public interface IClientStreamSession : IDisposable
    {
        CodecId Codec { get; }
        /// <summary>当前渲染帧宽/高（监控用，不暴露 FrameBuffer 内部对象，避免外部绕过编排逻辑）。</summary>
        int FrameWidth { get; }
        int FrameHeight { get; }
        /// <summary>已渲染帧数（监控用）。</summary>
        long FrameCount { get; }
        IRenderTarget RenderTarget { get; set; }
        void Start(ITransportClient transport);
        void Stop();
        /// <summary>致命错误（如解码 native 故障 IsAvailable=false、传输断连）。与服务端 FatalError 对称（D4），订阅方应 Stop+Dispose 并可选重连。</summary>
        event EventHandler<ErrorEventArgs> FatalError;
    }
}
```

#### IClientInputSession

```csharp
namespace EasyRDP.Core.Session
{
    public interface IClientInputSession : IDisposable
    {
        /// <summary>
        /// 启动输入会话。screenWidth/screenHeight 为初始服务端分辨率（来自握手 HandshakeRes），
        /// 用于把客户端鼠标坐标映射到服务端屏幕坐标。
        /// </summary>
        void Start(ITransportClient transport, int screenWidth, int screenHeight);

        /// <summary>
        /// 服务端分辨率变化通知（D5）。D11 自适应降级或用户改分辨率时，StreamSession 检测到
        /// VideoFrameMessage.Width/Height 变化后调用本方法，InputSession 据此更新坐标映射比例。
        /// 鼠标坐标映射：serverX = clientX / clientViewWidth * screenWidth（screenWidth 为本方法传入值）。
        /// 不调用则输入继续按旧分辨率映射，会导致鼠标错位。
        /// </summary>
        void OnResolutionChanged(int newWidth, int newHeight);

        void Stop();
    }
}
```

---

## 6. 协议规范

### 6.1 编码类型与能力

```csharp
namespace EasyRDP.Core.Protocol
{
    public enum CodecId : byte
    {
        H264Software = 1,
        H264Hardware = 2
    }

    [Flags]
    public enum CodecCapabilities : byte
    {
        None          = 0,
        H264Software  = 1 << 0,  // = 1
        H264Hardware  = 1 << 1,  // = 2
    }
}
```

`CodecCapabilities` 用于握手时声明支持的编码器集合。服务端动态探测——仅包含 `EncoderFactory.Create` 实际创建成功的编码器，而非静态全量声明。

#### CodecNegotiator（握手编码协商）

```csharp
namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 握手编码协商器。服务端收到 HandshakeReq 后调用 Negotiate：
    /// 取客户端声明的解码能力（clientCaps）与服务端可用编码能力（由 EncoderFactory.GetAvailableCodecs
    /// 动态探测得 serverCaps）的交集，按优先级选出唯一 CodecId：硬件优先（双方都支持硬件时用硬件，
    /// 更快），否则软件。XP 服务端因无硬件编码（serverCaps 仅含 H264Software），实际必走软件。
    /// 交集为空返回 null → 握手回 NoCommonCodec。
    /// 协商逻辑集中在此类，而非散落在编排层。
    /// </summary>
    public static class CodecNegotiator
    {
        /// <summary>协商。返回 null 表示无共同编码。</summary>
        public static CodecId? Negotiate(CodecCapabilities clientCaps, CodecCapabilities serverCaps)
        {
            CodecCapabilities common = clientCaps & serverCaps;
            // 优先级：H264Hardware > H264Software（双方都支持硬件时用硬件，否则软件）
            // 注：XP 服务端 serverCaps 通常只含 H264Software，故实际走软件。
            if ((common & CodecCapabilities.H264Hardware) != 0) return CodecId.H264Hardware;
            if ((common & CodecCapabilities.H264Software) != 0) return CodecId.H264Software;
            return null;
        }
    }
}
```

### 6.2 消息类型

```csharp
namespace EasyRDP.Core.Protocol
{
    public enum MessageType : byte
    {
        HandshakeReq  = 0x01,
        HandshakeRes  = 0x02,
        Keepalive     = 0x03,   // 心跳 ping/pong，payload 为空，见 6.5.2
        InputEvent    = 0x05,
        CursorUpdate  = 0x06,
        VideoFrame    = 0x50
    }

    public enum HandshakeResult : byte
    {
        Success       = 0x00,
        AuthFailed    = 0x01,
        VersionMismatch = 0x02,
        ServerBusy    = 0x03,
        NoCommonCodec = 0x05,
        InternalError = 0xFF
    }
}
```

### 6.3 帧格式（Framing）与序列化

> **为何需要 Framing**：TCP 是字节流，接收方无法天然知道一条消息从哪开始、到哪结束。`MaxFrameSize` 50MB 的视频帧与几字节的输入事件混在同一流上，必须有明确的切分规则，否则 `ITransportClient.DataReceived` 的"分片"语义无从成立。

#### 线格式（所有消息统一外层）

所有消息外层采用 **length-prefix + 类型码** 封装，**小端字节序（Little-Endian）**：

```
┌─────────────┬──────────────┬────────────────────────────┐
│ Magic (1B)  │ Type (1B)    │ PayloadLen (4B, LE, uint32)│
│ 0xE5        │ MessageType  │ payload 字节数              │
├─────────────┴──────────────┴────────────────────────────┤
│ Payload (PayloadLen 字节，按各消息结构序列化)              │
└──────────────────────────────────────────────────────────┘
```

- `Magic = 0xE5`：每条消息起始魔术字节，用于流错位后重新对齐（解析失败时扫描到下一个 0xE5 恢复）。
- `Type`：`MessageType` 枚举值（6.2 节）。
- `PayloadLen`：4 字节小端 uint32，上限 `MaxFrameSize`（50MB），超出视为协议错误并断连。
- 传输层（`ITransportClient`/`ITransportServer` 实现）**负责 framing 外层的装/拆**：发送侧拼装字节流，接收侧按 Magic+Type+PayloadLen 切分。但**传输层不保证**：消息有序到达、不丢失、大消息完整投递——这些由协议层用机制兜底（见 6.3.1 帧分片与顺序保证），与传输方式（TCP/UDP/WebSocket）无关。
- **传输无关原则（核心）**：协议层定义"帧"的完整语义（分片、序号、校验、顺序保证、丢帧策略），传输层只承担"尽力投递分片字节"的职责。无论底层是 TCP（天然有序可靠）、UDP（乱序丢包）、WebSocket（基于 TCP 但有帧边界），协议逻辑统一不变。这样换任何传输后端，上层（Session）与协议层代码无需改动。
  - 传输层契约（`ITransportClient`/`ITransportServer`）仅要求：`Send`/`SendTo` 尽力把字节写入底层；`DataReceived` 抛出收到的分片字节（可能乱序、可能丢失、可能重复）。不要求先发先达。完整消息由 `MessageReassembler` 重组（4.3.1）。
  - 协议层在传输层之上做：分片重组、按 FrameId 排序、丢帧判定、完整性校验。

#### 6.3.1 帧分片与顺序保证（传输无关）

> **设计原则**：协议层不依赖传输层的有序性/可靠性。无论 TCP/UDP/WebSocket，所有消息统一走分片机制，由协议层自己保证帧的顺序与完整性兜底。传输层只"尽力投递分片字节"。

**为何所有消息都分片（而非仅 UDP）**：
- 统一机制，协议层代码与传输后端解耦——换传输不改协议逻辑。
- 即使 TCP 有序可靠，大帧（可达 50MB）仍需切分以避免单次发送阻塞、控制内存峰值；分片头开销极小（每片 10 字节），对 TCP 路径近乎免费。
- 万一未来换 UDP/WebSocket，协议层无需改动。

**分片格式（所有消息统一，附加在 framing 外层之内、payload 之上）**：

```
分片头（每片前置，小端）：
┌──────────────┬──────────────┬──────────────┬──────────────┬───────────┐
│ FrameId(4B)  │ FragIdx(2B)  │ FragCount(2B)│ CRC16(2B)    │ FragData  │
│ 帧ID(单调)   │ 当前分片序号 │ 总分片数     │ FragData校验 │ (分片字节)│
└──────────────┴──────────────┴──────────────┴──────────────┴───────────┘
```

即完整线格式为：`Magic(1) + Type(1) + PayloadLen(4) + [FrameId(4)+FragIdx(2)+FragCount(2)+CRC16(2)+FragData]`。小消息（如 Keepalive、InputEvent）FragCount=1、FragIdx=0，仍带分片头以保持统一。

- `FrameId`：4 字节 uint32，**发送方自己的**单调递增计数器。同一帧的所有分片共享同一 FrameId。**这是传输层顺序保证的依据**——接收方据此判断新旧、丢弃过期帧。**区别于 `VideoFrameMessage.SequenceNumber`**：FrameId 是传输级分片组 ID（所有消息适用，服务端/客户端各自独立计数），SequenceNumber 是视频 payload 内的应用级帧序号（仅 VideoFrame 消息有，跨帧语义）。两者分别服务于传输可靠性与应用层丢帧检测，不可混用。
- `FragIdx`/`FragCount`：当前分片序号（0 起）/ 总分片数。FragCount=1 表示不分片（整帧一个分片）。
- `CRC16`：本分片 FragData 的校验（2 字节），检测传输损坏（无论传输层是否已校验，协议层独立校验，保证传输无关的完整性兜底）。
- `FragData`：原始 payload 按 FragCount 等分后的第 FragIdx 段。最后一片可较短。
- 分片大小 `FragData` 上限由 `Constants.FragmentSize`（默认 1400 字节，留余量给各传输协议头）控制。

**接收方重组与顺序保证（协议层逻辑，传输无关）**：

1. **按 FrameId 重组**：`MessageReassembler` 维护"当前期望 FrameId"与一个分片缓冲。收到某 FrameId 的分片，若 == 当前期望且 FragIdx 连续，填入缓冲；收齐 FragCount 个分片后，按 FragIdx 顺序拼接为完整 payload，校验通过则通过 `MessageReassembler.MessageReceived` 事件抛给上层（TransportHost/Session），FrameId 推进。
2. **乱序处理**：若收到的 FrameId > 当前期望（新帧的分片先到），且当前帧未收齐——**丢弃当前未完成帧**，转而组装新帧（最新帧优先，实时流语义）。
3. **丢包/超时处理**：当前帧某分片迟迟未到（超时 `Constants.FragmentReassembleTimeoutMs`，默认 100ms），丢弃当前帧部分分片，**不重传、不等待**——等下一帧。远程桌面是实时流，旧帧无价值。
4. **重复分片**：同一 FrameId+FragIdx 重复到达，忽略后到的（幂等）。
5. **CRC16 校验失败**：该分片视为损坏=丢失，按丢包处理（丢整帧）。

**与"可靠重组"的本质区别**：本机制为**实时性牺牲完整性**——丢帧不重传，只保证"最新可用帧"尽快呈现。这是远程桌面的正确语义，与文件传输的可靠重组（重传补齐）相反。若底层是 TCP（天然不丢不乱序），上述逻辑退化为"每帧恰好一个或多个有序分片、无丢包、CRC 恒通过"，开销近乎为零但机制统一。

**握手与控制的可靠性策略**：
- **握手（HandshakeReq/Res）**：小帧（FragCount=1 通常），若传输丢包/超时导致重组失败，**应用层重试兜底**——客户端握手超时未收到响应则重发 HandshakeReq（有限次，如 3 次），而非协议层重传分片。
- **InputEvent / CursorUpdate / Keepalive**：均为小消息（FragCount=1 通常）。`MessageReassembler` 对它们与视频帧一律按 6.3.1 丢帧策略处理（丢即弃不重传）——这是实时流语义的代价。实际影响：输入丢了用户可重新操作；光标丢了下一轮 60Hz 刷新覆盖；Keepalive 丢了服务端心跳检测容忍多轮丢包（30s 无活动才断连，见 6.5.2）。
- **此策略不适合"每消息必须到达"的可靠信令场景——如有此需求 V2 应加入 message-level ACK/重传。V1 以局域网 TCP 为默认传输，丢包极少，当前策略可行。**

#### 校验码策略（传输无关）

- **每个分片带 CRC16**（见分片头），无论传输方式。协议层独立校验分片完整性，不依赖传输层是否已校验——这保证换任何传输后端都有统一的完整性兜底。
- CRC16 而非 CRC32：分片小（默认 1400B），CRC16 足以检测突发错误，计算开销低于 CRC32，适合 XP 弱 CPU 的高频分片场景。
- **校验失败 = 丢整帧**：某分片 CRC16 不符，视为损坏=丢失，按 6.3.1 丢包处理（丢当前帧，不重传）。因整帧可能因任一分片损坏而无法重组，不对整帧单独加校验。
- 小消息（Keepalive/InputEvent，FragCount=1）也带 CRC16，统一无例外。

#### 序列化约定

- **统一二进制序列化**：所有消息 payload 用自定义 `BinaryPacker`（见下方 BinaryPacker 章节，小端、紧凑布局，无字段名），不用 JSON/XML。
- 多字节整数一律小端。
- 字符串（如认证口令）：UTF-8 编码，前缀 2 字节 uint16 长度。
- 字节数组（如 H.264 数据、光标像素）：前缀 4 字节 uint32 长度 + 原始字节。
- `BinaryPacker` 实现须 net40/C#5.0 可用（`BinaryWriter`/`BinaryReader` 已足够，无需第三方库）。

#### BinaryPacker（序列化工具）

```csharp
namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 紧凑二进制序列化器。所有消息 payload 的读写都经它，保证小端、紧凑布局。
    /// net40/C#5.0 可用，内部基于 BinaryWriter/BinaryReader，无第三方依赖。
    /// 用法：new BinaryPacker() → WriteXxx(...) → GetBytes() 序列化；
    ///       BinaryPacker.From(bytes) → ReadXxx(...) 反序列化。
    /// </summary>
    public class BinaryPacker
    {
        private readonly MemoryStream _stream;
        private readonly BinaryWriter _writer;
        private readonly BinaryReader _reader;

        public BinaryPacker()
        {
            _stream = new MemoryStream();
            _writer = new BinaryWriter(_stream);
        }

        private BinaryPacker(byte[] data)
        {
            _stream = new MemoryStream(data);
            _reader = new BinaryReader(_stream);
        }

        public static BinaryPacker From(byte[] data) { return new BinaryPacker(data); }
        public byte[] GetBytes() { return _stream.ToArray(); }

        // —— 写 ——
        public void WriteByte(byte v) { _writer.Write(v); }
        public void WriteInt32(int v) { _writer.Write(v); }       // BinaryWriter 本身小端
        public void WriteUInt32(uint v) { _writer.Write(v); }
        public void WriteInt64(long v) { _writer.Write(v); }
        public void WriteString(string v)                         // uint16 长度前缀 + UTF-8
        {
            byte[] b = System.Text.Encoding.UTF8.GetBytes(v ?? "");
            _writer.Write((ushort)b.Length);
            _writer.Write(b);
        }
        public void WriteBytes(byte[] v)                          // uint32 长度前缀 + 原始字节
        {
            _writer.Write((uint)(v != null ? v.Length : 0));
            if (v != null && v.Length > 0) _writer.Write(v);
        }

        // —— 读 ——
        public byte ReadByte() { return _reader.ReadByte(); }
        public int ReadInt32() { return _reader.ReadInt32(); }
        public uint ReadUInt32() { return _reader.ReadUInt32(); }
        public long ReadInt64() { return _reader.ReadInt64(); }
        public string ReadString()
        {
            int len = _reader.ReadUInt16();
            return len == 0 ? "" : System.Text.Encoding.UTF8.GetString(_reader.ReadBytes(len));
        }
        public byte[] ReadBytes()
        {
            int len = (int)_reader.ReadUInt32();
            return len == 0 ? null : _reader.ReadBytes(len);
        }
    }
}
```

#### 握手消息结构

```csharp
namespace EasyRDP.Core.Protocol
{
    /// <summary>客户端握手请求。</summary>
    public class HandshakeReq
    {
        public byte Version;                 // 协议版本，须 == Constants.ProtocolVersion (0x02)
        public CodecCapabilities Capabilities; // 客户端支持的解码能力
        public string Username;              // 用户名；认证机制见 6.5.1
        public string Password;              // 密码
    }

    /// <summary>服务端握手响应。</summary>
    public class HandshakeRes
    {
        public HandshakeResult Result;
        public CodecId Codec;                // 协商出的编码（Success 时有效）；服务端单边决策，客户端服从
        public int ScreenWidth;              // 服务端当前分辨率（Success 时有效，供客户端预分配）
        public int ScreenHeight;
    }
}
```

#### 输入事件消息结构（`MessageType.InputEvent = 0x05`）

```csharp
namespace EasyRDP.Core.Protocol
{
    /// <summary>输入事件类型。</summary>
    public enum InputEventType : byte
    {
        KeyDown = 1,
        KeyUp = 2,
        MouseMove = 3,
        MouseDown = 4,
        MouseUp = 5,
        MouseWheel = 6
    }

    /// <summary>
    /// 输入事件消息。键盘用 Windows 虚拟键码（VK_*）；
    /// 鼠标坐标为服务端屏幕坐标系，客户端负责按当前服务端分辨率比例映射（见 5.3 IClientInputSession）。
    /// </summary>
    public class InputEventMessage
    {
        public InputEventType Type;
        public int KeyCode;        // 键盘：VK_*；鼠标：按键位掩码（左=1,右=2,中=4）
        public int X;              // 鼠标 X（屏幕坐标）；键盘忽略
        public int Y;              // 鼠标 Y
        public int WheelDelta;     // MouseWheel 时有效
    }
}
```

#### 光标更新消息结构（`MessageType.CursorUpdate = 0x06`）

```csharp
namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 光标更新。RgbaPixels 为 null 表示仅更新位置（光标形状未变）。
    /// 像素为 RGBA8888，宽高由 Width/Height 给出。
    /// </summary>
    public class CursorUpdateMessage
    {
        public bool Visible;
        public int X;
        public int Y;
        public int Width;          // RgbaPixels 非 null 时有效
        public int Height;
        public int HotX;
        public int HotY;
        public byte[] RgbaPixels;  // 可空；非空时长度须 == Width*Height*4
    }
}
```

#### 视频帧消息（`MessageType.VideoFrame = 0x50`）

序列化规则同上（`VideoFrameMessage` 字段见 6.6 节）：`Width`/`Height`/`SequenceNumber`/`IsKeyframe` 为定长字段，`Data` 为 4 字节长度前缀 + H.264 字节。

### 6.4 协议常量

```csharp
namespace EasyRDP.Core.Protocol
{
    public static class Constants
    {
        public const byte ProtocolVersion = 0x02;
        public const byte FrameMagic = 0xE5;            // 消息起始魔术字节，见 6.3
        public const int DefaultPort = 8750;
        public const int MaxFrameSize = 50 * 1024 * 1024; // 50MB
        public const int FragmentSize = 1400;           // 分片 FragData 上限（传输无关，留余量给各协议头），见 6.3.1
        public const int FragmentReassembleTimeoutMs = 100; // 分片重组超时默认值（可被 MessageReassembler 实例构造参数覆盖；TCP 局域网下恒定不超时，UDP/WAN 场景可调大），见 6.3.1
    }
}
```

### 6.5 握手流程

```
Client → Server: HandshakeReq { Version=0x02, Capabilities=[H264Software], Username="admin", Password="..." }
Server → Client: HandshakeRes {
    Result=Success,
    Codec=H264Software,
    ScreenWidth=1920,
    ScreenHeight=1080
}
```

握手失败场景：
- 客户端版本 < 0x02 → `VersionMismatch`
- 双方编码能力交集为空 → `NoCommonCodec`（服务端动态探测后仅广告可实际创建的编码器）
- 认证失败 → `AuthFailed`

### 6.5.1 认证机制

- **认证方式：用户名 + 密码**。`HandshakeReq` 携带 `Username`/`Password`；服务端校验用户名密码对，失败返回 `AuthFailed` 并断连。
- **服务端存储**：服务端维护用户名/密码凭据表（实现层负责，如配置文件、注册表或自定义存储）。抽象层只定义 `HandshakeReq.Username`/`Password` 字段与 `AuthFailed` 结果，不规定存储介质与密码哈希算法。建议实现层存哈希（如 SHA-256）而非明文，校验时比对哈希。
- **明文传输警告**：Username/Password 以 UTF-8 明文置于握手包内。**明文仅适用于可信/内网环境**；跨公网须在传输层加固（TLS 隧道或在 `ITransportClient`/`ITransportServer` 实现层叠加加密），本抽象层不规定具体加密方案。
- **认证状态机**：握手前为未认证，握手 `Success` 后才允许收发 `VideoFrame`/`InputEvent`/`CursorUpdate`/`Keepalive`；未认证收到这些消息类型，服务端直接断连。
- **失败防护**：连续认证失败（如 3 次）可由实现层加入短时封锁/延迟，防暴力破解；抽象层不强制。

### 6.5.2 心跳机制（Keepalive）

- `MessageType.Keepalive = 0x03`，**payload 为空**（仅 framing 外层 Magic+Type+PayloadLen=0，共 6 字节）。双向：客户端与服务端均可主动发送 ping，对端收到后立即回 pong（同为空 payload 的 Keepalive 消息）。
- **服务端检测**（防僵尸 Session，#3）：`TransportHost` 为每个 Session 维护 `LastActivity` 时间戳，收到该 Session 任何消息（含 Keepalive/VideoFrame/InputEvent）即刷新。定时器（默认 10s 间隔）扫描：若某 Session 超过 30s 无活动，主动发送 Keepalive ping 探活；再等 15s 仍无响应则视为死连接，触发 C5 断连联动（Stop+Dispose）。
- **客户端检测**：`IClientStreamSession` 对称实现——`ITransportClient` 30s 无数据则发 ping，15s 无 pong 则触发 `FatalError`（D4）供上层重连。
- 选取 30s/15s 而非更短：避免弱网/慢编码导致的正常延迟误判为断连；TCP 自身的 keepalive 通常更长（2h），应用层心跳必须更早介入。

### 6.6 视频帧消息

```csharp
namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// H.264 编码后的视频帧。Width/Height 在分辨率变更时会变化，
    /// 客户端应据此检测并适配渲染尺寸。
    /// </summary>
    public class VideoFrameMessage
    {
        /// <summary>帧宽度（像素）。</summary>
        public int Width;

        /// <summary>帧高度（像素）。</summary>
        public int Height;

        /// <summary>H.264 编码数据。</summary>
        public byte[] Data;

        /// <summary>是否为 IDR 关键帧。</summary>
        public bool IsKeyframe;

        /// <summary>帧序号（服务端单调递增）。</summary>
        public long SequenceNumber;
    }
}
```

### 6.7 分辨率变更

服务端分辨率变更时（用户改分辨率或 D11 自适应降级），下一条 `VideoFrameMessage` 的 `Width`/`Height` 字段反映新尺寸，且该帧必定为 IDR 关键帧（`forceKeyframe=true`）。

客户端闭环：
- **视频侧（C6）**：`IClientStreamSession` 接收线程检测到 `Width`/`Height` 变化 → `IVideoDecoder.Reset()`+`Initialize` → `IRenderTarget.Resize`。
- **输入侧（D5）**：同一接收线程检测到变化后调用 `IClientInputSession.OnResolutionChanged(newW, newH)`，输入会话更新鼠标坐标映射比例，避免分辨率变化后鼠标错位。

### 6.8 消息字节级布局（Payload 精确格式）

> 以下为各消息 **payload**（最内层，去 framing 外层 Magic/Type/PayloadLen 与分片头 FrameId/FragIdx/FragCount/CRC16 后）的精确字节布局。
> 即：完整线格式 = framing 外层 + 分片头 + payload（见 6.3/6.3.1）；下表仅描述 payload。
> 字段按表中**从上到下顺序**序列化；所有多字节整数小端（LE）。编码者照表即可写 pack/unpack。

#### HandshakeReq（Type=0x01）

| 偏移 | 字段 | 类型 | 字节 | 说明 |
|---|---|---|---|---|
| 0 | Version | uint8 | 1 | 须 == 0x02 |
| 1 | Capabilities | uint8 | 1 | CodecCapabilities 位掩码 |
| 2 | UsernameLen | uint16 LE | 2 | UTF-8 字节数 |
| 4 | Username | byte[] | UsernameLen | UTF-8 编码用户名 |
| 4+UsernameLen | PasswordLen | uint16 LE | 2 | UTF-8 字节数 |
| 6+UsernameLen | Password | byte[] | PasswordLen | UTF-8 编码密码 |

Payload 长度 = 6 + UsernameLen + PasswordLen。

#### HandshakeRes（Type=0x02）

| 偏移 | 字段 | 类型 | 字节 | 说明 |
|---|---|---|---|---|
| 0 | Result | uint8 | 1 | HandshakeResult 枚举 |
| 1 | Codec | uint8 | 1 | CodecId 枚举（Success 时有效） |
| 2 | ScreenWidth | int32 LE | 4 | 服务端主屏宽（Success 时有效） |
| 6 | ScreenHeight | int32 LE | 4 | 服务端主屏高 |

Payload 长度 = 10（定长）。

#### Keepalive（Type=0x03）

| 偏移 | 字段 | 字节 | 说明 |
|---|---|---|---|
| — | （无） | 0 | payload 为空，仅 framing 外层 6 字节 |

ping/pong 同此格式，靠收到即回实现。

#### InputEvent（Type=0x05）

| 偏移 | 字段 | 类型 | 字节 | 说明 |
|---|---|---|---|---|
| 0 | Type | uint8 | 1 | InputEventType 枚举 |
| 1 | KeyCode | int32 LE | 4 | 键盘 VK_* / 鼠标按键位掩码 |
| 5 | X | int32 LE | 4 | 鼠标屏幕坐标 X |
| 9 | Y | int32 LE | 4 | 鼠标屏幕坐标 Y |
| 13 | WheelDelta | int32 LE | 4 | MouseWheel 时有效，其余 0 |

Payload 长度 = 17（定长）。

#### CursorUpdate（Type=0x06）

| 偏移 | 字段 | 类型 | 字节 | 说明 |
|---|---|---|---|---|
| 0 | Visible | uint8 | 1 | 0/1 |
| 1 | X | int32 LE | 4 | 光标 X |
| 5 | Y | int32 LE | 4 | 光标 Y |
| 9 | Width | int32 LE | 4 | 像素宽（RgbaPixels 非空时有效） |
| 13 | Height | int32 LE | 4 | 像素高 |
| 17 | HotX | int32 LE | 4 | 热区 X |
| 21 | HotY | int32 LE | 4 | 热区 Y |
| 25 | RgbaLen | uint32 LE | 4 | RgbaPixels 字节数；0 表示仅更新位置 |
| 29 | RgbaPixels | byte[] | RgbaLen | RGBA8888，长度须 == Width*Height*4 |

Payload 长度 = 29 + RgbaLen。

#### VideoFrame（Type=0x50）

| 偏移 | 字段 | 类型 | 字节 | 说明 |
|---|---|---|---|---|
| 0 | Width | int32 LE | 4 | 帧宽 |
| 4 | Height | int32 LE | 4 | 帧高 |
| 8 | IsKeyframe | uint8 | 1 | 0/1 |
| 9 | SequenceNumber | int64 LE | 8 | 单调递增帧序号 |
| 17 | DataLen | uint32 LE | 4 | H.264 数据字节数 |
| 21 | Data | byte[] | DataLen | H.264 编码字节 |

Payload 长度 = 21 + DataLen。

#### 序列化顺序约定

- 所有消息字段按上表**声明顺序**逐一写入 `BinaryPacker`；反序列化同序读取。
- 字节数组一律 uint32 长度前缀（视频/光标像素）或 uint16 长度前缀（字符串），长度为 0 时写 0 不写数据。
- 定长消息（HandshakeRes/InputEvent）便于预分配 buffer；变长消息（HandshakeReq/CursorUpdate/VideoFrame）按长度前缀动态读。

---

## 7. 实现计划

| 阶段 | 内容 | 验证 |
|---|---|---|
| P1 | `IVideoDecoder` + `DecoderFactory` + `ICursorTracker`/`ICursorTrackerSession` 接口及适配 | 编译通过 |
| P2 | 协议清理：移除 Bitmap 路径代码，版本升至 0x02，新增 `NoCommonCodec`；**Framing 规范（6.3）+ 各消息结构 + 序列化 + `HandshakeRes` 分辨率字段**（前置，供 P4 使用）；认证字段（6.5.1） | 编译通过 |
| P3 | `FrameBuffer` 下沉到 `Core/Rendering`，零拷贝双槽重写，`ScreenRect` 迁移 | 编译通过 |
| P4 | `IRenderTarget` + `CursorInfo`，`WpfRenderTarget` 实现。**强制集成验证**：`MessageDispatcher` 临时串联 `VideoFrameMessage → Decoder → FrameBuffer → WpfRenderTarget`（见下方代码模板） | 客户端实际渲染视频流 |
| P5 | `ICaptureService` + `CaptureService`（含 D10 抓屏后端选择）；`IServerStreamSession` + `IServerInputSession` 接口及实现（含 D11 自适应降级、A1 三线程模型、D12 并发上限+全局负载感知）；`TransportHost`（含握手/心跳检测/Stop 防竞态）；动态编码器探测；`CapturedFrame` 双缓冲复用 | 服务端全功能不回归 |
| P6 | `IClientStreamSession` + `IClientInputSession` 接口定义（不实现） | 编译通过 |
| P7 | 端到端验证 | WPF 全链路跑通 |

P4 和 P5 为高风险阶段，单独验证。

### P4 强制集成验证代码

```csharp
// 1. 初始化（握手回调中执行一次）
// P4 在 MessageDispatcher 单线程中执行；FrameBuffer 的并发安全逻辑此阶段不会被真实测试，留待 P7。
// P4 跑在 net8.0-windows 客户端项目（可用 C#7+ 语法）；服务端/XP 路径走 net40 + C#5.0。
private IVideoDecoder _decoder;
private FrameBuffer _frameBuffer;
private WpfRenderTarget _renderTarget;
private int _curW, _curH;   // 当前已知分辨率，初始 0；InitRenderPipeline/OnVideoFrame 中维护

public void InitRenderPipeline(CodecId negotiatedCodec, int width, int height)
{
    // width/height 来自 HandshakeRes.ScreenWidth/ScreenHeight（P2 已前置该字段）。
    // P4 阶段握手尚未全打通，可临时用硬编码/命令行传入，P7 端到端时改读握手结果。
    _decoder = DecoderFactory.Create(negotiatedCodec);
    if (_decoder == null)
        throw new InvalidOperationException("No decoder for " + negotiatedCodec);
    _decoder.Initialize(width, height);
    _frameBuffer = new FrameBuffer();
    _renderTarget = new WpfRenderTarget(/* platform render control */);
    _renderTarget.Resize(width, height);
    _curW = width; _curH = height;
}

// 2. 收到 VideoFrameMessage
public void OnVideoFrame(VideoFrameMessage msg)
{
    // 分辨率变更：解码器 Reset + 重新 Initialize + 强制关键帧（P4 单线程简化，P7 在编排层闭环）
    if (msg.Width != _curW || msg.Height != _curH)
    {
        _decoder.Reset();
        _decoder.Initialize(msg.Width, msg.Height);
        _renderTarget.Resize(msg.Width, msg.Height);
        _curW = msg.Width; _curH = msg.Height;
    }

    int frameSize = msg.Width * msg.Height * 4;
    byte[] writeSlot = _frameBuffer.BorrowWriteBuffer(frameSize);
    if (writeSlot == null) return;

    // L3 优化：解码直接写入 FrameBuffer 槽位，省去一次 BlockCopy
    DecodeResult r = _decoder.Decode(msg.Data, writeSlot);
    if (r.Status != DecodeStatus.Ok) return;  // NeedMoreInput/Failed 统一跳过

    if (!_frameBuffer.CommitFrame(msg.Width, msg.Height)) return;

    ReadFrameRef frame;
    if (_frameBuffer.TryBorrowReadFrame(out frame))
    {
        try { _renderTarget.RenderFrame(frame.Pixels, frame.Width, frame.Height); }
        finally { _frameBuffer.ReleaseReadFrame(); }
    }
}

// 3. 光标
public void OnCursorUpdate(int x, int y, bool visible, byte[] rgbaPixels,
    int cursorW, int cursorH, int hotX, int hotY)
{
    _renderTarget.UpdateCursor(new CursorInfo {
        Visible = visible, X = x, Y = y,
        RgbaPixels = rgbaPixels, Width = cursorW, Height = cursorH,
        HotX = hotX, HotY = hotY
    });
}

// 4. 清理
public void CleanupRenderPipeline()
{
    _decoder?.Dispose(); _decoder = null;
    _frameBuffer?.Reset(); _frameBuffer = null;
    _renderTarget?.Dispose(); _renderTarget = null;
}
```

> **⚠️ 限制（P4 与 P7 的关系）**：
> - P4 在 MessageDispatcher 单线程中执行，FrameBuffer 的并发安全逻辑（`BorrowWriteBuffer`→null、`CommitFrame`→false）不会被真实测试。P7 端到端时覆盖。P4 验证的是接口签名和像素推屏。
> - P4 用临时串联形态跑通；P7 落地 `IClientStreamSession` 编排时，接收/解码/渲染将被拆到独立双线程（D8 客户端模型），**P4 的串联代码不直接复用、需按编排重写**。P4 通过 ≠ 编排设计正确，P7 仍须独立验证并发与线程模型。

---

## 8. 风险与缓解

| 风险 | 缓解 |
|---|---|
| 无流控 → 客户端被帧淹没 | D8 + D9：有界队列满时跳帧，`PendingFrames` 暴露监控 |
| 握手成功但编码器创建失败 | 动态探测：`EncoderFactory.Create` 实测可用性 |
| 初始分辨率无从获取 | `HandshakeRes` 携带 `ScreenWidth`/`ScreenHeight` |
| 多客户端截屏线程安全 | D9 单捕获多编码：全局单例截屏线程 |
| 1080p@30fps 内存分配压力 | `FrameBuffer` 双槽零拷贝，全链路 2 次数据搬运（解码器→写槽、读槽→GPU），L3 省去中间 BlockCopy，无额外 `byte[]` 分配 |
| P4 拆分为高风险阶段 | 强制集成验证 + 代码模板，禁止跳过；P4→P7 编排须重写（见 P4 限制说明） |
| `IVideoDecoder` 接口 net40 可见但无实现 | 已修正：net40 走原生 P/Invoke 后端（`H264DecoderNative`），与编码器对称，非"无实现" |
| **XP 服务端无法编码 H.264** | 已修正：net40 必须有 `H264EncoderNative`（libx264/OpenH264），抽象层契约保证 Factory 在 net40 返回可用实例 |
| **net40 误升 4.5+ → XP 失效** | 第 4 节前言红线：服务端/XP 客户端项目 target 锁 net40，禁止升号 |
| **XP 无 DXGI，抓屏慢** | D10：BitBlt + 镜像驱动双后端运行期选择 |
| **XP 时代 CPU 跑不动 1080p 软编** | D11：按编码耗时自适应降分辨率/降 fps；丢帧（D8）只防积压，D11 才是流畅杠杆 |
| **截屏线程串行编码致帧率塌陷** | A1 修正：截屏回调只入队，每 Session 独立编码线程并行 |
| **TCP 无 framing 无法切分消息** | 6.3：Magic+Type+PayloadLen length-prefix，传输层负责装/拆 |
| **Stop 无超时，发送线程挂死** | Stop 带 Join 超时，超时放弃等待（实现要点 + 生命周期契约 D6） |
| **客户端断连后 Session 残留** | C5：TransportHost 订阅 ClientDisconnected，销毁对应 Session |
| **客户端分辨率变更未闭环** | C6：IClientStreamSession 接收线程统一处理 Reset+Resize |
| **D11 降分辨率后鼠标错位** | D5：StreamSession 检测 VideoFrame 尺寸变化后调 IClientInputSession.OnResolutionChanged，更新坐标映射 |
| **客户端解码故障无上报路径** | D4：IClientStreamSession 补 FatalError 事件，与服务端对称 |
| **生命周期/Dispose 契约不明** | D6：Dispose 幂等、显式 Stop 优先、释放非托管资源、用后抛 ObjectDisposedException |
| **GetAvailableCodecs 位运算耦合** | D1：改显式 switch 映射，CodecId 与 CodecCapabilities 解耦 |
| **CodecNegotiator/BinaryPacker/ErrorEventArgs/CapturedFrame 未定义** | C1–C4：均已补完整定义（6.1/6.3/Session 区） |
| **net40 32 位 long 撕裂** | C1：FrameBuffer 的 Sequence/FrameCount 读取加锁 |
| **认证明文泄露** | 6.5.1：内网可用明文；跨公网须传输层叠加 TLS/加密 |
| **编码层泄漏协议类型** | H1 修正：Encode 返回中性 EncodedFrame，编排层负责包装 VideoFrameMessage |
| **属性运行时不可改与自适应冲突** | H2/H3 修正：TargetBitrate/KeyframeInterval/FrameDelayMs 运行时可改，供 D11 动态调整 |
| **光标全局控制被单 Session 滥用** | H4/H5 修正：ICursorTracker 拆全局/会话级，会话级仅控本 Session |
| **传输层 framing 契约不清** | M3 修正：Send 契约为"已 framing 完整消息"，EventArgs/LogCallback 显式定义 |
| **FrameBuffer dirty 死代码分配** | M4 修正：移除 _dirtyList，纯 H.264 整帧路径无脏区 |
| **编解码状态机不明** | L2 修正：构造→Initialize→Encode/Decode→Reset→重新 Initialize，契约显式 |
| **N 客户端线程爆炸（2N+1）** | D12：并发上限（默认 ≤2，超限回 ServerBusy）+ 全局负载感知（TransportHost 汇总编码耗时，过载时通知所有 Session 同步降级） |
| **Stop 竞态：Encode 中间 Dispose 崩溃** | #2：_stopping 标志 + 超时不立即 Dispose 编码器，标记待清理延迟回收 |
| **无心跳致僵尸 Session 空转** | #3：MessageType.Keepalive ping/pong，30s 无活动探活、15s 无响应断连（6.5.2） |
| **CapturedFrame 每帧 ×N 分配致 LOH 碎片** | #4：Session 内双缓冲复用 byte[]，仅分辨率变更时重分配 |
| **多 Session 发送饿死** | #5：ITransportServer.SendTo 契约要求直接写各 Socket 或公平调度 |
| **多显示器不支持** | #6：V1 标注仅主屏，IScreenCapturer 预留 GetAllScreens 供 V2 |
| **FrameBuffer 读帧未释放致管线卡死** | #7：CommitFrame 检测读帧借用超 5s 强制回收 |
| **协议无字节级布局** | 6.8：补全各消息 payload 精确字段表，照表可写 pack/unpack |
| **大帧投递/乱序/丢包** | 6.3.1：所有消息统一分片（FrameId/FragIdx/FragCount/CRC16），协议层按 FrameId 重组、乱序丢包丢整帧不重传（实时流语义），与传输方式无关 |
| **协议与传输方式耦合** | 6.3 传输无关原则：协议层定义帧完整语义，传输层只"尽力投递分片"，换 TCP/UDP/WebSocket 协议逻辑不变 |
| **传输损坏检测** | 校验码策略：每分片带 CRC16（传输无关），校验失败丢整帧；不依赖传输层是否已校验 |
| **单口令认证粒度不足** | 6.5.1：改用户名+密码，服务端存凭据表（建议哈希），支持多用户；明文仅内网，跨公网须 TLS |
| **分片重组无归属类/开发者无从下手** | 4.3.1 MessageReassembler：传输层与 Session 间的桥接类，订阅 DataReceived 分片→重组→抛 MessageReceived 完整消息；发送侧 FragAndSend 分片封装 |
| **传输层事件与重组输出同名 (MessageReceived)** | v2.7 修正：传输层改名 DataReceived → FragmentReceivedEventArgs；重组器抛出 MessageReceived → MessageReceivedEventArgs，层次分明 |
| **FrameId 与 SequenceNumber 语义重叠** | 6.3.1 澄清：FrameId 是传输级分片组 ID（所有消息适用），SequenceNumber 是视频 payload 内应用级帧序号（仅 VideoFrame），不可混用 |
| **双 └── 树结构损坏 + 缺新文件** | v2.7 修正目录树：Session 下双 └── 修复；Transport 下新增 MessageReassembler.cs；Session 下补 CapturedFrame.cs/ErrorEventArgs.cs |

