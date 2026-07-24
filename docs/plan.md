# EasyRDP 详细实施计划

> 依据 `docs/EasyRDP-Abstraction-Layers-Design.md` v2.7，自底向上逐阶段实现五层抽象 + 编排层。
> 每个阶段结束时必须编译通过；P4/P5/P7 有强制集成验证。

---

## 1. 项目脚手架与接口骨架

- 创建 `EasyRDP.sln` 解决方案，包含三个项目：
  - `src/EasyRDP.Core/EasyRDP.Core.csproj` — 多目标 `net40;netstandard2.0`，C# 5.0 约束，零外部 NuGet 依赖
  - `src/EasyRDP.Client.Wpf/EasyRDP.Client.Wpf.csproj` — `net8.0-windows`，引用 Core
  - `src/EasyRDP.Server.Wpf/EasyRDP.Server.Wpf.csproj` — `net8.0-windows`，引用 Core + EasyDesk（子模块）
- 在 Core 中建立命名空间骨架：
  - `EasyRDP.Core.Protocol` — 编解码抽象、协议消息、序列化
  - `EasyRDP.Core.Transport` — ITransportClient/Server、MessageReassembler
  - `EasyRDP.Core.Services` — ICaptureService
  - `EasyRDP.Core.Rendering` — FrameBuffer、IRenderTarget、CursorInfo
  - `EasyRDP.Core.Session` — 编排层会话接口
- 定义基础枚举：
  - `CodecId`（H264Software=1, H264Hardware=2）
  - `CodecCapabilities` 标志枚举（None=0, H264Software=1<<0, H264Hardware=1<<1）
- 实现 `IVideoEncoder` 接口及 `EncoderFactory`（net40 预留 `H264EncoderNative` 占位）
- 实现 `IVideoDecoder` 接口（含 `DecodeResult`/`DecodeStatus`）及 `DecoderFactory`
- 实现 `ICursorTracker` 全局接口 + `ICursorTrackerSession` 会话级接口
- 验证：`dotnet build src/EasyRDP.Core/EasyRDP.Core.csproj` 编译通过

---

## 2. 协议层基础设施

- 定义协议枚举：
  - `MessageType`（HandshakeReq=0x01, HandshakeRes=0x02, Keepalive=0x03, InputEvent=0x05, CursorUpdate=0x06, VideoFrame=0x50）
  - `HandshakeResult`（Success/AuthFailed/VersionMismatch/ServerBusy/NoCommonCodec/InternalError）
  - `InputEventType`（KeyDown/Up, MouseMove/Down/Up/Wheel）
- 定义 `Constants` 静态类（ProtocolVersion=0x02, FrameMagic=0xE5, MaxFrameSize=50MB, FragmentSize=1400, FragmentReassembleTimeoutMs=100）
- 实现 `BinaryPacker` 完整序列化器（WriteByte/Int32/UInt32/Int64/String/Bytes + Read，全小端，基于 `BinaryWriter`/`BinaryReader`）
- 实现所有消息类 + Pack/Unpack 方法（严格按设计文档 6.8 节字节级布局）：
  - `HandshakeReq`（Version, Capabilities, Username, Password）
  - `HandshakeRes`（Result, Codec, ScreenWidth, ScreenHeight）
  - `InputEventMessage`（Type, KeyCode, X, Y, WheelDelta）
  - `CursorUpdateMessage`（Visible, X, Y, Width, Height, HotX, HotY, RgbaPixels）
  - `VideoFrameMessage`（Width, Height, IsKeyframe, SequenceNumber, Data）
- 实现 `EncodedFrame` 结构体（Data, IsKeyframe, Width, Height）
- 实现 `CodecNegotiator` 握手编码协商（取交集，H264Hardware > H264Software 优先级）
- 实现 framing 外层辅助方法：`BuildWireHeader` / `TryParseWireHeader`（Magic+Type+PayloadLen）
- 验证：编译通过 + 单元测试覆盖
  - `BinaryPacker` 所有消息类 pack/unpack 往返一致性
  - `CodecNegotiator.Negotiate` 各种能力组合的正确输出
  - `HandshakeReq`/`HandshakeRes` 序列化字节与 6.8 节偏移表对照

---

## 3. FrameBuffer 渲染逻辑层

