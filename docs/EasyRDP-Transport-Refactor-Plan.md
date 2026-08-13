# EasyRDP 传输层抽象重构实施计划

> **状态**：待审核（Plan）
> **关联规范**：`docs/EasyRDP-Abstraction-Layers-Design.md`（当前 v2.7，本次同步至 v2.8）
> **协议版本变更**：`0x02` → `0x03`（不向后兼容，客户端/服务端同发版）
> **核心结论**：统一 `ITransportClient` + `ITransportServer` 为 `ITransport`（连接抽象）+ `ITransportAcceptor`（监听抽象）；`Send(byte[])` 只收发「完整消息」，分片逻辑完全下放到传输实现。

---

## 1. 背景与目标

### 1.1 现状问题

当前传输层由两个角色化接口承担：

- `ITransportClient`（客户端）——`Connect(host, port, timeoutMs)` / `Send(data)` / `Disconnect()` / `DataReceived` / `Disconnected`
- `ITransportServer`（服务端）——`Start(port)` / `SendTo(sessionId, data)` / `Disconnect(sessionId)` / `ClientConnected` / `ClientDisconnected` / `DataReceived`

抽象层面的缺陷：

1. **角色耦合**：TCP 客户端 `Connect` 得到的 socket 与服务端 `Accept` 得到的 socket 本质是同一个「已连接字节通道」，却拆成两个接口，且 `SendTo(sessionId)` 把多连接路由塞进了连接抽象里。
2. **分片泄漏进契约**：`Send`/`SendTo` 的语义是「发送一条线格式**分片**字节」，`DataReceived` 抛出的也是「分片」。分片（`FrameId/FragIdx/FragCount/CRC16`）本是传输介质的实现细节，却成了 `ITransport` 契约的一部分。
3. **TCP 路径上分片名存实亡**：TCP 是可靠有序字节流，本不需要 1400/16384 字节切片。现状代码已在绕过统一分片——`MessageReassembler.SendSingleFragment` 用于 1MB 的 `ClipFileContentsRes`（单分片超 `FragmentSize`），`FramingBuffer` 也支持单分片超长。接收侧按 `FrameId` 判 stale 的逻辑在 TCP 上**永远不会触发**（TCP 有序不丢），是纯为 UDP 预留的死代码。

### 1.2 重构目标

- **统一连接抽象**：`ITransport` 表示「一条已建立连接」的双向**消息**通道，与客户端/服务端角色无关。
- **建连/监听分离**：`ITransportConnector` 负责客户端建连（`Connect(endpoint, timeoutMs)`）；`ITransportAcceptor` 负责服务端监听（`Start(endpoint)`），并产出 `ITransport` 实例。
- **消息级契约**：`Send(byte[])` / `MessageReceived` 的单位是「完整消息」，接口不出现「分片」概念。
- **分片完全下放**：切片、重组、丢帧、校验是各传输实现（TCP/UDP/WebSocket）的内部细节。当前唯一实现的 TCP 因天然可靠有序，直接收发完整消息、无需任何分片逻辑；未来 UDP 实现在内部自建切片/重组/丢帧，不动 `ITransport` 契约。
- **多后端可插拔**：endpoint 用 `string` 表达（TCP `"host:port"` / 命名管道 `"\\.\pipe\name"` / Unix Socket 路径），客户端/服务端编排层只依赖接口，换 WebSocket/QUIC/命名管道后端无需改动会话/协议/UI 代码。

### 1.3 已确认的关键决策

| 决策点 | 结论 |
|--------|------|
| 分片逻辑归属 | 完全下放到传输实现（协议层移除 `FrameId/FragIdx/FragCount/CRC16`） |
| 协议兼容 | 不要求向后兼容，客户端/服务端同发版，协议版本升 `0x03` |
| `SessionId` 路由 | 从传输层移到 `TransportHost`（`Dictionary<uint, ITransport>` + 闭包捕获） |
| 消息级 CRC16 | 随分片头一并移除；TCP 底层校验兜底（远程桌面单帧损坏下一帧覆盖，可接受） |
| 服务端寻址 | `Start(int port)` → `Start(string endpoint)`，支持 TCP/命名管道/Unix Socket 等多后端寻址 |
| 客户端建连 | 新增 `ITransportConnector`，客户端只依赖接口，换后端零改动编排/UI 代码 |

---

## 2. 现状分析

### 2.1 现状接口与线格式

**线格式（v0.02，16 字节头）**：

```
┌─────────┬──────────┬─────────────┬────────────┬──────────┬──────────┬─────────┬───────────┐
│Magic(1) │ Type(1)  │PayloadLen(4)│ FrameId(4) │FragIdx(2)│FragCnt(2)│ CRC16(2)│ FragData  │
│ 0xE5    │ MsgType  │ LE uint32   │ LE uint32  │ LE ushort │ LE ushort│         │           │
└─────────┴──────────┴─────────────┴────────────┴──────────┴──────────┴─────────┴───────────┘
```

分片职责集中在 `MessageReassembler`（`FragAndSend` / `SendSingleFragment` / `BuildWireFragment` / `ComputeCrc16` / 内部 `FrameState`），接收侧字节流切分由 `FramingBuffer` 完成。

### 2.2 现状文件清单

**Core（`src/EasyRDP.Core/Transport/`）**

