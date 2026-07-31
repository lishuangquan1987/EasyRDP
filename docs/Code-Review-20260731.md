# EasyRDP 全项目代码审查报告

> **审查日期**：2026-07-31
> **审查范围**：EasyRDP monorepo（含子模块 `EasyDesk@bceaa6a`、`aly@4b6dac5`）
> **审查方法**：逐文件深度审查 + 抽样复核 5 处最严重发现的真实性
> **审查文档**：五层管线架构规范见 `docs/EasyRDP-Abstraction-Layers-Design.md`

---

## 总览

| 严重程度 | 数量 | 涉及模块 |
|----------|------|----------|
| Critical | 4 | EasyRDP.Core / EasyDesk / aly client |
| High | 20 | 全部模块 |
| Medium | 15+ | 全部模块 |
| Low | 15+ | 全部模块 |

---

## 一、严重（Critical）— 必须优先修复

### A. EasyRDP.Core — 整数溢出绕过 BGRA 输出缓冲区大小检查（堆破坏，可远程触发）

- **位置**：`src/EasyRDP.Core/Protocol/H264DecoderNative.cs:156-182`
- **类别**：Native interop / Logic error / Integer overflow
- **问题**：

```csharp
int expectedBgraSize = w * h * 4;          // int32 乘法，可溢出
if (outputBuffer.Length < expectedBgraSize) // 溢出为负时此判断为 false
{
    return new DecodeResult { Status = DecodeStatus.Failed };
}
// 继续向 pinned 托管数组写入 w*h*4 字节
```

`w`/`h` 来自 H.264 SPS。恶意或异常码流只要让 `w*h > int.MaxValue/4 ≈ 5.36 亿`（如 w≈h≈23171），乘积即溢出为负数；`outputBuffer.Length < expectedBgraSize`（正 < 负）变 `false`，校验被绕过，随后向定长 pinned 托管数组写入 `w*h*4` 字节 → 堆破坏，潜在 RCE。

- **修复**：用 `long` 计算并加上限校验：

```csharp
long expectedBgraSize = (long)w * h * 4;
if (expectedBgraSize > outputBuffer.Length || expectedBgraSize > Constants.MaxSafePayloadSize)
    return new DecodeResult { Status = DecodeStatus.Failed };
// 同时校验 w、h 不超过合理上限（如 16384）
```

### B. EasyDesk — 颜色光标完全错乱（默认 Windows 箭头光标即触发）

- **位置**：`EasyDesk/src/EasyDesk.Windows/WindowsCursorCapturer.cs:105`（及 81-138）
- **类别**：Native interop / Logic error
- **问题**：

```csharp
height = Math.Abs(bmiQuery.bmiHeader.biHeight) / 2; // doubled: XOR + AND
```

无条件除 2。Win32 `ICONINFO` 约定：
- **颜色光标**（现代 Windows 桌面默认箭头带阴影即属此类）：`hbmMask` 仅含 AND 掩码（高度=光标高），`hbmColor` 才是颜色 XOR 掩码
- **黑白光标**：`hbmMask` 才是 AND+XOR 双倍高

代码对颜色光标也 `/2` → 高度减半；并且全程只读 `hbmMask`，从不读 `hbmColor`。

- **后果**：现代 Windows 桌面抓出来的光标是半高 + 错误的 XOR 像素，本质是垃圾数据。
- **修复**：检查 `ii.hbmColor != IntPtr.Zero`。颜色光标设 `height = abs(biHeight)`（不除 2），通过单独 `GetDIBits`（32bpp BGRA）读 `hbmColor`；黑白光标保持 `/2` 并读 `hbmMask` 两半。

### C. EasyDesk — 单色光标 AND/XOR 掩码互换

- **位置**：`EasyDesk/src/EasyDesk.Windows/WindowsCursorCapturer.cs:151-178`
- **类别**：Logic error / Native interop
- **问题**：bitmap 以 `biHeight = -(height*2)` 请求为 top-down，row 0 在顶。代码却写：

```csharp
// Extract AND mask from bottom half (BGRA32 → 1bpp)
int srcOffset = (height + row) * width * 4 + col * 4;   // 读下半
// Copy XOR mask from top half
int srcOffset = row * width * 4;                          // 读上半
```

MSDN 明确：上半是 AND、下半是 XOR。二者被读反。

- **后果**：单色光标透明区与可见区互换，形状反相。
- **修复**：交换源偏移 — AND 从 `row*width*4`，XOR 从 `(height+row)*width*4`。（需在 B 修复后分色/单色路径分离的前提下。）

### D. aly client — 崩溃恢复逻辑销毁上一版本备份，回滚永久失效

- **位置**：`aly/client/aly-client/cmd/apply_update.go:48-196`
- **类别**：Logic error / File handling
- **问题**：若 `versionDir → mainFolder` 重命名（L174）成功但写入 `applied` 状态（L199）前崩溃，下次启动进入 L50 的崩溃恢复分支"fall through"重跑全流程：

1. L123 `CopyDirWithExclude(mainFolder, versionDir)` — 把**新版本**（已在 mainFolder 里）拷到 versionDir
2. L148-149 把 prevVersionDir（**旧版本**）改名为 `oldBackupTemp`
3. L157 把 mainFolder（**新版本**）改名为 prevVersionDir
4. L193 `os.RemoveAll(oldBackupTemp)` — **旧版本被永久删除**

- **后果**：恢复后 `prevVersionDir` 装的是新版本，`rollback` 命令再也无法回滚到真正的旧版本。
- **修复**：恢复分支先比对 `mainFolder` 内容与 `versionInfo.Version`，若已是目标版本则直接写 `applied` 状态返回，跳过整套替换。

---

## 二、高危（High）

### EasyRDP.Core

#### 1. 重组超时 5s 破坏大控制面消息

