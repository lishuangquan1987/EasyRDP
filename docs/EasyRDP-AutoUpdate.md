# EasyRDP 自动更新（aly）接入说明

EasyRDP 客户端（`EasyRDP.Client.Wpf`）与服务端（`EasyRDP.Server.Wpf`）已接入
[aly](../aly/README.md) 自动更新系统，更新链路为：**检查（check）→ 下载（download）→ 应用（apply）**。
应用启动后由 `AlyClient.CSharpSDK` 的 `AlyUpdateClient` 在后台循环执行，状态通过事件驱动 UI。

## 目录结构

以客户端 `bin\Debug` 为例（服务端结构一致）：

```
bin\Debug\
├── net8.0-windows\               # ApplicationFolder：应用本体
│   ├── EasyRDP.Client.Wpf.exe    # 主程序
│   ├── aly-client.exe            # updater 副件（供 check_self_update 使用）
│   └── .updator\shared.json      # 服务端地址 / 项目名 / 忽略规则
└── UpdateFolder\                 # 更新器目录（应用更新时保持不变）
    ├── aly-client.exe            # 更新器运行入口（Go 1.10 / 386 / XP 兼容）
    ├── client.json               # 主程序相对路径 + 需关闭的进程名
    └── version.json              # 当前版本与状态机（applied/downloaded/applying）
```

`UpdateFolder` 由各项目 csproj 的 `DeployAlyUpdateFiles` 目标在构建时自动生成：

- 从 `aly/client/aly-client/aly-client.exe` 复制更新器到 `UpdateFolder/` 与 `ApplicationFolder/`
- 首次生成 `UpdateFolder/client.json` 与 `UpdateFolder/version.json`（已存在则不覆盖，保留运行期状态）
- 将 `UpdateConfig/shared.json` 复制为 `ApplicationFolder/.updator/shared.json`

> 注意：`client.json` / `version.json` 只在首次构建时生成。若以后调整了输出目录布局，
> 需要删除旧的 `bin\Debug\UpdateFolder` 后重新构建。

## 配置文件

### `UpdateFolder/client.json`

```json
{
  "main_exe_relative_path": "../net8.0-windows/EasyRDP.Client.Wpf.exe",
  "must_close_process_name": ["EasyRDP.Client.Wpf"]
}
```

- `main_exe_relative_path`：主程序相对 `UpdateFolder/` 的路径
- `must_close_process_name`：应用更新前需要关闭的进程名（发送 WM_CLOSE，超时后强制结束）

### `ApplicationFolder/.updator/shared.json`

```json
{
  "server_url": "http://127.0.0.1:2000",
  "project_name": "easyrdp-client",
  "ignore_folders": ["Log", "Logs"],
  "ignore_files": [],
  "un_copy_folders": [],
  "un_copy_files": []
}
```

- `server_url`：aly 更新服务端地址（默认本机 `2000` 端口，部署时改成实际地址）
- `project_name`：服务端项目名，客户端 `easyrdp-client`、服务端 `easyrdp-server`
- `ignore_folders` / `ignore_files`：发布时跳过、且客户端不参与差异比对的路径（日志等）
- `un_copy_folders` / `un_copy_files`：apply 时**不**从旧目录复制到新版本目录的路径（运行期生成的文件）

`shared.json` 属于构建产物，随每个版本上传到服务端，客户端更新后自动同步。

## 发布更新

以客户端为例（服务端把 `project_name` 换成 `easyrdp-server`、目录换成对应输出目录）：

```bash
# 1. 启动 aly 服务端（默认端口 2000，SQLite 自动建库）
cd aly/server
go run . -p 2000

# 2. 进入发布源目录（客户端构建输出目录）
cd src/EasyRDP.Client.Wpf/bin/Debug/net8.0-windows

# 3. 首次使用：初始化配置并创建服务端项目（后续可省略）
aly-publish config init --server http://127.0.0.1:2000 --project easyrdp-client
aly-publish project create --name easyrdp-client --title "EasyRDP Client"

# 4. 每次发版：查看差异 → 暂存 → 推送
aly-publish status
aly-publish add --all
aly-publish push --version 1.0.1 --message "发布说明"
```

发布完成后，客户端 `AlyUpdateClient` 的 `check_update` 会在一秒内探测到新版本：

- 非强制更新：UI 显示更新按钮（状态文本），点击确认后下载、再点击确认后应用；
- 强制更新（`force_update`）：自动下载并应用，无需用户确认；
- 应用更新时会关闭 `must_close_process_name` 指定的进程，原子替换目录后自动重启主程序。

## 多目标（net40）说明

`EasyRDP.Server.Wpf` 同时编译 `net40` 与 `net8.0-windows`。两个目标的 `OutDir` 均为
`bin\Debug\<TFM>\`，`UpdateFolder` 统一收敛到 `bin\Debug\UpdateFolder`，
其中 `client.json` 指向 `net8.0-windows` 主程序。
当前以 `net8.0-windows` 作为受更新管理的部署目标；若需要维护 `net40` 独立发布，
请为 net40 单独配置 `client.json` 并调整 `UpdateConfig/shared.json` 的项目名。

## 相关代码

- `src/EasyRDP.Client.Wpf/EasyRDP.Client.Wpf.csproj`、`src/EasyRDP.Server.Wpf/EasyRDP.Server.Wpf.csproj`：部署目标
- `src/EasyRDP.Client.Wpf/UpdateConfig/shared.json`、`src/EasyRDP.Server.Wpf/UpdateConfig/shared.json`：共享配置模板
- `src/EasyRDP.Client.Wpf/MainWindowViewModel.cs`、`src/EasyRDP.Server.Wpf/MainWindowViewModel.cs`：更新状态/命令
- `aly/client/aly-client-sdk/`：`AlyClient.CSharpSDK` 源码