| 文件 | 职责 | 重构去向 |
|------|------|----------|
| `ITransportClient.cs` | 客户端接口 | 删除 |
| `ITransportServer.cs` | 服务端接口 | 删除 |
| `FragmentReceivedEventArgs.cs` | 分片事件参数（含 SessionId） | 删除 |
| `ConnectionEventArgs.cs` | 连接事件参数（含 SessionId） | 删除（由 `TransportAcceptedEventArgs` 替代） |
| `MessageReceivedEventArgs.cs` | 完整消息事件参数（含 SessionId/MessageType/Data） | 保留，去掉 SessionId |
| `LogCallback.cs` | 日志委托 | 保留 |
| `FramingBuffer.cs` | 按 16 字节头切分片 | 删除（由 `MessageFramingBuffer` 替代） |
| `MessageReassembler.cs` | 分片/重组/CRC/stale/控制流分流 | 删除 |

**协议层（`src/EasyRDP.Core/Protocol/`）**

| 文件 | 重构去向 |
|------|----------|
| `Constants.cs` | `ProtocolVersion` 0x02→0x03；删除 `FragmentSize`/`FragmentReassembleTimeoutMs`；保留 `FrameMagic`/`MaxFrameSize`/`MaxSafePayloadSize` |

**实现与编排层**

| 文件 | 重构去向 |
|------|----------|
| `src/EasyRDP.Client.Wpf/TcpTransportClient.cs` | 删除（由 Core 的 `TcpTransportConnector` + `TcpTransport` 替代） |
| `src/EasyRDP.Server.Wpf/TcpTransportServer.cs` | 删除（由 Core 的 `TcpTransportAcceptor` 替代） |
| `src/EasyRDP.Server.Wpf/TransportHost.cs` | 迁移到 `ITransportAcceptor` + 连接字典路由 |
| `src/EasyRDP.Server.Wpf/ServerStreamSession.cs` | 发送改用 `Framing.BuildMessage` |
| `src/EasyRDP.Server.Wpf/CursorTracker.cs` | 发送改用 `Framing.BuildMessage`（去掉手拼线格式 + `ComputeCrc16`） |
| `src/EasyRDP.Server.Wpf/MainWindowViewModel.cs` | `TcpTransportServer` → `TcpTransportAcceptor` |
| `src/EasyRDP.Client.Wpf/ClientStreamSession.cs` | 移除 `MessageReassembler`/`ComputeCrc16`/光标手动解析 |
| `src/EasyRDP.Client.Wpf/ClientInputSession.cs` | `ITransportClient` → `ITransport` |
| `src/EasyRDP.Client.Wpf/MainWindowViewModel.cs` | `TcpTransportClient` → `TcpTransport`；约 15 处发送调用点替换 |
| `src/EasyRDP.Core/Session/IClientStreamSession.cs` | 签名 `ITransportClient` → `ITransport` |
| `src/EasyRDP.Core/Session/IClientInputSession.cs` | 签名 `ITransportClient` → `ITransport` |

### 2.3 现状调用点分布（`FragAndSend`/`SendSingleFragment`/`SendTo`）

- `Server/TransportHost.cs`：约 14 处（握手/心跳/剪贴板/诊断等）
- `Server/ServerStreamSession.cs`：1 处（视频帧）
- `Server/CursorTracker.cs`：直接手拼线格式 + `ComputeCrc16`（光标）
- `Client/MainWindowViewModel.cs`：约 15 处（握手/心跳/剪贴板/诊断等）
- `Client/ClientStreamSession.cs`：2 处（`ClipFileContentsReq`/`FramebufferUpdateRequest`）+ 接收侧光标手动解析
- `Client/ClientInputSession.cs`：1 处（输入事件）

---

## 3. 目标架构设计

### 3.1 新接口定义（`EasyRDP.Core.Transport`）

```csharp
// ITransport.cs —— 一条已建立连接的双向消息通道，与客户端/服务端角色无关
namespace EasyRDP.Core.Transport
{
    using System;

    /// <summary>
    /// 传输连接抽象。发送/接收的单位是「完整消息字节」（framing 外层 + payload），
    /// 不感知分片——分片是各传输实现的内部细节。
    /// </summary>
    public interface ITransport : IDisposable
    {
        /// <summary>开始接收循环（幂等）。连接建立后不自动开始：调用方须先订阅
        /// MessageReceived/Disconnected，再调 Start()，避免首包在订阅前到达而丢失（见 Phase 2 竞态说明）。</summary>
        void Start();

        /// <summary>发送一条完整消息（Magic+Type+PayloadLen+Payload）。不返回成功标志：
        /// 写入失败/连接已断通过 Disconnected 事件与 OnLog 上报，调用方依赖 IsConnected 判断。</summary>
        void Send(byte[] message);

        bool IsConnected { get; }

        /// <summary>优雅关闭连接并触发 Disconnected 事件；幂等。IDisposable.Dispose 等价调用本方法并释放资源。</summary>
        void Disconnect();

        /// <summary>收到一条完整消息时触发（MessageType + payload）。</summary>
        event EventHandler<MessageReceivedEventArgs> MessageReceived;

        event EventHandler Disconnected;

        LogCallback OnLog { get; set; }
    }
}
```

```csharp
// ITransportAcceptor.cs —— 服务端监听器
namespace EasyRDP.Core.Transport
{
    using System;

    /// <summary>传输监听抽象。监听 endpoint，接受新连接并产出 ITransport 实例。</summary>
    /// <remarks>endpoint 格式由各实现定义：TCP 为 "port"（监听 0.0.0.0）或 "host:port"；
    /// 命名管道为 "\\.\pipe\name"；Unix Socket 为路径。</remarks>
    public interface ITransportAcceptor : IDisposable
    {
        void Start(string endpoint);
        void Stop();

        /// <summary>新连接到达时触发，事件参数携带该连接的 ITransport 实例。</summary>
        event EventHandler<TransportAcceptedEventArgs> ClientConnected;

        LogCallback OnLog { get; set; }
    }
}
```