- 在 `EasyRDP.Core/Rendering/` 下实现 `FrameBuffer` 类：
  - 双槽零拷贝（`byte[2][]`），所有操作 `lock` 保护
  - `BorrowWriteBuffer(int requiredSize)` — 返回写槽引用，读帧占用时返回 null
  - `CommitFrame(int width, int height)` — 原子交换双槽；读帧超时 5s 强制回收
  - `TryBorrowReadFrame(out ReadFrameRef)` — 返回读帧引用（结构体，含 Pixels/Width/Height/Sequence）
  - `ReleaseReadFrame()` — 释放读帧标记
  - `Reset()` — 恢复初始状态
  - 属性（Width/Height/FrameCount/Sequence）读取均加锁，防 net40 32 位进程 long 撕裂
- 实现 `ReadFrameRef` 结构体（Pixels, Width, Height, Sequence）
- 实现 `CursorInfo` 结构体（Visible, X, Y, RgbaPixels, Width, Height, HotX, HotY）
- 迁移 `ScreenRect` 结构体到 `Core/Rendering/`（V1 作预留，dirty 机制已移除）
- 验证：编译通过 + 单元测试
  - 单线程：写→提交→读→释放 正常流程
  - 并发：生产者线程写、消费者线程读，无崩溃无死锁
  - 读帧超时：模拟渲染层不释放，CommitFrame 5s 后强制回收成功
  - 写槽满（读帧未释放且未超时）：BorrowWriteBuffer 返回 null、CommitFrame 返回 false

---

## 4. 客户端渲染管线（⚠️ 高风险，强制集成验证）

- 在 Core 中实现 `IRenderTarget` 接口（`RenderFrame(byte[],int,int)`, `UpdateCursor(CursorInfo)`, `Resize(int,int)`, `IDisposable`）
- 在 `EasyRDP.Client.Wpf` 中实现 `WpfRenderTarget`：
  - 构造注入 WPF `Image` 控件引用
  - `RenderFrame` → `WriteableBitmap.WritePixels` 推 BGRA32 像素
  - `UpdateCursor` → WPF Canvas/Adorner 叠加光标层
  - `Resize` → 重建 `WriteableBitmap` 到新尺寸
- 在 `EasyRDP.Client.Wpf` 中创建 `MessageDispatcher` 类，**单线程**串联验证管线：
  - `InitRenderPipeline(CodecId, width, height)` → 创建 Decoder + FrameBuffer + WpfRenderTarget
  - `OnVideoFrame(VideoFrameMessage)` → 解码直写 FrameBuffer → Commit → Render
  - `OnCursorUpdate(...)` → IRenderTarget.UpdateCursor
  - `CleanupRenderPipeline()` → Dispose 全部资源
  - 分辨率变更检测：VideoFrame 尺寸变化 → Decoder.Reset+Initialize → RenderTarget.Resize
- **强制集成验证**：
  - 用预先录制的 H.264 测试数据（或硬编码测试帧）驱动 `MessageDispatcher`
  - 在 WPF 窗口中实际渲染出视频，确认像素推屏链路正确
  - 验证光标叠加
- 验证标准：编译通过 + WPF 窗口实际渲染成功

---

## 5. 传输层

- 在 Core 中实现传输接口与支撑类：
  - `ITransportClient` 接口（`Connect`/`Disconnect`/`Send`/`IsConnected` + `DataReceived`/`Disconnected` 事件 + `LogCallback`）
  - `ITransportServer` 接口（`Start`/`Stop`/`SendTo`/`Disconnect` + `ClientConnected`/`ClientDisconnected`/`DataReceived` 事件 + `LogCallback`）
  - `FragmentReceivedEventArgs`（SessionId, Data）
  - `ConnectionEventArgs`（SessionId, RemoteEndPoint）
  - `LogCallback` 委托（`delegate void LogCallback(string message)`）
- 实现 `MessageReassembler` 类（4.3.1 节，完整消息重组桥接层）：
  - **接收侧**：`OnFragment(FragmentReceivedEventArgs)` — 解析分片头 → CRC16 校验 → 按 FrameId 分组 → 收齐全部分片后抛 `MessageReceived` 事件
  - **发送侧**：`FragAndSend` 静态方法 — payload 切分片 → 每片加 framing 外层+分片头+CRC16 → 逐片写 `sendAction`
  - 乱序/丢包策略：FrameId > 当前期望且当前帧未收齐 → 丢弃旧帧组装新帧（最新帧优先）；超时 `FragmentReassembleTimeoutMs` 未收齐 → 丢整帧
  - CRC16 实现：XMODEM 多项式（`0x1021`）