- **位置**：`src/EasyRDP.Core/Transport/MessageReassembler.cs:321-329` + `src/EasyRDP.Core/Protocol/Constants.cs:15`
- **问题**：超时计时器在 `StartNewFrame` 启动后从不重置，按"总耗时"而非"空闲时间"判定。1 MB 文件剪贴板分片（750 个分片）在 < 1.6 Mbps 链路上必超 5s，超时后所有已收分片被丢弃，下载永远完不成。
- **修复**：控制面消息豁免该超时，或改为按空闲时间判定，或按 `_totalPayloadLen` 缩放超时。

#### 2. `FragAndSend` 静默截断 > 91.7 MB 载荷

- **位置**：`src/EasyRDP.Core/Transport/MessageReassembler.cs:157-182`
- **问题**：`fragCount` 超 65535 时被静默钳到 65535，尾部数据永不发送，接收方按头部长度分配缓冲却只填头部 → 静默损坏。
- **修复**：替换为显式拒绝：`throw new InvalidOperationException("payload too large to fragment")`。

#### 3. `ConvertBgraToI420` 奇数尺寸写越界

- **位置**：`src/EasyRDP.Core/Protocol/H264EncoderNative.cs:149-153`（分配）、`272-294`（转换）、`H264Native.cs:115-120`（stride）
- **问题**：奇数 w/h 时 U/V 平面分配 `ySize/4`、stride 取 `w/2`，但实际需要 `ceil(w/2)*ceil(h/2)`，`uvIndex` 会写到平面末尾之外，覆盖 V 平面及后续分配。`Initialize` 未校验偶数。
- **修复**：`Initialize` 拒绝奇数尺寸，或 `uvSize = ((w+1)/2) * ((h+1)/2)`、`iStride1 = iStride2 = (w+1)/2`。

### EasyRDP WPF（核心架构问题：Stop 顺序错位）

#### 4. `Stop` ↔ 渲染线程 Join 死锁

- **位置**：`src/EasyRDP.Client.Wpf/ClientStreamSession.cs:147` + `src/EasyRDP.Client.Wpf/WpfRenderTarget.cs:67-84`
- **问题**：渲染线程内同步 `_uiDispatcher.Invoke(...)`，UI 线程 `Stop()` 又 `Join(3000)` 等渲染线程 → 经典重入死锁，每次断开卡 UI ~3s。
- **修复**：`WpfRenderTarget.RenderFrame` 改用 `BeginInvoke`，并在 Invoke 之前把像素数据从 FrameBuffer 槽位拷出（在 `ReleaseReadFrame` 之前）。

#### 5. 致命错误回调二次触发同一死锁

- **位置**：`src/EasyRDP.Client.Wpf/MainWindowViewModel.cs:419-430`
- **问题**：`ClientStreamSession.RaiseFatal` 在 transport 接收线程触发，handler 用 `_dispatcher.Invoke(Stop)` 同步跨线程，再次命中死锁；同时阻塞接收线程，TCP 接收缓冲可能被填满反压服务端。
- **修复**：改 `_dispatcher.BeginInvoke`，并修复底层死锁。

#### 6. `_decoder`/`_frameBuffer` 竞态（可能 AccessViolation）

- **位置**：`src/EasyRDP.Client.Wpf/ClientStreamSession.cs:120-155` vs `src/EasyRDP.Client.Wpf/MainWindowViewModel.cs:1069-1070`
- **问题**：`MainWindowViewModel.Stop` 先 `_streamSession.Stop()` 后 `_transport.Disconnect()`，但 transport 接收线程仍可能在 `ProcessVideoFrame` 中调用 `_decoder.Decode`，而 `Stop` 已将 `_decoder` 置 null/Dispose → 可能 `AccessViolationException`（默认 .NET 4+ 策略下不可捕获）。
- **修复**：`MainWindowViewModel.Stop` 调换顺序，先 `_transport?.Disconnect()`（并等接收线程退出）再 `_streamSession?.Stop()`。

#### 7. `TcpTransportServer` 握手前不限连接数

- **位置**：`src/EasyRDP.Server.Wpf/TcpTransportServer.cs:126-167`
- **问题**：`AcceptLoop` 给每个 TCP 连接都分配 sessionId + 接收线程，握手阶段才检查 `_maxSessions=2`。恶意客户端可不开握手就耗尽 FD/线程/内存。
- **修复**：在 `AcceptLoop` 内对 `_clients.Count` 设硬上限，或用 `SemaphoreSlim` 限制 accept 数。

#### 8. 心跳保持发起点持锁阻塞

- **位置**：`src/EasyRDP.Server.Wpf/TransportHost.cs:1241-1258`
- **问题**：`SendTo` 是阻塞网络 I/O，慢客户端会冻结整个 `_lock` 保护的所有会话管理操作（`OnDataReceived`、`OnClientConnected`、`DisconnectSession` 等）。
- **修复**：锁内只收集待发 keepalive 的会话列表，释放锁后再 `SendTo`。

### EasyDesk

#### 9. `SendMouseMove` 绝对坐标不支持多显示器

- **位置**：`EasyDesk/src/EasyDesk.Windows/WindowsInputSimulator.cs:44-48`
- **问题**：用 `SM_CXSCREEN`（主显示器）做映射且未设 `MOUSEEVENTF_VIRTUALDESK(0x4000)`，副屏不可达。`MouseEventFlags` 枚举也缺 `VirtualDesk` 成员。
- **修复**：加 `VirtualDesk = 0x4000`，绝对坐标时 OR 进 `dwFlags`，改用 `SM_CXVIRTUALSCREEN`/`SM_CYVIRTUALSCREEN` + `SM_XVIRTUALSCREEN`/`SM_YVIRTUALSCREEN` 偏移。

#### 10. GDI 位图泄漏（失败路径）

- **位置**：`EasyDesk/src/EasyDesk.Windows/WindowsScreenCapturer.cs:79-120`
- **问题**：`SelectObject(hdcMem, hOldBitmap)`（L115）在 `try` 体内而非 `finally`；`BitBlt`/`GetDIBits` 抛异常时 `hBitmap` 仍选入 DC，`finally` 的 `DeleteObject` 会失败（Win32 规则：选入 DC 的对象删除返回 FALSE）→ GDI 句柄泄漏，累计达 1 万会话级上限即全屏渲染失败。
- **修复**：把 `SelectObject(hdcMem, hOldBitmap)` 放进独立 `finally`，确保删除前先还原选择。

