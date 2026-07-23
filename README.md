# EasyRDP

> **轻量级局域网远程桌面工具** — 类似 VNC / Radmin，采用自定义轻量级协议，可扩展传输层（TCP 默认）。提供屏幕共享、远程键鼠操控、双向剪贴板同步等能力。
>
> **实现**：服务端和客户端均提供两套 UI——.NET Framework 4.0 + WPF（兼容 Windows XP ~ 11）和 .NET 8 + Avalonia（面向 Windows 7+ / Linux / macOS）。同一套 `EasyRDP.Core` 协议层共享。
>
> EasyRDP 底层由 [EasyDesk](EasyDesk/) 桌面 I/O 库驱动，纯 P/Invoke 实现，零第三方依赖，双目标 `net40` + `netstandard2.0`。

---

## 核心特性

| 特性 | 说明 |
|---|---|
| 自定义轻量级协议 | 非标准 RDP/VNC，按需精简字段，降低协议开销。全部字段小端序，14 字节消息头 |
| 可扩展传输层 | 抽象 `ITransportClient` / `ITransportServer` 接口，TCP 默认实现，UDP 可选，同一时刻单传输 |
| 屏幕捕获 | 全屏截图（BGRA32 原始像素），脏矩形增量检测 + Deflate 实时压缩 |
| 远程输入注入 | 鼠标移动/点击/滚轮、键盘按键、Unicode 文本发送 |
| 剪贴板同步 | 双向同步 ✅，500ms 静默期防死循环 |
| 全系统兼容 | 服务端+客户端各两套 UI：WPF (.NET 4，XP~Win11) + Avalonia (.NET 8，Win7+/Linux/macOS) |
| 零依赖 | 纯 P/Invoke 调用 user32/gdi32/kernel32，不依赖第三方 UI 框架 |

---

## 架构概览

```
┌─────────────────────────────────────────────────┐
│                  EasyRDP（应用层）                │
│                                                  │
│  ┌────────────────────┐  ┌────────────────────┐  │
│  │  .NET 4 + WPF      │  │  .NET 8 + Avalonia │  │
│  │  (Windows XP~11)   │  │  (Win7+/Linux/mac) │  │
│  ├────────────────────┤  ├────────────────────┤  │
│  │  Server.Wpf  ⏳    │  │  Server.Avalonia ⏳ │  │
│  │  Client.Wpf  ⏳    │  │  Client.Avalonia ⏳│  │
│  └────────┬───────────┘  └────────┬───────────┘  │
│           │                       │              │
│  ┌────────┴───────────────────────┴──────────┐   │
│  │            EasyRDP.Core 共享协议层          │   │
│  │  ┌─────────────────────────────────────┐  │   │
│  │  │          Protocol（协议编解码）       │  │   │
│  │  │  MessageHeader / MessageCodec       │  │   │
│  │  │  CompressHelper / DirtyRectDetector │  │   │
│  │  │  BinaryPacker（全部字段小端序 LE）   │  │   │
│  │  └────────────────┬────────────────────┘  │   │
│  │  ┌────────────────┴────────────────────┐  │   │
│  │  │          Transport（可扩展传输层）    │  │   │
│  │  │  ITransportClient / ITransportServer│  │   │
│  │  │  TcpTransportClient/Server ✅       │  │   │
│  │  │  UdpTransportClient/Server ✅       │  │   │
│  │  │  PacketFramer（传输无关分包器）      │  │   │
│  │  └─────────────────────────────────────┘  │   │
│  └──────────────────────┬────────────────────┘   │
└─────────────────────────┼────────────────────────┘
                          │
┌─────────────────────────┴────────────────────────┐
│               EasyDesk（桌面 I/O 库）              │
│  ┌────────────────────────────────────────────┐  │
│  │              EasyDesk.Core                  │  │
│  │   接口 + 数据模型，零依赖                    │  │
│  │   IInputSimulator   IScreenCapturer        │  │
│  │   ICursorCapturer    IClipboardService      │  │
│  │   IDesktopInfo       DesktopFactory         │  │
│  └─────────────────────┬──────────────────────┘  │
│  ┌─────────────────────┴──────────────────────┐  │
│  │            EasyDesk.Windows                 │  │
│  │   P/Invoke 实现，零依赖                      │  │
│  │   ┌───────────┐ ┌───────────┐              │  │
│  │   │ user32.dll │ │ gdi32.dll │              │  │
│  │   └───────────┘ └───────────┘              │  │
│  │   ┌───────────────┐                         │  │
│  │   │ kernel32.dll  │                         │  │
│  │   └───────────────┘                         │  │
│  └────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────┘
```