- 在 `EasyRDP.Server.Wpf` 中实现 `TcpTransportServer`（`ITransportServer`）：
  - `TcpListener` Accept 循环，为每个连接分配递增 `sessionId`
  - 接收：异步读 Socket → 按 Magic+Type+PayloadLen 切分 → 触发 `DataReceived`
  - 发送：`SendTo(sessionId, data)` → 找对应 Socket → 写字节（公平性：每 Session 独立 Socket，无全局锁）
- 在 `EasyRDP.Client.Wpf` 中实现 `TcpTransportClient`（`ITransportClient`）：
  - `Connect(host, port, timeoutMs)` → `TcpClient.ConnectAsync`（net8.0）
  - 接收循环同服务端
  - `Send(data)` → 写入 `NetworkStream`
- 验证：编译通过 + TCP 回环测试
  - localhost 收发，验证分片→MessageReassembler 重组→完整消息正确
  - 大帧（>FragmentSize）分片/重组正确
  - CRC16 校验失败→丢帧正确

---

## 6. 服务端全功能（⚠️ 高风险，强制集成验证）

- 实现 `ICaptureService` 接口 + `CaptureService` 类（`EasyRDP.Server.Wpf`）：
  - 构造注入 `IScreenCapturer`（来自 EasyDesk）
  - 独立截屏线程：`Thread.Sleep(FrameIntervalMs)` → `CaptureScreen()` → 同步 `FrameCaptured?.Invoke(frame)` → `FreeHGlobal(Scan0)`
  - D10 运行期探针：启动时检测镜像驱动是否安装，已装则用镜像驱动，未装回退 BitBlt（对上层透明）
  - `GetPrimaryScreen()` 通过 `IScreenCapturer.GetPrimaryScreen()` 获取
- 实现 `FrameToSend` 和 `CapturedFrame` 结构体（`Core.Session`）：
  - `CapturedFrame`：Pixels 双缓冲复用（两个 `byte[]` 槽交替），仅分辨率变更时重分配；含 Width/Height/CaptureTimestamp
  - `FrameToSend`：Data, IsKeyframe, SequenceNumber, CaptureTimestamp
- 实现 `IServerStreamSession` 接口 + `ServerStreamSession` 类：
  - **构造注入**（L1 原则）：`ICaptureService`、`Action<uint, byte[]> sendTo`、`ICursorTracker`
  - `Start`：
    - 创建两级有界队列：`_frameQueue`（容量默认 2）、`_sendQueue`（容量默认 2）
    - net40 路径用 `Queue<T>`+`Monitor` 代替 `BlockingCollection<T>`
    - 启动编码线程 + 发送线程（每个 Session 独立，真"单捕获多编码"）
    - 订阅 `FrameCaptured` 事件
    - 从全局 `CursorTracker` 派生 `CursorTrackerSession`，调 `AttachSendTo` + `Start`
  - **截屏回调**（截屏线程中，必须极快返回）：
    - 拷贝 `ScreenFrame.Scan0` 到 `CapturedFrame` 私有双缓冲 → `_frameQueue.TryAdd`，满则跳帧
    - ⚠️ 禁止在回调内编码
    - 内存复用：双缓冲交替使用，仅分辨率变更时重新分配 `byte[]`
  - **编码线程**：
    - 出队 `CapturedFrame` → 分辨率变更检测（`Reset`+`Initialize`+`forceKeyframe=true`）
    - `Encode(pixels, forceKeyframe)` → 得到 `EncodedFrame`
    - 包装为 `VideoFrameMessage`（填 `SequenceNumber` 等协议字段）
    - 构造 `FrameToSend` → `_sendQueue.TryAdd`，满则跳帧
  - **发送线程**：
    - 出队 `FrameToSend` → `BinaryPacker` 序列化 payload
    - `MessageReassembler.FragAndSend(frameId, messageType, payload, sendTo, sessionId)` 分片发送
  - D11 自适应降级（编码线程内）：
    - 滑动窗口统计 `Encode()` 实测耗时（窗口 30 帧）
    - 超阈值：降分辨率（1080p→720p→540p）/ 增 `FrameDelayMs` / 调 `TargetBitrate`，触发 `forceKeyframe`
    - 持续达标：逐步回升
  - D12 全局负载感知：暴露 `ApplyGlobalLoadLevel(int level)` 供 TransportHost 下发降级指令
  - `Stop`（防竞态）：
    - 设 `_stopping` 标志（编码/发送线程每轮循环顶部检查）
    - 退订 `FrameCaptured` → `CursorTrackerSession.Stop()`
    - `_frameQueue` + `_sendQueue` 标记完成（`CompleteAdding` 等价）
    - Join 编码线程 + 发送线程，带 3s 超时
    - 超时处理：编码线程可能卡在 `Encode()` 内部（原生库无法中断），不立即 Dispose 编码器，标记"待清理"由 TransportHost 延迟回收
  - 属性 `FrameDelayMs`/`KeyframeInterval`/`TargetBitrate`/`FrameQueueCapacity`/`SendQueueCapacity` 运行时可改