```csharp
// ITransportConnector.cs —— 客户端建连器
namespace EasyRDP.Core.Transport
{
    /// <summary>客户端建连抽象。按 endpoint 建立一条连接并返回 ITransport 实例。</summary>
    /// <remarks>endpoint 格式由各实现定义：TCP 为 "host:port"；命名管道为 "\\.\pipe\name" 等。
    /// 客户端编排层只依赖本接口，换 WebSocket/QUIC 后端时仅需替换 connector 实例，
    /// 无需改动会话/UI 代码。
    /// 返回的 ITransport 处于「已连接但未开始接收」状态：调用方先订阅 MessageReceived/Disconnected，
    /// 再调 transport.Start()。连接失败（endpoint 解析失败/超时/拒绝）返回 null，
    /// 失败详情经 OnLog 回调 + NLog 记录；调用方判空处理（对齐现状 bool=false 语义）。</remarks>
    public interface ITransportConnector
    {
        ITransport Connect(string endpoint, int timeoutMs);

        LogCallback OnLog { get; set; }
    }
}
```

```csharp
// TransportAcceptedEventArgs.cs
namespace EasyRDP.Core.Transport
{
    using System;

    /// <summary>新连接事件参数。</summary>
    public class TransportAcceptedEventArgs : EventArgs
    {
        public ITransport Transport;
        public string RemoteEndPoint;

        public TransportAcceptedEventArgs(ITransport transport, string remoteEndPoint)
        {
            Transport = transport;
            RemoteEndPoint = remoteEndPoint;
        }
    }
}
```

```csharp
// MessageReceivedEventArgs.cs（改造：去掉 SessionId）
namespace EasyRDP.Core.Transport
{
    using System;

    /// <summary>完整消息事件参数。</summary>
    public class MessageReceivedEventArgs : EventArgs
    {
        public byte MessageType;
        public byte[] Data;

        public MessageReceivedEventArgs(byte messageType, byte[] data)
        {
            MessageType = messageType;
            Data = data;
        }
    }
}
```

### 3.2 新线格式（v0.03，6 字节头）

```
┌─────────┬──────────┬─────────────┬─────────┐
│Magic(1) │ Type(1)  │PayloadLen(4)│ Payload │
│ 0xE5    │ MsgType  │ LE uint32   │         │
└─────────┴──────────┴─────────────┴─────────┘
```

- 删除 `FrameId(4)` + `FragIdx(2)` + `FragCount(2)` + `CRC16(2)`。
- `PayloadLen` 上限仍为 `MaxSafePayloadSize`（10MB，防 DoS），`MaxFrameSize`（50MB）保留。
- `FrameMagic = 0xE5` 保留，用于字节流错位后的重对齐。

### 3.3 线格式工具（`EasyRDP.Core.Protocol`）

```csharp
// Framing.cs —— 组装/解析 framing 外层，替代 MessageReassembler 的 BuildWireFragment
namespace EasyRDP.Core.Protocol
{
    /// <summary>线格式工具。负责 Magic+Type+PayloadLen 外层的装/拆，与分片无关。</summary>
    public static class Framing
    {
        /// <summary>framing 头字节数：Magic(1)+Type(1)+PayloadLen(4)。</summary>
        public const int HeaderSize = 6;

        /// <summary>把消息类型与 payload 组装为完整线格式消息。</summary>
        public static byte[] BuildMessage(byte messageType, byte[] payload);

        /// <summary>从完整线格式消息解析出消息类型与 payload（TcpTransport 接收侧使用）。</summary>
        public static bool TryParse(byte[] message, out byte messageType, out byte[] payload);
    }
}
```

### 3.4 消息级 framing 缓冲（`EasyRDP.Core.Transport`）

```csharp
// MessageFramingBuffer.cs —— 把 TCP 字节流按 Magic+Type+PayloadLen 切为完整消息
namespace EasyRDP.Core.Transport
{
    using System;

    /// <summary>消息级 framing 缓冲。按 6 字节头切出完整消息，替代旧 FramingBuffer 的分片切分。</summary>
    public class MessageFramingBuffer
    {
        /// <summary>切出完整消息（Magic+Type+PayloadLen+Payload）时触发。</summary>
        public event Action<byte[]> MessageReady;

        /// <summary>喂入收到的字节，可能触发零到多个 MessageReady。</summary>
        public void Feed(byte[] data, int offset, int length);
    }
}
```

### 3.5 职责划分（前后对比）

| 职责 | 重构前 | 重构后 |
|------|--------|--------|
| 客户端建连 | `ITransportClient.Connect(host, port)` | `ITransportConnector.Connect(endpoint, timeoutMs)` |
| 服务端监听 | `ITransportServer.Start(port)` | `ITransportAcceptor.Start(endpoint)` |
| 发送 | `Send`/`SendTo`（分片字节） | `ITransport.Send`（完整消息字节） |
| 接收 | `DataReceived`（分片字节） | `ITransport.MessageReceived`（完整消息） |
| 切片/重组 | 协议层 `MessageReassembler` + `FramingBuffer` | TCP 不需要；UDP 未来内部实现 |
| 丢帧语义 | 协议层 `MessageReassembler`（stale/控制流分流） | TCP 无（天然可靠）；发送侧丢帧仍在 Session 层 D11 |
| `SessionId` 路由 | 传输层 `SendTo(sessionId)` / 事件携带 SessionId | `TransportHost` 维护 `Dictionary<uint, ITransport>` + 闭包捕获 |