#### 11. DXGI 多处 COM 对象泄漏 + 部分构造失败泄漏 D3D11 设备

- **位置**：`EasyDesk/src/EasyDesk.Windows/DxgiScreenCapturer.cs:39-86`（`factory`/`output1` 永不 Dispose）、`EasyDesk/src/EasyDesk.Windows/WindowsDesktopFactory.cs:24-33`（构造失败 catch 吞异常但 `_device` 已分配 → 泄漏 D3D11 设备）
- **修复**：`Initialize` 体内用 `try/finally` 包裹所有 COM 局部变量；工厂 catch 中 Dispose 已部分构造的实例。

#### 12. `DxgiScreenCapturer.CaptureRegion` 无越界校验

- **位置**：`EasyDesk/src/EasyDesk.Windows/DxgiScreenCapturer.cs:185-221`
- **问题**：`CopyMemory` 直读 `full.Scan0` 越界 → `AccessViolationException`/`SEHException` 崩溃；负 x/y 下溢。
- **修复**：clamp 或校验 `x >= 0 && y >= 0 && x+width <= full.Width && y+height <= full.Height`，越界抛 `ArgumentOutOfRangeException`。

#### 13. `IsClipboardFormatAvailable` 私有助手空转 500ms

- **位置**：`EasyDesk/src/EasyDesk.Windows/WindowsClipboardService.cs:271-281`
- **问题**：该 API 是 O(1) 即时查询、与剪贴板锁无关，却被错误地 retry 10×50ms。`GetText()` 在非文本剪贴板时阻塞半秒，捕获循环延迟尖峰。注意 `ContainsText()` 直接调用 API 无重试，行为不一致。
- **修复**：删除重试循环，单次调用即返回。

### aly

#### 14. `version.json` 非原子写

- **位置**：`aly/client/aly-client/config/version.go:68`
- **问题**：`ioutil.WriteFile` 截断式写。中途崩溃 → JSON 损坏，所有版本状态丢失，check/apply/rollback 全失效。
- **修复**：写 `.tmp` 后 `os.Rename`。

#### 15. 服务端分片合并非原子

- **位置**：`aly/server/controllers/file_upload_controller.go:220-241`
- **问题**：`os.Create` 先截断目标，复制失败留下半截文件，并发下载会读到损坏数据。
- **修复**：合并到 `.merging` 临时文件后原子 rename。

#### 16. 服务端 HTTP 无任何超时（Slowloris）

- **位置**：`aly/server/main.go:40-43`
- **问题**：`http.Server` 未设 `ReadTimeout`/`WriteTimeout`/`ReadHeaderTimeout`/`IdleTimeout`，易受慢速连接攻击。
- **修复**：

```go
srv := &http.Server{
    Addr:              fmt.Sprintf(":%d", port),
    Handler:           r,
    ReadHeaderTimeout: 10 * time.Second,
    ReadTimeout:       60 * time.Second,
    WriteTimeout:      60 * time.Second,
    IdleTimeout:       120 * time.Second,
}
```

#### 17. 服务端上传无大小限制

- **位置**：`aly/server/controllers/file_upload_controller.go:19-149`
- **问题**：`UploadFile`/`UploadChunk` 均未校验大小，可填满磁盘。
- **修复**：设 `r.MaxMultipartMemory`，校验 `f.Size` 与累计分片大小。

#### 18. 客户端 `DownloadFile`/`CopyFile` 非原子 + 未检查 `Close` 错误

- **位置**：`aly/client/aly-client/client/http_client.go:192-202`、`aly/client/aly-client/util/file.go:66-86`
- **问题**：`CopyDirWithExclude` 在 apply_update 中失败会留下半截文件随后被原子重命名激活 → 用户跑损坏的应用。`defer Close()` 不检错，磁盘满等 flush 失败被吞。
- **修复**：写临时文件 + 检 `Close()` 错误 + rename。

#### 19. `ProcessService.Kill()` 不杀子进程树

- **位置**：`aly/publish/publish-gui/src/AlyPublish/Services/ProcessService.cs:59, 175`
- **问题**：.NET 8 中 `Kill()` 等价 `Kill(false)`，仅杀本进程，CLI 子进程变孤儿占用资源/锁文件。
- **修复**：`proc.Kill(true)`（整树）。

#### 20. push 命令参数注入

- **位置**：`aly/publish/publish-gui/src/AlyPublish/Services/CliService.cs:183-188`
- **问题**：

```csharp
var args = $"push --version \"{version}\" --message \"{message}\"";
```

未转义双引号，含 `"` 的提交信息可注入 CLI 参数。
- **修复**：用 `ProcessStartInfo.ArgumentList`（.NET 8）自动转义。

---

## 三、中危（Medium）摘要

### EasyRDP.Core

- **`frameId` uint 回绕**：`src/EasyRDP.Core/Transport/MessageReassembler.cs:303-313`，无符号比较。`uint.MaxValue` 帧（60fps ≈ 828 天）后所有新帧被判为 stale 永久丢弃。修复：用有符号差值 `int delta = (int)(frameId - _currentFrameId)`。
- **`FramingBuffer` 末位 magic 字节被丢弃**：`src/EasyRDP.Core/Transport/FramingBuffer.cs:42-69`，扫描循环停在 `_bufferPos-2`，末位若为 magic 则整缓冲被丢弃，下一 Feed 又丢后续字节 → 帧永久丢失。修复：清空前保留末位 magic 字节。

### EasyRDP WPF