- 实现 `IServerInputSession` 接口 + `ServerInputSession`：
  - `HandleInput(InputEventMessage)` → 调用 `IInputSimulator`（EasyDesk）执行键盘/鼠标操作
  - 事件驱动同步调用，无独立线程

- 实现 `TransportHost` 类：
  - 持有全局 `ICaptureService` + `ITransportServer` + `ICursorTracker`
  - 管理所有活跃 Session 的字典（`Dictionary<uint, SessionPair>`）
  - **握手处理**：
    - 为每个新连接创建 `MessageReassembler` 实例
    - 订阅 `ITransportServer.DataReceived`，过滤该 SessionId 的分片喂给 Reassembler
    - Reassembler 的 `MessageReceived` 事件 → 解析 `HandshakeReq`
    - 校验版本（`==0x02`）+ 认证（用户名+密码）
    - `CodecNegotiator.Negotiate(clientCaps, EncoderFactory.GetAvailableCodecs())`
    - 协商成功 → 回 `HandshakeRes(Success, codec, screenW, screenH)` → 创建 Session
    - 协商失败 → 回 `NoCommonCodec`；认证失败 → 回 `AuthFailed`；版本不匹配 → 回 `VersionMismatch`
  - **并发上限（D12）**：活跃 Session 计数，默认上限 2（XP 双核实测安全值，可配）。超限新连接回 `ServerBusy`
  - **心跳检测**：定时器（10s），扫描超 30s 无活动的 Session 发 Keepalive ping；再 15s 无响应触发断连
  - **断连联动（C5）**：订阅 `ITransportServer.ClientDisconnected`，找到对应 Session → `Stop()` + `Dispose()` → 从字典移除
  - **全局负载感知（D12）**：周期性汇总各 Session 编码耗时统计，过载时全体调 `ApplyGlobalLoadLevel(1/2)`，恢复时调 `0`
  - 停机：遍历所有 Session → `Stop()` → `Dispose()` → 再 `Stop` CaptureService + TransportServer + CursorTracker
  - 待清理编码器列表（Stop 超时未能 Join 的编码器），进程退出时回收

- 实现 `CursorTracker` 全局类（实现 `ICursorTracker`）：
  - 独立 60Hz 线程轮询 `ICursorCapturer`（EasyDesk）
  - 为每个 Session 派生 `CursorTrackerSession`（实现 `ICursorTrackerSession`），注入 `sendTo` 回调
  - 光标位置/形状变化时发送 `CursorUpdateMessage`
  - `Stop()` 结束 60Hz 线程；`StopAll()` 停止所有 Session 的光标追踪

- **强制集成验证**：
  - 加载 EasyDesk，启动服务端，验证 `CaptureService` 正常截屏、`FrameCaptured` 事件触发
  - 启动 TransportHost，验证握手流程、Session 创建
  - 用测试客户端连接，验证视频帧编码→分片→发送完整链路

---

## 7. 客户端编排层 + 端到端集成

- 在 Core 中定义 `IClientStreamSession` 和 `IClientInputSession` 接口（设计文档 5.3 节）
- 实现 `ErrorEventArgs` 类（Message + Exception）
- 实现 `ClientStreamSession` 类（`EasyRDP.Client.Wpf`）：
  - **构造注入**：`ITransportClient`（由 `Start(transport)` 传入）
  - 双线程模型：
    - **接收线程**：TransportClient `DataReceived` → `MessageReassembler` → `MessageReceived` → 解析消息类型
      - `VideoFrame`：检测分辨率变化 → `Decoder.Reset+Initialize` → `RenderTarget.Resize` → 通知 `ClientInputSession.OnResolutionChanged`
      - 解码：`Decoder.Decode(msg.Data, writeSlot)` 直写 FrameBuffer 槽
      - `FrameBuffer.CommitFrame` → 原子交换
    - **渲染线程**：`FrameBuffer.TryBorrowReadFrame` → `RenderTarget.RenderFrame` → `ReleaseReadFrame`
  - `FatalError` 事件（解码 native 故障 `IsAvailable=false`、传输断连、解码连续失败达阈值）
  - 属性：Codec, FrameWidth, FrameHeight, FrameCount
  - `Stop`：停止线程 + Dispose Decoder + Reset FrameBuffer + Dispose RenderTarget

