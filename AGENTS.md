# EasyRDP

跨平台远程桌面应用的 monorepo。当前处于设计阶段 — 五层数据管线架构已完整定义，待实现。

## 项目概述

- **目标**：远程桌面 — 截屏 → 编码 → 传输 → 解码 → 渲染，上层为编排层（Session）
- **设计文档**：`docs/EasyRDP-Abstraction-Layers-Design.md`（v2.7，约 96KB，权威规范）
- **技术栈**：C# (.NET)、Go、Avalonia 11.3
- **结构**：根目录下两个 git submodule，见 `.gitmodules`

### 子模块

| 路径 | 仓库 | 职责 |
|------|------|------|
| `EasyDesk/` | EasyDesk | 桌面 I/O 抽象库 — 屏幕捕获、键鼠模拟、光标、剪贴板、桌面信息（net40/netstandard2.0，纯 P/Invoke） |
| `aly/` | aly | 自动更新系统 — 服务端 (Go/Gin/Ent/SQLite)、客户端 (Go 1.10，兼容 XP)、发布 CLI (Go/cobra)、发布 GUI (Avalonia/.NET 8) |

每个子模块有自己的 `AGENTS.md`，进入对应目录工作前必须先读取。

### 根目录文件

- `docs/EasyRDP-Abstraction-Layers-Design.md` — 五层架构规范（Capture / Encode / Transport / Decode / Render + Session 编排）
- `reasonix.toml` — Reasonix agent 配置

## 命令

### EasyDesk（仅 Windows）

```bash
dotnet build EasyDesk/EasyDesk.sln
dotnet test EasyDesk/test/EasyDesk.Windows.Tests/EasyDesk.Windows.Tests.csproj
```

测试会真实移动鼠标/按键/操作剪贴板，运行时不要操作机器。

### aly — 服务端

```bash
cd aly/server
go run . -p 2000                       # 启动服务，默认端口 2000
go run . -p 2000 -db /path/to/aly.db   # 指定数据库路径
```

首次运行自动创建 SQLite 数据库。

### aly — 发布 CLI

```bash
cd aly/publish/publish-cli
go run . config init --server http://localhost:2000 --project myapp
go run . status
go run . add --all
go run . push --version V1.0.1 --message "发布说明"
```

### aly — 发布 GUI

```bash
dotnet run --project aly/publish/publish-gui/src/AlyPublish/AlyPublish.csproj
```

### aly — 客户端（兼容 XP，Go 1.10，GOPATH 模式，386）

```bash
# 构建：先将 client/ 复制到 GOPATH/src/aly/client/，然后：
set GOOS=windows && set GOARCH=386 && set GO111MODULE=off
go build -ldflags="-s -w" -o aly-client.exe .
```

详见 `aly/client/aly-client/build.bat`。

## 架构

```
EasyRDP/                          # Monorepo 根 — 项目级文档、共享配置
├── docs/
│   └── EasyRDP-Abstraction-Layers-Design.md   # 主设计文档：五层管线规范
├── EasyDesk/                     # 子模块：桌面 I/O（截屏+输入层的构建基石）
│   ├── src/EasyDesk.Core/        #   接口 + 模型
│   ├── src/EasyDesk.Windows/     #   Windows P/Invoke 实现
│   └── test/                     #   xUnit 集成测试
└── aly/                          # 子模块：自动更新系统
    ├── server/                   #   API 服务端 (Go/Gin/Ent/SQLite)
    ├── client/aly-client/        #   更新器可执行文件 (Go 1.10, 386, 兼容 XP)
    ├── publish/publish-cli/      #   发布 CLI (Go/cobra)
    └── publish/publish-gui/      #   发布 GUI (Avalonia 11.3, .NET 8)
```

EasyRDP 的核心管线（`docs/` 中的规范）将在根目录下实现 — 目前处于设计阶段，尚未开始编码。EasyDesk 提供截屏基元；aly 负责分发和更新。

## 约定

### 跨模块

- 每个子模块有独立的 `AGENTS.md` — **进入对应目录工作前必须先读取**
- 子模块独立版本管理；clone 后执行 `git submodule update --init`
- 子模块指针变更需显式提交（指向特定 commit）
- 设计文档是唯一权威来源 — 接口变更必须与之保持同步

### 各语言规范

- EasyDesk：C# 5.0（net40 目标 — 禁止字符串插值、`?.`、表达式体成员等 C# 6+ 语法）。详见 `EasyDesk/AGENTS.md`
- aly server / publish-cli：现代 Go（go.mod，modules）
- aly client：Go 1.10，GOPATH 模式，`GOARCH=386`，禁用 modules — 必须编译为 Windows XP 可运行
- aly publish-gui：.NET 8，Avalonia 11.3，CommunityToolkit.Mvvm，Semi.Avalonia，Serilog。详见 `aly/AGENTS.md`
- 编码规范
  - 每个类一个文件，每个类，每个属性、方法必须添加注释
  - wpf/avalonia除了viewmodel可以引用view外，其余必须严格遵守MVVM的设计
  - 关键逻辑添加必要的注释
  - 尽量避免魔法字符串

### 中文注意事项

- `aly/` 的文档、注释、AGENTS.md 使用中文
- EasyDesk 和设计文档使用英文
- PowerShell `Set-Content` 不加 `-Encoding UTF8` 会把中文按 GBK 编码导致乱码 — 编辑含中文的文件时用 Python

## 备注

- H.264 硬件/软件编码是强制要求，不允许回退到原始像素绕过编码问题。OpenH264 仅支持 I420 (YUV) 输入，所有 BGRA 截屏数据必须先做 BGRA→I420 颜色空间转换再送入编码器。
- OpenH264 v2.6.0 本地源码路径：`E:\DownloadCode\openh264`。核对结构体字节对齐、vtable 槽位映射、接口方法签名等无需到 GitHub 翻阅源码，直接本地查看。关键文件：
  - `codec/api/wels/codec_app_def.h` — SFrameBSInfo / SLayerBSInfo / SSourcePicture / SDecodingParam / SBufferInfo 等结构体定义
  - `codec/api/wels/codec_api.h` — ISVCEncoder / ISVCDecoder 接口声明（vtable 槽位顺序）
  - `codec/encoder/plus/src/welsEncoderExt.cpp` — 编码器实现（验证接口方法顺序）
  - `codec/decoder/plus/src/welsDecoderExt.cpp` — 解码器实现（验证接口方法顺序）
- 预留，后续快速追加。