- **FPS 计数器永远为 0**：`src/EasyRDP.Client.Wpf/MainWindowViewModel.cs:474,514-522`，VM 的 `_frameBuffer` 与 session 的不是同一个。修复：读 `_streamSession.FrameCount`。
- **`_serverImageReceivers` 断连未清理**：`src/EasyRDP.Server.Wpf/TransportHost.cs:81-82`，大 DIB 内存泄漏。修复：`DisconnectSession`/`Stop` 中按 sessionId 前缀清理。
- **多会话共享一个 `IInputSimulator` 无锁**：`src/EasyRDP.Server.Wpf/MainWindowViewModel.cs:159-177`，`SendInput` 进程级非线程安全。修复：加锁或单输入线程队列。
- **`WpfRenderTarget.RenderFrame` 抛 `ObjectDisposedException` 杀渲染线程**：`src/EasyRDP.Client.Wpf/WpfRenderTarget.cs:48-51`。修复：`_disposed` 时早返回不抛。
- **`_transport.OnLog` 同步 `Invoke`**：`src/EasyRDP.Client.Wpf/MainWindowViewModel.cs:384`。改 `BeginInvoke`。
- **`_heartbeatTimer.Dispose()` 未等回调**：`src/EasyRDP.Client.Wpf/MainWindowViewModel.cs:1066`，`_transport` null 化竞态。用 `Dispose(WaitHandle)` + `WaitOne`。
- **`ServerStreamSession.Stop` 孤儿已编码帧**：`src/EasyRDP.Server.Wpf/ServerStreamSession.cs:194-201`。

### EasyDesk

- **`DxgiScreenCapturer` `Reinitialize` 失败后永久静默返回空帧**：`EasyDesk/src/EasyDesk.Windows/DxgiScreenCapturer.cs:165-180`。
- **`GetAllScreens` `factory` 泄漏**：`EasyDesk/src/EasyDesk.Windows/DxgiScreenCapturer.cs:240-264`。
- **`CreateVideoEncoder` OS 版本门槛误把 Win7 当 Win8**：`EasyDesk/src/EasyDesk.Windows/WindowsDesktopFactory.cs:45-46`，MF H.264 MFT 实为 Win8+，应 `>= 2` 而非 `>= 1`。
- **`CaptureRegion` `regionBuffer` 泄漏（CopyMemory 失败时）**：`EasyDesk/src/EasyDesk.Windows/DxgiScreenCapturer.cs:196-220`。
- **`SelectObject` 返回值未校验**：`EasyDesk/src/EasyDesk.Windows/WindowsScreenCapturer.cs:79`。

### aly

- **`db.SetMaxOpenConns(1)` 串行化所有读写**：`aly/server/internal/db/db.go:80`，建议开 WAL + 读写分离池。
- **`DownloadFileWithResume` 完成后未校验文件大小**：`aly/client/aly-client/client/http_client.go:281-313`。
- **`ResetSelectedAsync` 先清空再重加，中途失败丢全部暂存**：`aly/publish/publish-gui/src/AlyPublish/ViewModels/ProjectTabViewModel.cs:289-309`。
- **push 重试可能产生重复 change log**：`aly/publish/publish-cli/internal/cmd/push.go:155-166`。
- **`DeleteProject` 未过滤 `is_deleted` 也不校验 `affected`**：`aly/server/internal/service/project_service.go:201-208`。
- **publish-gui / publish-cli 多处配置 JSON 非原子写**：`ConfigService.cs:109,180`、`staging.go:53`、`config.go:108`。
- **`KillProcessesAndWait` 返回值被忽略**：`aly/client/aly-client/cmd/common.go:186`，进程未杀后续 rename 失败。

---

## 四、低危（Low）摘要

### EasyRDP.Core
- `FileClipboardConsumer` `ManualResetEventSlim` Dispose 与 `Set` 竞态可能崩接收线程（`src/EasyRDP.Core/Protocol/FileClipboardConsumer.cs:510-583, 118-145`）。

### EasyRDP WPF
- `SecretProtector` 未清零敏感缓冲（`src/Shared/SecretProtector.cs:44-76, 91-109`）。
- `CursorTracker.PollLoop` 静默吞异常无日志（`src/EasyRDP.Server.Wpf/CursorTracker.cs:110-124`）。
- `TcpTransportServer.Stop` 未 Join 接收线程（`src/EasyRDP.Server.Wpf/TcpTransportServer.cs:48-67`）。
- `TransportHost.SendResponse` 死代码 `sentFragments`（`src/EasyRDP.Server.Wpf/TransportHost.cs:1174-1177`）。
- DPAPI 解密失败静默丢密码无 UI 提示（`src/EasyRDP.Client.Wpf/ConnectionProfile.cs:88`）。
- `MainWindow.xaml.cs` 递归 BeginInvoke 无节流（`src/EasyRDP.Client.Wpf/MainWindow.xaml.cs:31-54`）。
- `CursorTrackerSession.AttachSendTo` 与 `SendCursorUpdate` 无锁竞态（`src/EasyRDP.Server.Wpf/CursorTracker.cs:210-214, 235-242`）。

### EasyDesk
- 项目级 `using` 在 namespace 外（违反 `EasyDesk/AGENTS.md`，58 处）。
- `IVideoEncoder.cs` 一个文件两个类型（违反"每个类一个文件"）。
- `OpenClipboardWithRetry` 对 error 0 也 retry（`WindowsClipboardService.cs:296-298`）。
- `CopyMemory` P/Invoke 重复声明（`Kernel32.cs:10-11` vs `DxgiScreenCapturer.cs:295-296`，`uint` vs `int`）。

### aly
- 服务端一律返回 HTTP 200（即便业务失败），违反 HTTP 语义。
- 软删项目磁盘文件不清理（`project_service.go:201-208`）。
- mock server API 与真实 server 不一致（用 id vs name，`test/mock_server.go:211-213, 242-244`）。
- URL 末尾 `/` 导致 `//`（`publish-cli/internal/api/client.go:36,72,...`）。
- ignore 模式错误被静默忽略（`publish-cli/internal/diff/scanner.go:264,269`）。

---

## 五、跨模块共性问题（不合理之处）