- **双 UI 策略**：服务端和客户端各两套实现——.NET 4 + WPF 兼容 Windows XP ~ 11；.NET 8 + Avalonia 面向 Windows 7+ / Linux / macOS。两套 UI 通过 `EasyRDP.Core` 共享同一套协议逻辑
- **EasyRDP.Core**：协议 + 可扩展传输共享库，多目标 `net40;net8.0`。Protocol 层包含消息编解码、Deflate 压缩、脏矩形检测；Transport 层抽象 `ITransportClient` / `ITransportServer`，TCP/UDP 双实现 + `PacketFramer` 分包
- **EasyDesk.Core**：定义 5 类桌面 I/O 抽象接口 + 8 个数据模型，零依赖
- **EasyDesk.Windows**：P/Invoke 调用 `user32.dll`（SendInput、剪贴板、光标）、`gdi32.dll`（BitBlt 屏幕捕获）、`kernel32.dll`（内存操作），零依赖

---

## 快速开始

### 克隆仓库

```bash
git clone --recurse-submodules https://github.com/lishuangquan1987/EasyRDP.git
git submodule update --init --recursive   # 如果已普通克隆
```

### 构建

```bash
dotnet build
```

### 运行

```bash
# 服务端（被控机器）
dotnet run --project src/EasyRDP.Server

# 客户端（控制机器，弹出 Avalonia/WPF 窗口）
dotnet run --project src/EasyRDP.Client [server-ip]
```

### 配置

编辑 `src/EasyRDP.Server/appsettings.json`：

```json
{
  "Port": 8750,
  "AuthToken": "easyrdp-demo",
  "CompressType": "Zlib",
  "FrameRate": 15
}
```

### 测试 EasyDesk

```bash
# ⚠️ 测试会实际操作鼠标键盘 / 剪贴板，运行时请勿触碰机器
dotnet test test/EasyDesk.Windows.Tests/EasyDesk.Windows.Tests.csproj
```

---

## 平台支持

| 平台 | 屏幕捕获 | 输入模拟 | 光标 | 剪贴板 | 桌面信息 | 应用层 |
|---|---|---|---|---|---|---|
| **Windows 7+** | ✅ BitBlt | ✅ SendInput | ✅ GetCursorInfo | ✅ OpenClipboard | ✅ GetSystemMetrics | ✅ WPF + Avalonia |
| **Windows XP** | ✅ BitBlt | ✅ SendInput | ✅ GetCursorInfo | ✅ OpenClipboard | ✅ GetSystemMetrics | ✅ WPF (.NET 4) |
| Linux (X11) | ⏳ 计划中 | ⏳ 计划中 | ⏳ 计划中 | ⏳ 计划中 | ⏳ 计划中 | ⏳ Avalonia 计划 |
| macOS | ⏳ 计划中 | ⏳ 计划中 | ⏳ 计划中 | ⏳ 计划中 | ⏳ 计划中 | ⏳ 计划中 |

---

## 项目结构

```
EasyRDP/
├── README.md                    ← 你在这里
├── AGENTS.md                    ← 开发规范
├── .gitignore
├── .gitmodules                  ← EasyDesk 子模块引用
├── docs/
│   ├── EasyRDP-Protocol-v1.md   ← 协议规范
│   └── EasyRDP-Codec-Plan-B.md ← 编码层抽象改进计划（B1–B4）
├── src/
│   ├── EasyRDP.Core/            # 协议 + 传输层共享库 (net40;net8.0)
│   │   ├── Protocol/            # 消息类型、编解码、BinaryPacker、CompressHelper
│   │   └── Transport/           # ITransportClient/ITransportServer + TCP/UDP 实现
│   ├── EasyRDP.Server/          # 控制台服务端 (.NET 8) — WPF/Avalonia 开发期入口
│   ├── EasyRDP.Client/          # 客户端 (.NET 8-windows) — WPF/Avalonia 开发期入口
│   ├── EasyRDP.Server.Wpf/      # 服务端 .NET 4 + WPF (XP 兼容) ⏳
│   ├── EasyRDP.Server.Avalonia/ # 服务端 .NET 8 + Avalonia ⏳
│   ├── EasyRDP.Client.Wpf/      # 客户端 .NET 4 + WPF (XP 兼容) ⏳
│   └── EasyRDP.Client.Avalonia/ # 客户端 .NET 8 + Avalonia ⏳
└── EasyDesk/                    ← Git 子模块（桌面 I/O 核心库）
```

---

## 开发路线图