### 3.6 TCP 实现落点

`TcpTransport` / `TcpTransportConnector` / `TcpTransportAcceptor` 放入 **`EasyRDP.Core/Transport/`**，理由：

- `EasyRDP.Core` 同时被 Client.Wpf 与 Server.Wpf 引用，是两端共享传输实现的唯一自然位置。
- `TcpClient` / `TcpListener` / `NetworkStream` 在 `net40` / `netstandard2.0` / `net8.0` 三个目标框架下均可用。
- 实现沿用现状的「同步 Socket + `Thread`」风格（C# 5 兼容，无 async/await 的 TFM 差异）。

需保留的现状细节（迁移时不得丢失）：

- 客户端 `Connect` 的 `BeginConnect + WaitOne(timeoutMs)` 超时逻辑
- 两端 `NoDelay = true`（禁用 Nagle，降低输入/心跳延迟）
- 服务端接收线程 `ThreadPriority.AboveNormal`（弱机 CPU 饱和时保证输入及时处理）
- 发送锁（序列化 `stream.Write`，防止并发写导致字节交错）
- 服务端 `MaxPendingConnections = 16`（握手前连接硬上限，防恶意占用）
- 接收循环的防御性 try-catch（单个坏消息不杀死接收线程）

### 3.7 多后端兼容性

「消息级 + 连接/监听器分离 + 角色无关 + `string endpoint`」这套抽象，覆盖了绝大多数可用于实时双工通讯的协议：

| 通讯方式 | 能否实现为 `ITransport` / `ITransportConnector` / `ITransportAcceptor` | 说明 |
|----------|------------------------------------------------------------------------|------|
| WebSocket | ✅ 完美 | 有 message 帧边界、双向、可穿透 HTTP 代理；Upgrade 握手放 Connect/Accept 阶段 |
| QUIC / HTTP/3 stream | ✅ 完美 | UDP 上双向可靠流，无 TCP 队头阻塞，公网弱网首选；net40/XP 跑不了，仅作现代平台后端 |
| WebRTC DataChannel | ✅ 兼容（集成重） | SCTP 双向消息通道 + NAT 穿透；数据面可映射，但 ICE/SDP 信令需额外带外通道 |
| gRPC 双向流 | ✅ 兼容 | HTTP/2 双向流，但引入 protobuf/http2 依赖较重，实时视频收益不大 |
| 命名管道 / Unix Socket | ✅ 兼容 | 本机双向字节流；正是 `Start(string endpoint)` 而非 `Start(int port)` 的原因 |
| HTTP/1.1 纯请求响应 | ⚠️ 不应硬套 | 请求-响应模型与服务端主动持续推送视频流的语义冲突；HTTP 正确角色是 WebSocket/WebRTC 的握手与信令载体 |
| SSE / UDP 广播 / 共享内存 | ❌ 不适用 | 单向、无连接、或一对多，与「连接型双向通道」模型冲突 |

**结论**：本次把 `Start(int port)` 改为 `Start(string endpoint)`、并新增 `ITransportConnector`，正是为了让「换后端」无需改动会话/协议/UI 层——客户端只依赖 `ITransportConnector` + `ITransport`，服务端只依赖 `ITransportAcceptor` + `ITransport`。

---

## 4. 分阶段实施步骤

> **策略**：先增后删。Phase 1–3 只新增不删除，保证每个阶段结束 `dotnet build` 通过；Phase 4 统一删除旧代码；Phase 5 收尾验证。全程 C# 5 语法约束（`net40` 目标禁止字符串插值/`?.`/表达式体成员）。

### Phase 1 —— 定义传输接口与线格式（Core 层，纯新增）

**目标**：新增新接口与线格式工具，旧代码不动，`EasyRDP.Core` 仍可编译。

| 文件 | 操作 | 内容 |
|------|------|------|
| `src/EasyRDP.Core/Transport/ITransport.cs` | 新增 | 3.1 节接口定义 |
| `src/EasyRDP.Core/Transport/ITransportAcceptor.cs` | 新增 | 3.1 节接口定义（`Start(string endpoint)`） |
| `src/EasyRDP.Core/Transport/ITransportConnector.cs` | 新增 | 3.1 节接口定义（`Connect(endpoint, timeoutMs)`） |
| `src/EasyRDP.Core/Transport/TransportAcceptedEventArgs.cs` | 新增 | 3.1 节类定义 |
| `src/EasyRDP.Core/Protocol/Framing.cs` | 新增 | 3.3 节工具类（`BuildMessage` / `TryParse` / `HeaderSize=6`） |
| `src/EasyRDP.Core/Protocol/Constants.cs` | 修改 | `ProtocolVersion` 0x02→0x03；其余常量暂留 |

**关键实现要点**：

- `Framing.BuildMessage`：`Magic=0xE5` + `Type` + `PayloadLen`(小端 4 字节) + `payload`，用 `MemoryStream` + `BinaryWriter` 或手写字节拼接（参考现有 `MessageReassembler.BuildWireFragment` 的小端写法）。
- `Framing.TryParse`：校验 `Magic`、`Type` 为已知 `MessageType`、`PayloadLen ≤ MaxSafePayloadSize`，返回 payload。
- `Constants.FragmentSize` / `FragmentReassembleTimeoutMs` **暂不删除**（旧 `MessageReassembler`/`FramingBuffer` 仍在用），Phase 4 清理。