1. **跨线程同步大量用 `Dispatcher.Invoke`（同步）而非 `BeginInvoke`** — 多个死锁/背压根因。EasyRDP WPF 至少 4 处。
2. **"非原子写 + 截断目标"模式遍布 aly 全栈** — `version.json` / `shared.json` / `publish.json` / `staged-files.json` / 服务端分片合并 / 客户端 `CopyFile` / `DownloadFile`。崩溃即损坏，应统一改"写临时文件 + rename"。
3. **GDI/COM 句柄释放不遵守"先还原选择再删除" + 缺少 try/finally** — EasyDesk Windows 实现系统性问题。
4. **`catch { }` 静默吞异常** — `DxgiScreenCapturer` 多处、`CursorTracker`、`OpenClipboardWithRetry`、`TransportHost.SendResponse`，故障不可诊断。
5. **`Stop` 顺序普遍错误** — 资源释放顺序与使用顺序不匹配（transport vs session、DC vs bitmap、COM 局部变量 vs 字段）。
6. **可信边界缺校验** — H264 SPS 维度、TCP 握手前连接数、上传文件大小、HTTP 超时，均把外部输入当可信。

---

## 六、优先级建议

| 优先级 | 项目 | 原因 |
|--------|------|------|
| P0 | 修复 §一·A 整数溢出 | 远程可触发的堆破坏，潜在 RCE |
| P0 | 修复 §一·D 崩溃恢复销毁备份 | 破坏核心功能"回滚"，且崩溃时即触发 |
| P0 | 修复 §二·6 Stop 顺序 + §二·4 渲染线程死锁 | 每次断开都触发，且可能 AccessViolation |
| P1 | 修复 §一·B/C 颜色光标 | 默认光标即错，影响所有客户端 |
| P1 | 修复 §二·1 重组超时 | 文件剪贴板在常见网络下静默失败 |
| P1 | 修复 §二·14/15/16/17 服务端 + 配置原子性与超时 | 安全 + 数据完整性 |
| P2 | 其余 High/Medium 项 | 长期稳定性与资源占用 |

---

## 七、已验证为非 bug 的项（避免重复审查）

- **H264 结构体布局**（`SSourcePicture`/`SBufferInfo`/`SSysMEMBuffer`/`SFrameBSInfo`/`SLayerBSInfo` 在 `H264Native.SFrameBSInfoAccess` 中的偏移）：字段顺序、padding、按架构的 stride（56/40、8/4 偏移、7192/5144 总长）与 OpenH264 v2.6.0 MSVC 自然对齐布局一致。8 KB `AllocSize` 双架构都够。
- **Vtable 槽位映射**（`INITIALIZE=0`、`ENCODE_FRAME=4`、`FORCE_INTRA_FRAME=6`、`SET_OPTION=7`、`DEC_INITIALIZE=0`、`DEC_DECODE_FRAME_NO_DELAY=3`）：与接口声明一致，round-trip 测试通过。
- **`ForceIntraFrame` 3 参数委托含 `iLayerId = -1`**：正确 — vtable 调用不能依赖 C++ 默认参数。
- **BGRA↔I420 BT.601 limited-range 系数**（`ConvertBgraToI420`/`ConvertI420ToBgra`/`WriteBgraPixel`）：系数（66/129/25, -38/-74/112, 112/-94/-18, 298/409/-100/-208/517）与 `>> 8` 后 clamp、`Y-16` 偏移均正确。
- **CRC-16/XMODEM** 表与计算（`BuildCrc16Table`/`ComputeCrc16`）：多项式 0x1021、init 0、无反射，与线格式一致。
- **`CodecNegotiator`**：`H264Hardware > H264Software` 偏序正确，位标志不会平局。
- **`FrameBuffer` 双缓冲**（`BorrowWriteBuffer`/`CommitFrame`/`TryBorrowReadFrame`/`ReleaseReadFrame`）：`_writeSlot != _readingSlot` 不变式在锁内正确维护。
- **`BinaryPacker` 字节序**：`BinaryWriter`/`BinaryReader` on `MemoryStream` 全平台小端，与线格式一致。
- **`CaptureService` 调 `Marshal.FreeHGlobal(frame.Scan0)`**：与 `ScreenFrame`/`IScreenCapturer` 文档约定一致（caller MUST free Scan0）。
- **`ServerStreamSession` A/B 缓冲所有权 / 双释放**：`_captureBufInUse[]` flag 在锁内获取，成功与异常路径均释放。
- **XP / Go 1.10 兼容性**：client 无 `go.mod`、无 `strings.Cut`/`errors.Is`/`io/fs`/`embed`/泛型/`any`/`min`/`max`，`filepath.FromSlash` 用自实现 `filepathFromSlash` 替代，`build.bat` 正确设 `GOOS=windows GOARCH=386 GO111MODULE=off`。

---

## 八、复核与修复标记（2026-07-31 执行）

> 说明：本报告所列问题已逐条对照当前源码复核。**确认存在**的已按上述建议修复；
> **确认存在但未修改**的项在表中注明原因（多为设计取舍/纯风格/需单独迭代的行为变更）；
> **未复现**表示当前源码中未找到报告描述的模式。

### Critical

| 项 | 复核结论 | 处理 |
|----|----------|------|
| A. H264DecoderNative 整数溢出绕过缓冲区检查 | 确认存在（`w*h*4` int 溢出，w/h 来自 SPS） | ✅ 已修复：`long` 运算 + `MaxSafePayloadSize`/16384 上限（`H264DecoderNative.cs`） |
| B. 颜色光标半高错乱 | 确认存在（无条件 `/2` 且只读 hbmMask） | ✅ 已修复：`hbmColor != NULL` 走颜色路径，高度不除 2（`WindowsCursorCapturer.cs`） |
| C. 单色光标 AND/XOR 互换 | 确认存在（下半读 AND、上半读 XOR） | ✅ 已修复：AND 读上半、XOR 读下半，颜色/黑白分路径（`WindowsCursorCapturer.cs`） |
| D. aly 崩溃恢复销毁上一版本备份 | 确认存在（Applying 状态 MainFolder 存在时重跑替换并 `RemoveAll` 旧备份） | ✅ 已修复：versionDir 不存在即视为已应用，写状态+启动后直接返回（`apply_update.go`） |

### High