- 实现 `ClientInputSession` 类（`EasyRDP.Client.Wpf`）：
  - `Start(transport, screenWidth, screenHeight)`：注册 WPF 鼠标/键盘事件监听
  - 输入事件 → 构造 `InputEventMessage`（坐标按 screenWidth/screenHeight 映射）→ `BinaryPacker` 序列化 → `MessageReassembler.FragAndSend` 分片 → `transport.Send`
  - `OnResolutionChanged(newW, newH)`：更新坐标映射比例，防止鼠标错位

- 创建 `EasyRDP.Client.Wpf` 示例应用：
  - **连接界面**：输入服务器 IP、端口、用户名、密码 → 点击连接
  - **主界面**：`Image` 控件（视频渲染）+ 键盘/鼠标输入捕获
  - **连接流程**：
    1. `TcpTransportClient.Connect(host, port, timeoutMs)`
    2. 构造 `HandshakeReq` → 序列化 → `MessageReassembler.FragAndSend` → `Send`
    3. `MessageReassembler` 等待重组 → 收到 `HandshakeRes`
    4. 校验 Result → 创建 `MessageDispatcher`（P4 代码）= 初始化 Decoder + FrameBuffer + WpfRenderTarget
    5. 后续消息由 `ClientStreamSession` / `ClientInputSession` 接管

- 创建 `EasyRDP.Server.Wpf` 示例应用：
  - 启动/停止按钮（启动 TransportHost）
  - 连接列表显示活跃 Session（SessionId、RemoteEndPoint、分辨率、帧率、PendingFrames）
  - 控制面板：设置端口、并发上限、日志显示

- **端到端验证**：
  - 服务端 + 客户端在同一机器（localhost）跑通
  - 验证完整远程桌面链路：截屏 → 编码 → TCP 传输 → 解码 → WPF 渲染
  - 验证键盘/鼠标输入转发
  - 验证光标更新
  - 验证分辨率变更闭环（服务端改分辨率 → 客户端自动 Resize → 鼠标坐标正确）
  - 验证断连重连
  - 验证 D11 自适应降级（用 CPU 压力工具模拟弱机，观察自动降分辨率/fps 并恢复）
  - 验证 D12 并发上限（第三个客户端连接被拒绝 ServerBusy）

---

## 验证策略总览

| 阶段 | 编译 | 单元测试 | 集成测试 | 风险等级 |
|------|------|----------|----------|----------|
| 1. 脚手架 | ✅ | — | — | 低 |
| 2. 协议层 | ✅ | BinaryPacker 往返 + CodecNegotiator 组合 | — | 低 |
| 3. FrameBuffer | ✅ | 单线程 + 并发 + 超时 | — | 中 |
| 4. 客户端渲染 | ✅ | — | WPF 窗口实际推屏 | **高** |
| 5. 传输层 | ✅ | — | TCP 回环分片重组 | 中 |
| 6. 服务端 | ✅ | — | 完整服务端链路 | **高** |
| 7. 端到端 | ✅ | — | 远程桌面全链路 | **高** |

---

## 依赖关系

```
P1 ──→ P2 ──→ P3 ──→ P4 ──→ P5 ──→ P7
                              │        ↑
                              └──→ P6 ─┘
```
P1→P2→P3 严格线性依赖。P4 和 P5 可并行推进（分别验证客户端和服务端独立链路）。P6 和 P7 依赖 P5 传输层就绪。

---

## 注意事项

- 每个阶段完成后执行 `open-code-review` 代码审查
- P4 和 P5 必须通过强制集成验证才能进入下一阶段
- P5 的 net40 编码器后端（`H264EncoderNative`/`H264DecoderNative`）依赖原生 DLL（libx264/OpenH264），可先以 `#if NET8_0_OR_GREATER` 管理端实现推进，net40 路径后续补充 P/Invoke
- EasyDesk 子模块的 `IScreenCapturer` 等接口已在 `EasyDesk.Core` 中定义，服务端直接引用
- 所有公开接口/类必须带 XML doc 注释；`using` 指令在 `namespace` 内部