**验证**：

```bash
dotnet build src/EasyRDP.Core/EasyRDP.Core.csproj
```

**验收标准**：Core 三目标（net40/netstandard2.0/net8.0）全部编译通过，无新增警告为错误。

---

### Phase 2 —— 实现 TCP 传输（新增，旧实现不动）

**目标**：新增 `TcpTransport` / `TcpTransportConnector` / `TcpTransportAcceptor` / `MessageFramingBuffer`，实现新接口；旧 `TcpTransportClient`/`TcpTransportServer` 原样保留。

| 文件 | 操作 | 内容 |
|------|------|------|
| `src/EasyRDP.Core/Transport/MessageFramingBuffer.cs` | 新增 | 3.4 节缓冲类（按 6 字节头切完整消息） |
| `src/EasyRDP.Core/Transport/TcpTransport.cs` | 新增 | 实现 `ITransport`，仅代表「一条已连接通道」（详见下） |
| `src/EasyRDP.Core/Transport/TcpTransportConnector.cs` | 新增 | 实现 `ITransportConnector`（客户端建连，详见下） |
| `src/EasyRDP.Core/Transport/TcpTransportAcceptor.cs` | 新增 | 实现 `ITransportAcceptor`（服务端监听，详见下） |

**`TcpTransport` 设计**（合并自 `TcpTransportClient` + `TcpTransportServer` 的连接职责）：

```csharp
public class TcpTransport : ITransport
{
    // 仅代表「一条已连接通道」：包装已连接的 TcpClient，构造时只设 NoDelay，
    // 不启动接收线程（调用方在订阅完事件后显式 Start()，避免首包竞态）。
    // 服务端 Accept 与客户端 Connector 建连后都走这个构造函数（职责与角色无关）。
    public TcpTransport(TcpClient client, string remoteEndPoint);

    // ITransport 实现
    public void Start() { /* 启动接收线程，幂等 */ }
    public void Send(byte[] message) { /* 发送锁 + stream.Write + 断连竞态处理 */ }
    public bool IsConnected { get; }
    public void Disconnect() { /* 关 socket + 抛 Disconnected */ }
    public event EventHandler<MessageReceivedEventArgs> MessageReceived;
    public event EventHandler Disconnected;
    public LogCallback OnLog { get; set; }
    public void Dispose();
}
```

- 私有接收线程 `ReceiveLoop`（**由 `Start()` 启动，非构造启动**）：`stream.Read` → `MessageFramingBuffer.Feed` → `MessageReady` 回调里 `Framing.TryParse` → 构造 `MessageReceivedEventArgs(messageType, payload)` → 抛 `MessageReceived`，整体套防御性 try-catch。
- `Send` 保留现状的发送锁语义；发送失败/对端断开时走限频日志 + 触发断连清理（对齐 `TcpTransportServer.SendTo` 的断连竞态处理）。

> **首包竞态说明（关键）**：连接建立后若接收线程立即启动，服务端 `Accept` 后客户端可能已发来 `HandshakeReq`，此时 `TransportHost` 尚未在 `ClientConnected` 事件里订阅该连接的 `MessageReceived` → 首包丢失、握手超时。因此 `Start()` 必须在「订阅完成」之后调用：服务端由 `TransportHost` 在 `ClientConnected` 处理器内订阅 `MessageReceived`/`Disconnected` 后调用，客户端由 `MainWindowViewModel` 在订阅握手 `MessageReceived` 后调用。

**`TcpTransportConnector` 设计**（客户端建连，替代 `TcpTransportClient.Connect`）：

```csharp
public class TcpTransportConnector : ITransportConnector
{
    // endpoint 格式 "host:port"；解析后 BeginConnect + WaitOne(timeoutMs) 超时，
    // 成功后 new TcpTransport(client, remote) 并返回「未 Start 的通道」（由调用方订阅后 Start）。
    // 失败（解析失败/超时/拒绝）返回 null，详情经 OnLog 回调 + NLog 记录。
    public ITransport Connect(string endpoint, int timeoutMs);
    public LogCallback OnLog { get; set; }
}
```

**`TcpTransportAcceptor` 设计**（合并自 `TcpTransportServer` 的监听职责）：

```csharp
public class TcpTransportAcceptor : ITransportAcceptor
{
    // endpoint 格式 "port"（监听 0.0.0.0）或 "host:port"；实现内解析为 IPAddress/端口。
    public void Start(string endpoint);   // TcpListener + AcceptLoop 线程
    public void Stop();                   // 关 listener + 关所有连接 + Join 接收线程
    public event EventHandler<TransportAcceptedEventArgs> ClientConnected;
    public LogCallback OnLog { get; set; }
    public void Dispose();
}
```

- `AcceptLoop`：`AcceptTcpClient` → `NoDelay=true` → 校验 `MaxPendingConnections`（此时 pending 计数由 acceptor 维护，超限直接 `Close`）→ 构造 `TcpTransport`（**未 Start**）→ fire `ClientConnected`（`TransportHost` 在处理器内完成订阅 + `SessionId` 分配 + `transport.Start()`）。**注意**：`SessionId` 的分配不再由 acceptor 承担，移到 `TransportHost`（Phase 3）。
- 断开事件（原 `ClientDisconnected`）由每个 `TcpTransport.Disconnected` 事件上抛，`TransportHost` 订阅时闭包捕获 `sessionId` 完成路由。