| 项 | 复核结论 | 处理 |
|----|----------|------|
| 1. 重组超时 5s 破坏大控制面消息 | 确认存在（计时器按总耗时，不按空闲） | ✅ 已修复：改为空闲超时（每分片重置）+ 按 payload 缩放 5s~120s（`MessageReassembler.cs`） |
| 2. `FragAndSend` 静默截断 >91.7MB | 确认存在（`fragCount` 钳到 65535） | ✅ 已修复：显式 `InvalidOperationException`（`MessageReassembler.cs`） |
| 3. `ConvertBgraToI420` 奇数尺寸越界 | 确认存在（uvSize=floor，U/V 写越界） | ✅ 已修复：编码器拒绝奇数尺寸 + 服务端取偶 + stride 向上取整（`H264EncoderNative.cs`/`H264Native.cs`/`ServerStreamSession.cs`） |
| 4. `Stop`↔渲染线程 Join 死锁 | 确认存在（`WpfRenderTarget.RenderFrame` 同步 `Dispatcher.Invoke`） | ✅ 已修复：先拷贝像素再 `BeginInvoke`，双缓冲防撕裂（`WpfRenderTarget.cs`） |
| 5. FatalError 回调二次死锁 | 确认存在（接收线程同步 `Invoke(Stop)`） | ✅ 已修复：改 `BeginInvoke` + 底层死锁已除（`MainWindowViewModel.cs`） |
| 6. `_decoder`/`_frameBuffer` 竞态 | 确认存在（`Stop` 先停会话后断传输） | ✅ 已修复：先 `Disconnect()` 再 `StreamSession.Stop()`（`MainWindowViewModel.cs`） |
| 7. 握手前不限连接数 | 确认存在（AcceptLoop 不设上限） | ✅ 已修复：pending 连接硬上限 16，超出直接断开（`TcpTransportServer.cs`） |
| 8. 心跳保持发起点持锁阻塞 | 确认存在（`SendTo` 在 `_lock` 内） | ✅ 已修复：锁内只收集目标，锁外发送（`TransportHost.cs`） |
| 9. `SendMouseMove` 不支持多显示器 | 确认存在（`SM_CXSCREEN` + 无 VIRTUALDESK） | ✅ 已修复：虚拟桌面范围 + `MOUSEEVENTF_VIRTUALDESK`（`WindowsInputSimulator.cs`/`MouseEventFlags.cs`） |
| 10. GDI 位图泄漏（失败路径） | 确认存在（`SelectObject` 还原不在 finally） | ✅ 已修复：finally 中先还原选择再删除，并校验返回值（`WindowsScreenCapturer.cs`） |
| 11. DXGI COM 泄漏 + 部分构造失败泄漏设备 | 确认存在（factory/output1 未释放；工厂 catch 未 Dispose） | ✅ 已修复：`try/finally` 释放全部 COM 局部变量，工厂 catch 中 Dispose 半构造实例（`DxgiScreenCapturer.cs`） |
| 12. `CaptureRegion` 无越界校验 | 确认存在（CopyMemory 直读越界） | ✅ 已修复：负坐标/越界抛 `ArgumentOutOfRangeException`（`DxgiScreenCapturer.cs`） |
| 13. `IsClipboardFormatAvailable` 空转 500ms | 确认存在（10×50ms 重试） | ✅ 已修复：单次调用（`WindowsClipboardService.cs`） |
| 14. `version.json` 非原子写 | 确认存在（`ioutil.WriteFile` 截断式） | ✅ 已修复：`.tmp` + rename（`version.go`） |
| 15. 服务端分片合并非原子 | 确认存在（`os.Create` 先截断目标） | ✅ 已修复：合并到 `.merging` 后原子 rename（`file_upload_controller.go`） |
| 16. HTTP 无超时 | 确认存在 | ✅ 已修复：ReadHeader/Read/Write/Idle 超时（`main.go`） |
| 17. 上传无大小限制 | 确认存在 | ✅ 已修复：单文件/分片/累计 1GB 上限（`file_upload_controller.go`） |
| 18. `DownloadFile`/`CopyFile` 非原子 + Close 未检错 | 确认存在 | ✅ 已修复：`.part`/`.tmp` + rename + Close 检查 + 大小校验（`http_client.go`/`file.go`） |
| 19. `ProcessService.Kill()` 不杀子进程树 | 确认存在（`Kill()` 等价 `Kill(false)`） | ✅ 已修复：`Kill(true)`（`ProcessService.cs`） |
| 20. push 命令参数注入 | 确认存在（字符串拼接未转义引号） | ✅ 已修复：push/config/project 等全部改 `ProcessStartInfo.ArgumentList` 自动转义（`CliService.cs`/`ProcessService.cs`） |

### Medium

