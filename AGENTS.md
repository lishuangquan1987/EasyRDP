# EasyRDP

轻量级局域网远程桌面工具 — 自定义协议 + 可扩展传输层（TCP 默认），当前 Console Server + WinForms Client (.NET 8)，计划 WPF (.NET 4) + Avalonia (.NET 8) 双 UI。底层 EasyDesk 桌面 I/O 库驱动。

## Project

- **Stack**: C#, dual-framework — `net40` (XP 兼容) + `net8.0-windows` (WinForms, 当前实现) + `net8.0` (Avalonia, 计划)
- **Submodule**: `EasyDesk/` — 桌面 I/O 库 (net40;netstandard2.0, 零依赖 P/Invoke)
- **Current phase**: EasyDesk ✅ | EasyRDP.Core + Server(Console) + Client(WinForms) ✅ | WPF/Avalonia UIs ⏳
- **Entry**: `src/EasyRDP.Server/Program.cs` (server) / `src/EasyRDP.Client/Program.cs` (WinForms client)
- **Config**: `src/EasyRDP.Server/appsettings.json` (port, auth token, compression, frame rate)

## Commands

```bash
# Clone (submodule)
git clone --recurse-submodules https://github.com/lishuangquan1987/EasyRDP.git
git submodule update --init --recursive   # if already cloned

# Build (full solution)
dotnet build

# Run server (default port 8750, config: appsettings.json)
dotnet run --project src/EasyRDP.Server

# Run client (WinForms window, connect to localhost or [ip])
dotnet run --project src/EasyRDP.Client [server-ip]

# Test EasyDesk (⚠️ moves mouse/presses keys)
cd EasyDesk
dotnet test test/EasyDesk.Windows.Tests/EasyDesk.Windows.Tests.csproj
```

## Architecture

```
EasyRDP/
├── src/
│   ├── EasyRDP.Core/             # 协议 + 传输共享库 (net40;net8.0)
│   │   ├── Protocol/             # 消息类型、编解码、BinaryPacker、CompressHelper
│   │   └── Transport/            # ITransportChannel 接口 + Tcp/UdpChannel + Server/Client
│   ├── EasyRDP.Server/           # 控制台服务端 (.NET 8) ✅
│   ├── EasyRDP.Client/           # WinForms 客户端 (.NET 8-windows) ✅
│   ├── EasyRDP.Server.Wpf/       # .NET 4 + WPF 服务端 (XP 兼容) ⏳
│   ├── EasyRDP.Server.Avalonia/  # .NET 8 + Avalonia 服务端 ⏳
│   ├── EasyRDP.Client.Wpf/       # .NET 4 + WPF 客户端 (XP 兼容) ⏳
│   └── EasyRDP.Client.Avalonia/  # .NET 8 + Avalonia 客户端 ⏳
└── EasyDesk/                     ← Git 子模块（桌面 I/O 库）
    ├── src/EasyDesk.Core/         # 5 接口 + 8 模型
    ├── src/EasyDesk.Windows/      # P/Invoke 实现 (user32/gdi32/kernel32)
    └── test/                      # xUnit 集成测试
```

- **EasyRDP.Core** — 协议编解码 + Deflate 压缩 + 可扩展传输（`ITransportChannel`，默认 `TcpChannel`）。多目标 `net40;net8.0`。
- **CompressHelper** — DeflateStream 压缩/解压，兼容 net40。`Protocol/CompressHelper.cs`
- **TransportMode 枚举** — `Tcp`（默认）、`TcpAndUdp`（可选），通过 `ITransportChannel` 接口可扩展 WebSocket / NamedPipe / QUIC。
- **EasyDesk** — 5 个接口：`IInputSimulator`, `IScreenCapturer`, `ICursorCapturer`, `IClipboardService`, `IDesktopInfo`。详见 `EasyDesk/AGENTS.md`。

## Conventions

### Current implementation

| Aspect | Server | Client |
|---|---|---|
| Target | net8.0 | net8.0-windows |
| UI | Console | WinForms |
| Config | appsettings.json (JSON) | — |

### Dual framework (planned)

| Aspect | .NET 4 WPF | .NET 8 Avalonia |
|---|---|---|
| Target OS | Windows XP ~ 11 | Windows 7+, Linux, macOS |
| UI | WPF (System.Windows) | Avalonia (cross-platform XAML) |
| Role | XP compatibility only | New-feature trunk |
| Code sharing | Zero UI code shared | Zero UI code shared |

- **EasyRDP.Core** is the only shared library — all protocol + transport logic lives here.
- Use `#if NET8_0_OR_GREATER` conditional compilation in EasyRDP.Core when APIs diverge.
- New features go into .NET 8 first; backport to .NET 4 only if XP compatibility demanded.
- EasyDesk already targets `net40;netstandard2.0` — all stacks reference it directly.

### C# 5.0 (mandatory where net40)

EasyDesk and EasyRDP.Core must compile under C# 5.0 (net40 target). See `EasyDesk/AGENTS.md` for the full forbidden/alternative table. Key no-nos: string interpolation `$""`, `?.`, `nameof()`, expression-bodied members, `async/await`.

### General

- `using` directives inside `namespace` blocks
- XML doc comments on all public API
- No `.editorconfig` — manual consistency
- **ScreenFrame.Scan0** must always be `Marshal.FreeHGlobal`'d in `finally`
- **Clipboard** requires STA thread — `thread.SetApartmentState(ApartmentState.STA)`

## Notes

- Placeholder for future quick-adds.