**`MessageFramingBuffer` 实现要点**（迁移自旧 `FramingBuffer` 的边界细节，不得丢失）：

- 尾部 `Magic` 字节保留：若缓冲末尾最后一个字节恰为 `0xE5`，不能立即消费/丢弃，须保留到下一轮 `Feed`（TCP 可能在帧边界切分，末位 `0xE5` 是下一帧的起始），否则边界场景丢包。
- 失步重对齐：扫描 `0xE5` 找帧头时，须**同时校验 `Type ∈ 已知 MessageType 集合` 且 `PayloadLen ≤ MaxSafePayloadSize`**，避免 payload 内的 `0xE5` 字节被误判为帧头（移除 CRC16 后仅靠 Magic 重对齐更易受干扰）。

**验证**：

```bash
dotnet build src/EasyRDP.Core/EasyRDP.Core.csproj
```

**验收标准**：Core 编译通过；新增类无编译错误；旧 `TcpTransportClient`/`TcpTransportServer` 未被触碰。

---

### Phase 3 —— 迁移编排层与调用点到新接口（旧代码共存）

**目标**：把所有编排层与 UI 层从旧接口/`MessageReassembler` 迁移到新接口，旧实现仍存在但已无调用方（或保留待 Phase 4 删除）。此阶段改动最大，需逐文件核对。

**3.1 服务端 `TransportHost.cs`**（改动最大）

- 字段 `ITransportServer _transportServer` → `ITransportAcceptor _transportAcceptor`。
- `Start(int port)` → `Start(string endpoint)`，透传 `_transportAcceptor.Start(endpoint)`。
- 新增 `Dictionary<uint, ITransport> _transports` 替代对 `SendTo(sessionId)` 的依赖；`_nextSessionId` 分配逻辑移入 `TransportHost`（原来在 `TcpTransportServer.AcceptLoop`）。
- 订阅 `_transportAcceptor.ClientConnected`：分配 `sessionId` → `_transports[sessionId] = transport` → 订阅该 `transport.MessageReceived`（闭包捕获 `sessionId`）→ 订阅 `transport.Disconnected`（触发 C5 断连联动）→ **调 `transport.Start()`**（订阅完成后才启动接收，避免首包竞态）。
- 删除 `MessageReassembler` 相关字段（`_reassemblers` 字典）与 `OnDataReceived` 分片重组逻辑，改为直接处理 `MessageReceived`（已是完整消息）。
- 所有发送点（握手 `HandshakeRes`、`Keepalive`、`ClipFormatList`、`ClipboardSync`、`ImageClipboard*`、`ClipFileContents*`、`DiagnosticInfo` 等约 14 处）：
  - `MessageReassembler.FragAndSend(0, type, payload, (s,d) => _transportServer.SendTo(s,d), sid)` → `_transports[sid].Send(Framing.BuildMessage(type, payload))`
  - `SendSingleFragment(...)` → 同上（不再区分单/多分片）
- 发送侧对已断开会话的防御：`_transports.TryGetValue(sid)` 失败则跳过（等价原 `SendTo` 的 `_sendNotFoundCount` 限频日志）。

**3.2 服务端 `ServerStreamSession.cs`**

- `MessageReassembler.FragAndSend(...)` 发送视频帧 → `_sendAction(sessionId, Framing.BuildMessage(MessageType.VideoFrame, payload))`（`_sendAction` 回调签名由 `Action<uint, byte[]>` 承载，内部改为 `transport.Send`）。

**3.3 服务端 `CursorTracker.cs`**

- `ICursorTrackerSession.AttachSendTo(Action<uint, byte[]> sendTo, uint sessionId)` → `AttachSendTo(Action<byte[]> send)`：`sessionId` 路由移到 `TransportHost`，光标发送回调只需发字节（`SessionId` 由闭包捕获），并同步调整 `SendCursorUpdate`/`BuildCursorWire`。
- 删除手拼线格式 + `MessageReassembler.ComputeCrc16` 的代码，改为 `Framing.BuildMessage(MessageType.CursorUpdate, payload)` 后经 `transport.Send` 发送。

**3.4 服务端 `MainWindowViewModel.cs`**

- `new TcpTransportServer()` → `new TcpTransportAcceptor()`；字段类型 `TcpTransportServer` → `ITransportAcceptor`（或 `TcpTransportAcceptor`）。
- `_transportHost.Start(port)` → `_transportHost.Start(port.ToString())`（endpoint 为纯端口字符串，`TcpTransportAcceptor` 解析为监听 0.0.0.0）。

**3.5 客户端 `ClientStreamSession.cs`**

- 字段 `ITransportClient _transport` → `ITransport`；删除 `MessageReassembler _reassembler`。
- `BeginReceive(ITransport)` / `Start(ITransport)`：`_transport.DataReceived += OnDataReceived` → `_transport.MessageReceived += OnMessageReceived`。
- 删除 `OnDataReceived`（原来对 `CursorUpdate` 做手动 16 字节解析 + CRC16 校验再分流）；`OnMessageReceived` 直接按 `MessageReceivedEventArgs.MessageType` 分发（`VideoFrame` → 解码、`CursorUpdate` → `CursorUpdateMessage.Unpack`、其余消息走原有分发逻辑）。
- 删除 `MessageReassembler.ComputeCrc16` 相关代码。
- `Stop()` 中 `_transport.DataReceived -= OnDataReceived` → `_transport.MessageReceived -= OnMessageReceived`；删除 `_reassembler` 清理。