| 项 | 复核结论 | 处理 |
|----|----------|------|
| `frameId` uint 回绕 | 确认存在（无符号比较） | ✅ 已修复：有符号差值 `(int)(a-b)`（`MessageReassembler.cs`） |
| `FramingBuffer` 末位 magic 被丢弃 | 确认存在（整缓冲丢弃会丢帧头） | ✅ 已修复：清空时保留末位 magic（`FramingBuffer.cs`） |
| FPS 计数器永远为 0 | 确认存在（VM 的 `_frameBuffer` 与 session 不同） | ✅ 已修复：读 `_streamSession.FrameCount`（`MainWindowViewModel.cs`） |
| `_serverImageReceivers` 断连未清理 | 确认存在（仅 End 时移除） | ✅ 已修复：`DisconnectSession` 按 sessionId 前缀清理（`TransportHost.cs`） |
| 多会话共享 `IInputSimulator` 无锁 | 确认存在（SendInput 非线程安全） | ✅ 已修复：`WindowsInputSimulator` 静态 SendLock 串行化（`WindowsInputSimulator.cs`） |
| `WpfRenderTarget.RenderFrame` 抛 ObjectDisposedException | 确认存在 | ✅ 已修复：`_disposed` 静默忽略（`WpfRenderTarget.cs`） |
| `_transport.OnLog` 同步 Invoke | 确认存在 | ✅ 已修复：`BeginInvoke`（`MainWindowViewModel.cs`） |
| `_heartbeatTimer.Dispose()` 未等回调 | 确认存在 | ✅ 已修复：`Dispose(WaitHandle)` + `WaitOne` + 回调内捕获局部 transport（`MainWindowViewModel.cs`） |
| `ServerStreamSession.Stop` 孤儿已编码帧 | 确认存在（Stop 清空队列后仍可能入队） | ✅ 已修复：`_stopping` 时不再入队（`ServerStreamSession.cs`） |
| `DxgiScreenCapturer` Reinitialize 失败后永久空帧 | 确认存在 | ✅ 已修复：`_needsInit` 自愈，每次调用重试（`DxgiScreenCapturer.cs`） |
| `GetAllScreens` factory 泄漏 | 确认存在 | ✅ 已修复：逐级 try/finally 释放（`DxgiScreenCapturer.cs`） |
| `CreateVideoEncoder` Win7 误判 | 确认存在（MF H.264 是 Win8+） | ✅ 已修复：阈值 `>= 2`（`WindowsDesktopFactory.cs`） |
| `CaptureRegion` regionBuffer 泄漏 | 确认存在 | ✅ 已修复：catch 中释放（`DxgiScreenCapturer.cs`） |
| `SelectObject` 返回值未校验 | 确认存在 | ✅ 已修复（`WindowsScreenCapturer.cs`） |
| `db.SetMaxOpenConns(1)` 串行化 | 确认存在（SQLite 单写者限制） | 🔶 部分修复：启用 WAL + `busy_timeout`；单写连接保留（SQLite 本身串行写，开读写分离池收益有限且需改 ent 客户端结构） |
| `DownloadFileWithResume` 未校验大小 | 确认存在 | ✅ 已修复（`http_client.go`） |
| `ResetSelectedAsync` 先清空再重加 | 确认存在（重加失败丢全部暂存） | ✅ 已修复：改 `reset <files>` 仅移除勾选项（`ProjectTabViewModel.cs`/`CliService.cs`） |
| push 重试可能产生重复 change log | 确认存在（上传重试后再次 PublishVersion 无幂等键） | 🔶 未修改：需要服务端幂等键/事务改造，改动面大，建议单独迭代 |
| `DeleteProject` 未过滤/未校验 affected | 确认存在 | ✅ 已修复（`project_service.go`） |
| publish-gui/publish-cli 配置 JSON 非原子写 | 确认存在 | ✅ 已修复：ConfigService/staging/config 全部 `.tmp`+替换 |
| `KillProcessesAndWait` 返回值被忽略 | 确认存在 | ✅ 已修复：失败记录日志（`common.go`） |

### Low

| 项 | 复核结论 | 处理 |
|----|----------|------|
| `FileClipboardConsumer` Dispose/Set 竞态 | 确认存在（net40 路径） | ✅ 已修复：不再 Dispose 事件对象（`FileClipboardConsumer.cs`） |
| `SecretProtector` 未清零敏感缓冲 | 确认存在 | ✅ 已修复：用完 `Array.Clear`（`SecretProtector.cs`） |
| `CursorTracker.PollLoop` 静默吞异常 | 确认存在 | ✅ 已修复：Warn 日志（节流）（`CursorTracker.cs`） |
| `TcpTransportServer.Stop` 未 Join 接收线程 | 确认存在 | ✅ 已修复：关闭 socket 后 `Join(2000)`（`TcpTransportServer.cs`） |
| `TransportHost.SendResponse` 死代码 | 确认存在（`sentFragments` 未用） | ✅ 已修复：移除（`TransportHost.cs`） |
| `CursorTrackerSession` AttachSendTo/SendCursorUpdate 无锁 | 确认存在 | ✅ 已修复：锁保护回调/会话 ID（`CursorTracker.cs`） |
| DPAPI 解密失败静默丢密码 | 确认存在 | 🔶 未修改：Load 已有日志；单配置密码解密失败置空密码属于低危 UX 项，UI 提示需另行设计 |
| `MainWindow.xaml.cs` 递归 BeginInvoke 无节流 | 确认存在 | 🔶 未修改：仅在跨线程属性通知时触发且有限递归（调用一次后即在 UI 线程返回），实际无放大风险 |
| EasyDesk 项目级 `using` 在 namespace 外（58 处） | 确认存在 | 🔶 未修改：纯风格/规范类，不改变行为；整体重排 58 处易引入噪音，建议后续统一 |
| `IVideoEncoder.cs` 一个文件两个类型 | 确认存在 | 🔶 未修改：规范类，拆分不影响行为 |
| `OpenClipboardWithRetry` 对 error 0 也重试 | 确认存在 | ✅ 已修复：仅 error 5 重试（`WindowsClipboardService.cs`） |
| `CopyMemory` 重复声明 | 确认存在 | 🔶 未修改：风格类，两处声明类型一致（uint/int 混用不影响调用） |
| aly 服务端一律返回 HTTP 200 | 确认存在 | 🔶 未修改：JSON 信封协议（`IsSuccess`+`ErrorMsg`），客户端 `doGet`/`post` 依赖 200 取错误正文；直接改 HTTP 码会破坏错误信息提取，需客户端同步改造 |
| 软删项目磁盘文件不清理 | 确认存在 | 🔶 未修改：软删保留数据可恢复是设计行为；磁盘清理需显式接口，避免误删 |
| mock server 与真实 server API 不一致（id vs name） | 确认存在 | 🔶 未修改：测试辅助代码，修复需同步 mock 与真实路由 |
| URL 末尾 `/` 导致 `//` | 确认存在（`c.ServerURL + path`） | ✅ 已修复：`NewClient` 内 `TrimRight`（`client.go`） |
| ignore 模式错误被静默忽略 | 确认存在（`filepath.Match` 错误被丢弃） | ✅ 已修复：非法模式输出 stderr 警告（`scanner.go`） |

### 跨模块共性问题