| 阶段 | 模块 | 状态 |
|---|---|---|
| **Phase 1** | EasyDesk — 桌面 I/O 抽象库（接口 + Windows P/Invoke 实现） | ✅ 已完成 |
| **Phase 2** | 自定义协议设计 — 消息类型定义、二进制编解码、版本协商 | ✅ 已完成 |
| **Phase 3** | 传输层 — ITransportClient/ITransportServer 抽象 + TCP/UDP 实现 + PacketFramer + Options | ✅ 已完成 |
| **Phase 4** | 屏幕编解码 — Deflate 压缩 + 脏矩形增量检测 | ✅ 已完成 |
| **Phase 5** | 剪贴板同步 — 双向同步 + 静默期防死循环 | ✅ 已完成 |
| **Phase 6** | 服务端 / 客户端 UI — WPF (.NET 4, XP 兼容) + Avalonia (.NET 8, 跨平台) | ⏳ 进行中 |
| **Phase 7** | 跨平台扩展 — Linux X11、macOS 实现 | ⏳ 计划中 |
| **Phase 8** | 编码层抽象（B1–B4）— 可插拔编码后端：Bitmap / H.264 软编 / H.264 硬编 | 🔄 进行中（B-2 协商基础 ✅） |

> Phase 8 详见 [docs/EasyRDP-Codec-Plan-B.md](docs/EasyRDP-Codec-Plan-B.md)：在不破坏 XP 兼容性的前提下
> 引入可插拔编码层，H.264 代码用 `#if NET8_0_OR_GREATER` 隔离，net40/XP 路径保留 Bitmap 兜底。

---

## 注意事项

### 双框架策略

| 版本 | 框架 | UI | 目标系统 | 定位 | 状态 |
|---|---|---|---|---|---|
| .NET Framework 4.0 | WPF | WPF | Windows XP ~ 11 | 兼容老旧系统（XP 工控机、ATM 等） | ⏳ 进行中 |
| .NET 8 | Avalonia | 跨平台 XAML | Windows 7+, Linux, macOS | 新功能主线，未来跨平台基础 | ⏳ 进行中 |

- **两套 UI 不共享代码**，各自独立实现界面，但通过 `EasyRDP.Core` 引用同一套协议逻辑
- `EasyRDP.Core` 多目标 `net40;net8.0`，使用 `#if NET8_0_OR_GREATER` 条件编译隔离版本差异
- EasyDesk 本身已 `net40;netstandard2.0` 双目标，两个版本均直接引用，无需额外适配

### 剪贴板：STA 线程要求

使用剪贴板 API 的线程必须是 **STA (Single-Threaded Apartment)**：

```csharp
var thread = new Thread(() =>
{
    var clip = factory.CreateClipboardService();
    clip.SetText("hello");
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();
```

### 屏幕捕获内存管理

`CaptureScreen()` 返回的 `ScreenFrame.Scan0` 指向新分配的堆内存，**调用者负责释放**，否则每次截屏泄漏 `Width × Height × 4` 字节：

```csharp
var frame = capturer.CaptureScreen();
try { /* 处理像素数据... */ }
finally { Marshal.FreeHGlobal(frame.Scan0); }
```

### 线程安全

| 组件 | 线程安全 | 说明 |
|---|---|---|
| `IInputSimulator` | ✅ | `SendInput` 是 Win32 原子 API |
| `IScreenCapturer` | ❌ | 内部 DC/Bitmap 非线程安全，需外部加锁 |
| `ICursorCapturer` | ✅ | 仅读取，无共享状态 |
| `IClipboardService` | ❌ | 必须在同一线程上序列化调用 |

### .NET 4.0 / C# 5.0 语法约束

EasyDesk 双目标 `net40`，所有代码必须兼容 C# 5.0。禁止使用 `$` 字符串插值、`async/await`、`?.`、`nameof()`、表达式体成员等 C# 6.0+ 语法。详见 [EasyDesk/README.md](EasyDesk/README.md)。

---

## 适用场景

- 局域网远程桌面（替代 VNC / Radmin / TeamViewer LAN 模式）
- 远程协助与技术支持
- 机房服务器集中管控
- UI 自动化测试（屏幕验证 + 远程输入注入）
- 桌面监控与录屏

---

## 许可证

MIT License — 详见 [EasyDesk/LICENSE](EasyDesk/LICENSE)。

---

## 更多文档

- 协议规范：**[docs/EasyRDP-Protocol-v1.md](docs/EasyRDP-Protocol-v1.md)**
- 编码层改进计划：**[docs/EasyRDP-Codec-Plan-B.md](docs/EasyRDP-Codec-Plan-B.md)**（B1–B4 可插拔编码后端）
- 开发规范：**[EasyDesk/AGENTS.md](EasyDesk/AGENTS.md)**
- API 参考：**[EasyDesk/README.md](EasyDesk/README.md)**