**3.6 客户端 `ClientInputSession.cs`**

- `ITransportClient _transport` → `ITransport`；发送输入事件改 `Framing.BuildMessage(MessageType.InputEvent, payload)` + `transport.Send`。
- 移除 `_sendFrameId`（分片 `FrameId` 计数器，分片概念消失后无意义）；`SendInput` 的诊断日志中 frameId 字段改为记录 payload 长度或直接去掉。

**3.7 客户端 `MainWindowViewModel.cs`**

- 字段 `TcpTransportClient _transport` → `ITransport _transport`；`new TcpTransportClient()` → `var connector = new TcpTransportConnector(); _transport = connector.Connect(host + ":" + port, ConnectTimeoutMs);`。
- 仅依赖 `ITransportConnector` + `ITransport` 接口，换 WebSocket/QUIC 后端时只替换 connector 实例（endpoint 格式随之变化），会话/UI 逻辑不变。
- 握手阶段：删除 `handshakeReassembler = new MessageReassembler()` 与 `onHandshakeData`，改为订阅 `_transport.MessageReceived` 处理 `HandshakeRes`；**订阅完成后调 `_transport.Start()`，再发送 `HandshakeReq`**（先订阅后启动，避免 `HandshakeRes` 丢失）。
- 约 15 处 `FragAndSend`/`SendSingleFragment` → `_transport.Send(Framing.BuildMessage(type, payload))`。

**3.8 Core 会话接口签名**

- `IClientStreamSession.Start(ITransport transport)`；`IClientInputSession.Start(ITransport transport, int w, int h)`。

**验证**：

```bash
dotnet build EasyRDP.slnx
```

**验收标准**：全 solution 编译通过（新旧 `TcpTransport*` 与旧接口并存）；grep 确认除 Phase 4 待删文件外，无 `FragAndSend`/`SendSingleFragment`/`ComputeCrc16`/`ITransportClient`/`ITransportServer`/`FragmentReceivedEventArgs`/`ConnectionEventArgs`/`FramingBuffer`/`SendTo` 的业务调用点残留。

---

### Phase 4 —— 删除旧接口与旧实现（无引用后清理）

**目标**：删除所有旧传输基元，收尾接口细节。

| 文件 | 操作 |
|------|------|
| `src/EasyRDP.Core/Transport/ITransportClient.cs` | 删除 |
| `src/EasyRDP.Core/Transport/ITransportServer.cs` | 删除 |
| `src/EasyRDP.Core/Transport/FragmentReceivedEventArgs.cs` | 删除 |
| `src/EasyRDP.Core/Transport/ConnectionEventArgs.cs` | 删除 |
| `src/EasyRDP.Core/Transport/MessageReassembler.cs` | 删除 |
| `src/EasyRDP.Core/Transport/FramingBuffer.cs` | 删除 |
| `src/EasyRDP.Client.Wpf/TcpTransportClient.cs` | 删除 |
| `src/EasyRDP.Server.Wpf/TcpTransportServer.cs` | 删除 |
| `src/EasyRDP.Core/Transport/MessageReceivedEventArgs.cs` | 修改：去掉 `SessionId` 字段与构造参数 |
| `src/EasyRDP.Core/Protocol/Constants.cs` | 修改：删除 `FragmentSize` / `FragmentReassembleTimeoutMs` |

**验证**：

```bash
grep -rn "MessageReassembler\|FragAndSend\|SendSingleFragment\|ITransportClient\|ITransportServer\|FragmentReceivedEventArgs\|ConnectionEventArgs\|FramingBuffer\|TcpTransportClient\|TcpTransportServer" src/ test/
# 期望：仅剩注释/文档中的历史引用，无代码引用
dotnet build EasyRDP.slnx
```

**验收标准**：grep 无代码引用残留；全 solution 编译通过。

---

### Phase 5 —— 测试更新 + 文档同步 + 全量验证

**5.1 测试更新**

| 文件 | 操作 |
|------|------|
| `test/EasyRDP.Core.Tests/Transport/FramingBufferTests.cs` | 重写为 `MessageFramingBufferTests`（消息级切分：跨 TCP 包边界、粘包、失步重对齐、超长 payload 拒绝） |
| `test/EasyRDP.Core.Tests/Transport/MessageReassemblerTests.cs` | 删除（被测类已删除） |
| `test/EasyRDP.Core.Tests/Protocol/FramingTests.cs` | 新增（`BuildMessage`/`TryParse` 往返、小端 PayloadLen、未知 Type 拒绝） |

**5.2 设计文档同步（`docs/EasyRDP-Abstraction-Layers-Design.md`，v2.7 → v2.8）**

需更新的章节：

- 版本头与修订记录：新增 `v2.7 → v2.8` 条目
- 4.3 节 `ITransportClient`/`ITransportServer` → `ITransport` + `ITransportAcceptor`（含代码块）
- 4.3.1 `MessageReassembler` → 说明分片下放，删除该类
- 层次小结图：`TCP/UDP/WebSocket` 之上改为 `ITransport`（消息级），协议层不再有分片重组
- 6.3 线格式：16 字节分片头 → 6 字节消息头（`ProtocolVersion = 0x03`）
- 6.3.1 帧分片章节：整节改写为「分片由传输实现内部负责，TCP 直接整消息收发」
- 目录树：Transport 下新增 `ITransport.cs`/`ITransportAcceptor.cs`/`ITransportConnector.cs`/`TcpTransport.cs`/`TcpTransportConnector.cs`/`TcpTransportAcceptor.cs`/`MessageFramingBuffer.cs`/`TransportAcceptedEventArgs.cs`，删除旧文件
- 决策表 D1「纯视频流协议」相关协议版本号同步 0x03
- 风险表「协议与传输方式耦合」「大帧投递/乱序/丢包」等条目同步新语义