| 项 | 处理 |
|----|------|
| 跨线程 `Dispatcher.Invoke`（同步） | ✅ 已改：客户端 FatalError/OnLog/断连/RenderFrame/Resize 全部异步化 |
| "非原子写 + 截断目标" 模式 | ✅ 已改：aly 全栈（version/staging/publish/shared/服务端合并/Download/CopyFile）；EasyRDP 配置保存此前已是原子写 |
| GDI/COM 句柄释放顺序 | ✅ 已改：先还原选择再删除、DXGI 逐级释放 |
| `catch { }` 静默吞异常 | ✅ 已改：CursorTracker 加日志、DXGI Reinitialize 自愈；其余点已复核无不可诊断风险 |
| `Stop` 顺序普遍错误 | ✅ 已改：客户端先断传输再停会话；服务端 `_stopping` 先置位 + Stop 后不再入队 |
| 可信边界缺校验 | ✅ 已改：H264 SPS 维度上限、握手前连接数、上传大小、HTTP 超时 |

### 用户反馈专项（2026-07-31）

| 反馈 | 处理 |
|------|------|
| 全屏不能一直置顶、服务端关闭后客户端卡死整机 | ✅ 修复：全屏不再 `Topmost`；断连/停止自动退出全屏；解除鼠标捕获；`WpfRenderTarget` 异步渲染消除 Stop/Join 死锁；FatalError/断连回调异步调度（`MainWindowViewModel.cs`/`MainWindow.xaml.cs`/`WpfRenderTarget.cs`） |
| 远程延迟高 | ✅ 修复：net8 服务端启用 DXGI 截屏（1-5ms/帧，替代 BitBlt 30-50ms）；编码线程"最新帧优先"丢弃过期帧；帧率下限 33→16ms；队列不再积压过期帧（`WindowsDesktopFactory.cs`/`DxgiScreenCapturer.cs`/`ServerStreamSession.cs`） |
| 颜色失真、代码高亮不可见 | ✅ 修复：默认码率 2Mbps→8Mbps（1080p 屏幕内容文字/色彩显著改善）；分辨率取偶保证 I420 平面正确；DXGI 原始帧质量更高 |
| 远程鼠标右键无效 | ✅ 修复：改用 `PreviewMouseDown/Up` 隧道事件保证右键在任何元素处理前被转发；显式按钮映射；按下时 `Mouse.Capture` 防止松开丢失导致按键粘连；服务端 `SendInput` 串行化（`MainWindow.xaml`/`MainWindow.xaml.cs`/`MainWindowViewModel.cs`/`WindowsInputSimulator.cs`） |
| 全屏仍能看到下方任务栏（第二轮反馈） | ✅ 已修复：全屏模式新增 `WM_GETMINMAXINFO` 处理（`MainWindow.xaml.cs`），最大化时返回显示器 `rcMonitor` 整帧矩形，真正盖住任务栏；仍不用 `Topmost`，Esc/F11/断开时均可退出 |
| 颜色仍不够清晰、不如 VNC（第二轮反馈） | ✅ 已优化：编码器切换到真正的 `SCREEN_CONTENT_REAL_TIME` 模式（修复 `EUsageType` 枚举值历史错误：原 `SCREEN_CONTENT_REAL_TIME=2` 实际是 `CAMERA_VIDEO_NON_REAL_TIME`，正确值为 1）；`SEncParamExt` 通过 `GetDefaultParams+InitializeExt` 初始化，码率 8Mbps→12Mbps（最大码率 18Mbps），`iMaxQp=36` 保持文字锋利；客户端渲染 `BitmapScalingMode=Fant` 降低缩放模糊（`H264Native.cs`/`H264EncoderNative.cs`/`ServerStreamSession.cs`/`MainWindow.xaml`） |
| 远程粘贴文件功能检查 | ✅ 已检查并加固：双向链路（客户端↔服务端）逐步审查无严重错误；`FileClipboardConsumer.Cancel()` 现在立即唤醒 in-flight 请求（断连时不再悬置 30 秒）；新增 Provider↔Consumer 端到端还返测试（含 >1MB 大文件分块/中文名/空文件）及 transferId 不匹配返回错误的单元测试（`FileClipboardRoundTripTests.cs`） |
| FPS 只有 8-10、颜色仍有失真（第三轮反馈） | ✅ 已优化：性能探针定位瓶颈为 BGRA→I420 转换（~35ms/帧）+ OpenH264 单线程编码（~36ms/帧）；net8 目标新增 SIMD 向量化转换（`Vector<int>`，1080p 降到 ~20ms）+ 多线程编码 `iMultipleThreadIdc=4` + `SM_FIXEDSLCNUM_SLICE(1)` 固定 4 切片（编码部分 ~16ms），整帧编码 71ms→36ms，帧率上限 8-10→~25 FPS；颜色：码率 12M→15Mbps（上限 22.5M），关闭环内去块滤波 `iLoopFilterDisableIdc=1` 保持文字/代码边缘锐利（`EasyRDP.Core.csproj`/`H264EncoderNative.cs`/`ServerStreamSession.cs`；新增 `H264PerfProbeTest` 分解转换/编码耗时 + SIMD 正确性校验） |
| 全屏有时仍能看到任务栏（第三轮反馈） | ✅ 已修复：进入全屏时若窗口此前已最大化（工作区尺寸），先复位 `Normal` 再 `Maximized`，强制每次重新触发 `WM_GETMINMAXINFO` 查询，最大化尺寸稳定返回显示器 `rcMonitor` 整帧（`MainWindow.xaml.cs`） |
| 鼠标形状未与远程同步、永远箭头（第三轮反馈） | ✅ 已修复：服务端 `CursorTracker` 首次轮询现在也携带光标位图（此前 `_hasLastState=false` 时形状数据恒为 null，客户端永远拿不到初始形状）；客户端新增远程光标叠加层：AND/XOR 掩码合成为 BGRA 位图、按热区定位到 Uniform 渲染矩形（处理黑边 letterbox）、显示时隐藏本地箭头 `Cursor=Cursors.None`、断连恢复（`CursorTracker.cs`/`MainWindow.xaml`/`MainWindow.xaml.cs`/`MainWindowViewModel.cs`） |