**5.3 全量验证**

```bash
dotnet test test/EasyRDP.Core.Tests/EasyRDP.Core.Tests.csproj
dotnet build EasyRDP.slnx
```

---

## 5. 测试策略

- **单元测试**：`FramingTests`（BuildMessage/TryParse）、`MessageFramingBufferTests`（切分边界与失步恢复）。
- **集成验证**（可选，需真实两端）：启动 `EasyRDP.Server.Wpf`（net40）与 `EasyRDP.Client.Wpf`（net8.0），验证握手、视频帧、光标、剪贴板（含 1MB 文件块）、输入事件、心跳均正常。
- **回归关注点**：
  - 1MB 文件剪贴板块（原来 `SendSingleFragment` 单分片超长路径）在新架构下就是一条完整消息，`MaxSafePayloadSize`(10MB) 内直接发送。
  - 空 payload 消息（`Keepalive`/`FramebufferUpdateRequest`）在新 `Framing.BuildMessage` 中必须产出 `PayloadLen=0` 的 6 字节头消息，接收侧 `TryParse` 不得丢弃。
  - 光标消息 32×32 位图（约 4.3KB）原依赖单分片超长支持，新架构天然支持。
  - pre-handshake 缓冲时序：`ClientStreamSession.BeginReceive` 在握手前订阅 `MessageReceived`，管线未就绪时缓冲消息的逻辑在接口迁移后须保持一致（`OnMessageReceived` 内 `_pipelineReady` 判断不变，避免首个关键帧/光标在 `InitPipeline` 完成前丢失）。

---

## 6. 风险与回滚

| 风险 | 影响 | 缓解 |
|------|------|------|
| 协议格式不兼容 | 旧客户端连不上新服务端 | 已确认同发版；文档标注 0x03 不接受 0x01/0x02 |
| 消息级 `Send` 队头阻塞 | 大视频帧一次性写入时，同连接 Keepalive/InputEvent 短暂延迟 | 单 Session 内 H.264 帧通常远小于 50MB 上限、局域网写入毫秒级；发送锁 per-connection，不同 Session 互不影响；如需更细粒度可在 `TcpTransport` 内部再分次 write（不改消息契约） |
| 发送/接收内存峰值 | 完整消息一次性 `new byte[]` + 接收侧缓冲最大 10MB | 现状 `FragAndSend` 也逐片分配、总分配相当；`MaxSafePayloadSize` 10MB 兜底；D12 并发上限 `_maxSessions=2` 限制最坏 ~20MB 接收缓冲，XP 32 位 2GB 地址空间可用但不宽裕 |
| UDP 未来完整性 | 移除 CRC16 后 UDP 路径无应用层校验 | 未来 UDP 实现必须自带 datagram 级校验/重传；TCP 路径由 TCP checksum 兜底（远程桌面单帧损坏下一帧覆盖可接受） |
| `SessionId` 路由迁移遗漏 | 断连/发送错路 | Phase 3 用 `Dictionary<uint, ITransport>` + 闭包；Phase 4 grep 核对零残留 |
| 接收线程异常杀死连接 | 单个坏消息断连 | 保留防御性 try-catch（现状语义） |
| net40/XP 编译 | Core 多目标失败 | `TcpTransport` 用同步 Socket+Thread（C# 5），Phase 1/2 即 build Core 提前暴露 |

**回滚**：每个 Phase 完成后 `git commit`；Phase 1–3 是纯新增、随时可回退到上一 commit；Phase 4 删除不可逆，须确认 Phase 3 全量构建与 grep 均通过后再执行。

---

## 7. 验收标准（最终）

1. `ITransport` / `ITransportConnector` / `ITransportAcceptor` 接口已定义且无「分片」概念泄漏；`Send(byte[])` / `MessageReceived` 以完整消息为单位；`ITransport.Start()` 显式开始接收（订阅后启动，无首包竞态）。
2. `Start(string endpoint)`（非 `int port`）支持 TCP/命名管道/Unix Socket 等多后端的寻址表达；`ITransportConnector` 使客户端建连与具体传输解耦。
3. `TcpTransport` / `TcpTransportConnector` / `TcpTransportAcceptor` 落地 `EasyRDP.Core`，客户端/服务端共用。
4. 旧 `ITransportClient`/`ITransportServer`/`MessageReassembler`/`FramingBuffer`/`FragmentReceivedEventArgs`/`TcpTransportClient`/`TcpTransportServer` 全部删除，代码零残留。
5. 线格式为 6 字节头（`ProtocolVersion = 0x03`），`Constants` 无 `FragmentSize`/`FragmentReassembleTimeoutMs`。
6. `dotnet test test/EasyRDP.Core.Tests/EasyRDP.Core.Tests.csproj` 通过；`dotnet build EasyRDP.slnx` 通过（net40/netstandard2.0/net8.0/net8.0-windows 全目标）。
7. 设计文档已同步至 v2.8。
