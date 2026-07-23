# EasyRDP 完整开发计划

> 最后更新: 2026-07-23
>
> **架构策略**：服务端仅 WPF (.NET 4，XP 兼容)；客户端 WPF (.NET 4) + Avalonia (.NET 8) 双版本，共用
> `EasyRDP.Client.Common` 逻辑库。页面 UI 层面不做代码共享，各实现各的。Avalonia 服务端待定。
>
> **关联计划**：编码层（取像素→压缩→传输）的可插拔抽象见 `docs/EasyRDP-Codec-Plan-B.md`
> （B1–B4：Bitmap / H.264 软编 / H.264 硬编）。本计划的 `CaptureEngine` 截屏压缩部分将在 B-1 阶段
> 抽离为 `IFrameEncoder`/`BitmapEncoder`，下文 §2.6 描述为重构前形态，实际代码已含 b545cad 的
> 双缓冲 + 发送队列重构，B-1 落地时再行抽象。

---

## 0. 基础设施层 ✅ 已完成

### 0.1 EasyDesk — 桌面 I/O 抽象库
   - `EasyDesk.Core` (net40;netstandard2.0)：5 个接口（`IInputSimulator`, `IScreenCapturer`, `ICursorCapturer`, `IClipboardService`, `IDesktopInfo`）+ 8 个数据模型（`ScreenFrame`, `CursorInfo`, `DesktopBounds`, `CaptureOptions`, `KeyEventFlags`, `MouseButton`, `MouseEventFlags`, `VirtualKeyCode`）
   - `EasyDesk.Windows` (net40;netstandard2.0)：Windows P/Invoke 实现（user32/gdi32/kernel32），零第三方依赖
   - 集成测试 `test/EasyDesk.Windows.Tests/`

### 0.2 EasyRDP.Core Protocol — 协议编解码层
   - `Protocol/Constants.cs` — 魔数 `0x45524450`（小端序 LE）、版本 `0x01`、默认端口 8750 等
   - `Protocol/MessageType.cs` — 消息类型枚举（`HandshakeReq/Res`, `ScreenFrame`, `CursorUpdate`, `InputEvent`, `ClipboardData`, `KeepAlive/Ack`, `FileTransferReq/Data`, `Disconnect`）、握手结果码、断开原因码、帧类型、压缩类型、输入事件类型
   - `Protocol/MessageHeader.cs` — 14 字节消息头（Magic LE + Version + Type + Sequence LE + Length LE），读写方法
   - `Protocol/MessageCodec.cs` — 编解码工厂，`Decode(byte[])` / `Encode(type, seq, body)`
   - `Protocol/BinaryPacker.cs` — 二进制读写工具（uint16/32/64 LE、string UTF-8、byte[]）
   - `Protocol/CompressHelper.cs` — DeflateStream 压缩/解压
   - `Protocol/DirtyRectDetector.cs` — 行级脏矩形检测 + 垂直合并
   - `Protocol/SequenceTracker.cs` — 消息序号跟踪器
   - `Protocol/Messages/` — 各类型消息的 Encode/Decode 实现

### 0.3 EasyRDP.Core Transport — 可扩展传输层
   - `Transport/TransportEvents.cs` — `ConnectionEventArgs`, `MessageReceivedEventArgs`, `LogLevel`, `LogCallback`
   - `Transport/ITransportClient.cs` — 客户端传输抽象接口（`Connect`, `Disconnect`, `Send`, `IsConnected`, 事件）
   - `Transport/ITransportServer.cs` — 服务端传输抽象接口（`Start`, `Stop`, `SendTo`, `Disconnect`, 事件）
   - `Transport/PacketFramer.cs` — 传输无关分包器（粘包/半包处理）
   - `Transport/TcpTransportClient.cs` — TCP 客户端实现（TcpClient + NetworkStream + 后台接收线程，Send/Close 锁同步，Disconnected 幂等）
   - `Transport/TcpTransportServer.cs` — TCP 服务端实现（TcpListener + accept 线程 + 多客户端会话管理，MaxClients 限制）
   - `Transport/UdpTransportClient.cs` — UDP 客户端实现（UdpClient + 注册探测 + 丢包容忍）
   - `Transport/UdpTransportServer.cs` — UDP 服务端实现（远程端点映射 + 僵尸会话超时清理）
   - `Transport/TcpTransportOptions.cs` — TCP 配置（Send/ReceiveBufferSize, NoDelay, Send/ReceiveTimeoutMs, ConnectTimeoutMs, Backlog, MaxClients）
   - `Transport/UdpTransportOptions.cs` — UDP 配置（Send/ReceiveTimeoutMs, ReceiveBufferSize, ProbeRetries, SessionTimeoutSeconds）

### 0.4 EasyRDP.Core.Tests — 传输层集成测试
   - `test/EasyRDP.Core.Tests/Transport/TcpTransportTests.cs` — 21 例（连通性/消息/错误处理/Options）
   - `test/EasyRDP.Core.Tests/Transport/UdpTransportTests.cs` — 16 例（连通性/消息/错误处理/僵尸清理/Options）

### 0.5 协议文档
   - `docs/EasyRDP-Protocol-v1.md` (v1.1) — 传输无关 + 全部字段小端序

---

## 1. EasyRDP.Client.Common — 客户端共享逻辑库

> **目标框架**: `net40;net8.0` (C# 5.0 兼容)
> **依赖**: 仅 `EasyRDP.Core`，不含任何 UI 框架（WPF/Avalonia）引用
> **消费者**: `Client.Wpf` (.NET 4) 和 `Client.Avalonia` (.NET 8)

### 1.1 项目骨架
   - 新建 `src/EasyRDP.Client.Common/`
   - `.csproj`：
     ```xml
     <TargetFrameworks>net40;net8.0</TargetFrameworks>
     <LangVersion>5</LangVersion>
     <Nullable>disable</Nullable>
     <ImplicitUsings>disable</ImplicitUsings>
     <RootNamespace>EasyRDP.Client.Common</RootNamespace>
     ```
   - 项目引用：`EasyRDP.Core`
   - 目录结构：
     ```
     Client.Common/
     ├── ConnectionManager.cs       # 连接/断连/重连状态机
     ├── MessageDispatcher.cs        # 消息路由分发
     ├── FrameBuffer.cs              # 本地帧缓冲 + 增量合并
     ├── InputEncoder.cs             # 输入事件 → InputEventMessage 编码
     ├── ClipboardSyncEngine.cs      # 双向剪贴板同步 + 静默期
     ├── KeepAliveEngine.cs          # 心跳发送 + Ack 超时检测
     ├── IClipboardProvider.cs       # 剪贴板操作抽象（WPF/Avalonia 各自实现）
     └── ConnectionState.cs          # 连接状态枚举
     ```

### 1.2 `ConnectionManager.cs` — 连接状态机
   - **`ConnectionState` 枚举** (`ConnectionState.cs`)：
     - `Disconnected` — 未连接
     - `Connecting` — 正在建立 TCP 连接 + 握手中
     - `Connected` — 握手成功，可收发数据
     - `Disconnecting` — 正在断开
   - **属性**：
     - `Transport` (ITransportClient) — 当前传输实例（TcpTransportClient，可通过配置切换为 UdpTransportClient）
     - `State` (ConnectionState)
     - `RemoteScreenWidth` / `RemoteScreenHeight` (int) — 握手后获得
     - `SessionId` (uint) — 握手后获得
     - `SeqTracker` (SequenceTracker) — 发送消息序号
   - **方法**：
     - `void BeginConnect(string host, int port, int timeoutMs, string authToken)`：
       1. `_transport = new TcpTransportClient(TcpTransportOptions.Default)`
       2. `_transport.MessageReceived += OnMessageReceived`
       3. `_transport.Disconnected += OnDisconnected`
       4. `_transport.Connect(host, port, timeoutMs)` → 失败则 `State = Disconnected; return false`
       5. `State = Connecting`
       6. 构建 `HandshakeReqMessage(AuthToken, ScreenWidth=0, ScreenHeight=0, CompressType=Zlib)`
       7. `_transport.Send(MessageCodec.Encode(HandshakeReq, SeqTracker.Next(), req))`
       8. 阻塞等待 `HandshakeRes`（通过 `ManualResetEvent`）：`_handshakeEvent.WaitOne(timeoutMs)`
       9. 成功 → `State = Connected`，填充 `ScreenWidth/Height`/`SessionId`
       10. 失败 → `State = Disconnected`，`_transport.Disconnect()`
     - `void Disconnect()`：
       1. `State = Disconnecting`
       2. 发送 `DisconnectMessage(UserDisconnect)`
       3. `_transport.Disconnect()`
       4. `State = Disconnected`
     - `void SendMessage(MessageType type, object body)` — `_transport.Send(Encode(type, SeqTracker.Next(), body))`
     - **事件**：
       - `event Action<object> OnMessage` — 原始消息对象回调，由 `MessageDispatcher` 消费
       - `event Action OnConnected` — 连接成功
       - `event Action<string> OnConnectionFailed` — 连接失败（含原因）
       - `event Action<string> OnDisconnected` — 断连（含原因）
     - **内部实现**：
       - `ManualResetEvent _handshakeEvent = new ManualResetEvent(false)`
       - `OnMessageReceived` 检测到 `HandshakeRes` 时调用 `_handshakeEvent.Set()`

### 1.3 `MessageDispatcher.cs` — 消息路由器
   - **职责**：将 `ConnectionManager.OnMessage` 转发的 `object` 按实际类型分派到注册的处理器
   - **注册机制**：
     ```csharp
     // 内部存储 Dictionary<Type, Action<object>>
     void RegisterHandler<T>(Action<T> handler) where T : class;
     void UnregisterHandler<T>();
     ```
   - **核心方法**：
     - `void Dispatch(object messageBody)`：
       1. `Type msgType = messageBody.GetType()`
       2. 查 `_handlers` 字典，找到则调用对应 `Action<object>`
       3. 找不到则记录警告日志（未知消息类型）
   - **预注册的标准处理器**（由 ViewModel 在初始化时注册）：
     | 消息类型 | 处理器 | 说明 |
     |----------|--------|------|
     | `HandshakeResMessage` | `ConnectionManager.HandleHandshakeRes` | 已在 ConnectionManager 内部处理 |
     | `ScreenFrameMessage` | `FrameBuffer.ProcessFrame` | 帧缓冲更新 |
     | `CursorUpdateMessage` | `OnCursorUpdate` 回调 | 由 UI 层注册 |
     | `ClipboardDataMessage` | `ClipboardSyncEngine.OnRemoteClipboard` | 剪贴板同步 |
     | `DisconnectMessage` | `ConnectionManager.HandleRemoteDisconnect` | 远程断连 |
   - `OnCursorUpdate` — 暴露为 `event Action<CursorUpdateMessage>`，由 UI 层消费来移动光标

### 1.4 `FrameBuffer.cs` — 本地帧缓冲
   - **属性**：
     - `Width` / `Height` (int) — 当前帧尺寸
     - `Buffer` (byte[]) — BGRA32 像素缓冲（只读副本，`lock` 保护）
     - `IsDirty` (bool) — 新帧到达标志（供 UI 层查询后消费）
     - `FrameCount` (int) — 累计帧数
   - **方法**：
     - `void ProcessFrame(ScreenFrameMessage frame)`：
       1. 解压像素：`CompressHelper.Decompress(frame.Pixels, frame.Compress, rawPixelSize)`
       2. 如果 `frame.FrameType == Full`：
          - `lock(_lock)` { `Buffer = new byte[w*h*4]`; `Array.Copy(pixels, 0, Buffer, 0, Buffer.Length)`; `Width = w`; `Height = h`; }
       3. 如果 `frame.FrameType == Delta`：
          - 在 `lock(_lock)` 中遍历 `frame.Rects`：
            - 每个 Rect：计算 `srcOffset = rect.Offset`，`dstOffset = (rect.Y * stride) + (rect.X * 4)`
            - 逐行 `Array.Copy(allPixels, srcOffset, Buffer, dstOffset, rect.Width * 4)`（`stride = Width * 4`）
       4. `IsDirty = true`，`FrameCount++`
     - `bool TryGetFrame(out byte[] pixels, out int w, out int h)`：
       - `lock(_lock)`：`pixels = new byte[Buffer.Length]; Array.Copy(Buffer, pixels, Buffer.Length); w = Width; h = Height`
       - `IsDirty = false` — 标记已消费
       - 返回 `true`（如果 `Buffer != null`）
   - 线程安全：`private readonly object _lock = new object()` 保护 `Buffer`、`Width`、`Height` 的读写

### 1.5 `InputEncoder.cs` — 输入事件编码器
   - **职责**：接收 UI 层已映射好的原始值，编码为 `InputEventMessage`
   - **不负责**键盘/鼠标按键映射——映射逻辑在 UI 层（WPF `Key`→VK 和 Avalonia `Key`→VK 的映射表不同），`InputEncoder` 只接收已映射好的 `byte` 值
   - **方法**：
     ```csharp
     // 鼠标移动（absolute=true 表示绝对坐标，false 表示相对偏移）
     byte[] EncodeMouseMove(bool absolute, short x, short y);

     // 鼠标按键（button: 0=左 1=右 2=中 3=X1 4=X2）
     byte[] EncodeMouseButton(bool isDown, byte button);

     // 鼠标滚轮（delta：正值向上，WHEEL_DELTA=120）
     byte[] EncodeMouseWheel(short delta);

     // 键盘（virtualKey: Windows VK 码，flags: 0x0001=扩展键）
     byte[] EncodeKey(bool isDown, byte virtualKey, ushort flags);

     // Unicode 文本
     byte[] EncodeUnicodeText(string text);
     ```
   - 每个方法内部：构建 `InputEventMessage` → `MessageCodec.Encode(InputEvent, 0, msg)`（seq 由调用方传入）
   - 实际签名改为 `byte[] EncodeXxx(uint sequence, ...)`，序列号由 `ConnectionManager.SeqTracker.Next()` 管理

### 1.6 `ClipboardSyncEngine.cs` — 剪贴板同步引擎
   - **属性**：
     - `_lastSentText` (string) — 上次发送的文本（去重用）
     - `_cooldownUntil` (DateTime) — 收到远程剪贴板后 500ms 静默期内不发送本地变更
   - **方法**：
     - `byte[]? TryEncodeLocalChange(string currentText, uint sequence)`：
       1. 如果 `currentText == _lastSentText` → 返回 null（无变化）
       2. 如果 `DateTime.Now < _cooldownUntil` → 返回 null（静默期）
       3. 否则构建 `ClipboardDataMessage(UnicodeText, currentText)` → 编码并返回
       4. 更新 `_lastSentText = currentText`
     - `string? OnRemoteClipboard(ClipboardDataMessage msg)`：
       1. 如果 `msg.Format != UnicodeText` → 返回 null
       2. 设置 `_cooldownUntil = DateTime.Now.AddMilliseconds(500)`（启动静默期）
       3. 更新 `_lastSentText = msg.Text`（防止本地立即检测到变更）
       4. 返回 `msg.Text`（由 UI 层写入本地剪贴板）

### 1.7 `KeepAliveEngine.cs` — 心跳引擎
   - **属性**：
     - `IntervalMs` (int, 默认 5000)
     - `TimeoutMs` (int, 默认 15000)
     - `LastAckTime` (DateTime)
     - `IsRunning` (bool)
   - **方法**：
     - `void Start(Func<bool> sendKeepAlive, CancellationToken ct)`：
       1. 新线程循环：`while (!ct.IsCancellationRequested) { sendKeepAlive(); Thread.Sleep(IntervalMs); }`
     - `void OnAckReceived()` → `LastAckTime = DateTime.Now`
     - `bool IsTimeout()` → `(DateTime.Now - LastAckTime).TotalMilliseconds > TimeoutMs`
   - **事件**：`event Action OnTimeout`
   - `Start` 所在的线程同时监控超时（每次循环检查 `IsTimeout()`，超时则触发 `OnTimeout`）

### 1.8 `IClipboardProvider.cs` — 剪贴板抽象
   - WPF 和 Avalonia 的剪贴板 API 不同：
     - WPF：`System.Windows.Clipboard.GetText()` / `SetText()`（同步）
     - Avalonia：`Avalonia.Application.Current.Clipboard.GetTextAsync()` / `SetTextAsync()`（异步）
   - 接口定义：
     ```csharp
     public interface IClipboardProvider
     {
         string GetText();
         void SetText(string text);
     }
     ```
   - WPF 实现（`Client.Wpf/Services/WpfClipboardProvider.cs`）：直接调用 `System.Windows.Clipboard`
   - Avalonia 实现（`Client.Avalonia/Services/AvaloniaClipboardProvider.cs`）：
     ```csharp
     public string GetText() => Application.Current.Clipboard.GetTextAsync().GetAwaiter().GetResult();
     public void SetText(string text) => Application.Current.Clipboard.SetTextAsync(text).GetAwaiter().GetResult();
     ```

### 1.9 单元测试（`Client.Common.Tests`）
   - **`FrameBufferTests`** — 全帧替换正确性、增量帧合并正确性（多 Rect 场景）、`IsDirty`/`TryGetFrame` 消费语义
   - **`InputEncoderTests`** — 各类型编码后 `MessageCodec.Decode` 解码验证一致
   - **`ClipboardSyncEngineTests`** — 静默期内返回 null、静默期后正常返回、去重逻辑
   - **`KeepAliveEngineTests`** — 超时触发 OnTimeout、正常 Ack 重置计时器
   - **`MessageDispatcherTests`** — 注册/分发/未注册消息日志

---

## 2. WPF 服务端 — Server.Wpf

> **目标框架**: .NET Framework 4.0
> **输出类型**: WinExe (WPF)
> **依赖**: `EasyRDP.Core` + `EasyDesk.Core` + `EasyDesk.Windows`
> **兼容**: Windows XP SP3 ~ Windows 11

### 2.1 项目清理
   - 删除 `MainWindow.xaml.cs` 中所有残存的旧 Transport/截屏代码，保留空的 `MainWindow` 类
   - 确认 `.csproj` 中无 `System.Windows.Forms` 引用（仅 WPF 依赖：`WindowsBase`, `PresentationCore`, `PresentationFramework`）
   - 确认所有项目引用正确

### 2.2 目录结构
   ```
   Server.Wpf/
   ├── App.xaml                       # 全局样式 + 启动
   ├── App.xaml.cs
   ├── MainWindow.xaml                # 主窗口布局
   ├── MainWindow.xaml.cs
   ├── Models/
   │   ├── ServerConfigModel.cs       # 服务配置（端口/认证/压缩/帧率）
   │   ├── ClientSessionModel.cs      # 客户端会话信息
   │   └── LogEntry.cs                # 日志条目
   ├── ViewModels/
   │   ├── ViewModelBase.cs           # INotifyPropertyChanged 基类
   │   ├── RelayCommand.cs            # ICommand 实现
   │   └── MainViewModel.cs           # 主 VM（服务管理/客户端列表/日志）
   ├── Views/
   │   └── MainWindow.xaml            # （主窗口，根目录已有，此目录放子控件）
   ├── Services/
   │   ├── ServerEngine.cs            # TcpTransportServer 封装
   │   ├── CaptureEngine.cs           # 截屏循环 + 输入注入
   │   └── ClipboardSyncService.cs    # 服务端剪贴板同步
   └── Converters/
       ├── BoolToVisibilityConverter.cs
       └── StatusToColorConverter.cs
   ```

### 2.3 Models

#### `ServerConfigModel.cs`
   - 属性（均触发 `PropertyChanged`）：
     - `Port` (int) — 默认 8750
     - `AuthToken` (string) — 默认 "easyrdp-demo"
     - `CompressType` (string) — "Zlib" 或 "None"，默认 "Zlib"
     - `FrameRate` (int) — 1~60，默认 15
     - `MaxClients` (int) — 0=无限，默认 0
   - 实现 `INotifyPropertyChanged`
   - `static ServerConfigModel Load()`：从 `AppDomain.CurrentDomain.BaseDirectory + "EasyRDP.Server.Wpf.exe.config"` 读取 `<appSettings>` 节（`ConfigurationManager.AppSettings["key"]`）
   - `void Save()`：写入 `ConfigurationManager.AppSettings` + `config.Save()`

#### `ClientSessionModel.cs`
   - 属性（均触发 `PropertyChanged`）：
     - `SessionId` (uint)
     - `RemoteEndPoint` (string)
     - `ConnectedAt` (DateTime)
     - `IsAuthenticated` (bool)
     - `FrameCount` (int)
     - `LastFrameAt` (DateTime)
   - `string DisplayStatus` — 计算属性：`IsAuthenticated ? "已认证" : "握手中"`

#### `LogEntry.cs`
   - 属性：`Timestamp` (DateTime)、`Level` (LogLevel 枚举)、`Message` (string)
   - `string DisplayText` — 计算属性：`$"{Timestamp:HH:mm:ss} [{Level}] {Message}"`（net8.0），net40 用 `string.Format`

### 2.4 ViewModels

#### `ViewModelBase.cs`
   ```csharp
   public abstract class ViewModelBase : INotifyPropertyChanged
   {
       public event PropertyChangedEventHandler PropertyChanged;

       protected void Set<T>(ref T field, T value, string propertyName)
       {
           if (!object.Equals(field, value))
           {
               field = value;
               OnPropertyChanged(propertyName);
           }
       }

       protected void OnPropertyChanged(string propertyName)
       {
           var handler = PropertyChanged;
           if (handler != null)
               handler(this, new PropertyChangedEventArgs(propertyName));
       }
   }
   ```

#### `RelayCommand.cs`
   ```csharp
   public class RelayCommand : ICommand
   {
       private readonly Action _execute;
       private readonly Func<bool> _canExecute;

       public RelayCommand(Action execute, Func<bool> canExecute = null)
       {
           _execute = execute ?? throw new ArgumentNullException("execute");
           _canExecute = canExecute;
       }

       public bool CanExecute(object parameter) { return _canExecute == null || _canExecute(); }
       public event EventHandler CanExecuteChanged
       {
           add { CommandManager.RequerySuggested += value; }
           remove { CommandManager.RequerySuggested -= value; }
       }
       public void Execute(object parameter) { _execute(); }
       public void RaiseCanExecuteChanged() { CommandManager.InvalidateRequerySuggested(); }
   }
   ```

#### `MainViewModel.cs`（核心 VM，约 350 行）

   - **属性**：
     | 属性 | 类型 | 默认值 | 说明 |
     |------|------|--------|------|
     | `Config` | `ServerConfigModel` | `new()` | 服务配置，双向绑定 |
     | `IsRunning` | bool | false | 控制 Start/Stop 按钮 |
     | `IsStopped` | bool | true | `!IsRunning`，控制 Stop 按钮灰显 |
     | `Clients` | `ObservableCollection<ClientSessionModel>` | `new()` | 客户端列表 |
     | `LogEntries` | `ObservableCollection<LogEntry>` | `new()` | 日志（上限 500 条） |
     | `StatusText` | string | "未启动" | 状态栏文本 |
     | `SelectedClient` | `ClientSessionModel` | null | 选中客户端 |

   - **命令**：
     | 命令 | 条件 | 行为 |
     |------|------|------|
     | `StartCommand` | `!IsRunning` | 启动服务、截屏引擎、剪贴板同步 |
     | `StopCommand` | `IsRunning` | 停止所有引擎、断开所有客户端、清空列表 |
     | `SaveConfigCommand` | 始终可用 | 保存配置到 XML |
     | `DisconnectClientCommand` | `SelectedClient != null` | 断开选中客户端 |
     | `ClearLogCommand` | 始终可用 | 清空 `LogEntries` |

   - **`StartCommand` 执行流程**：
     1. 从 `Config` 读取 `Port`、`AuthToken`、`CompressType`、`FrameRate`
     2. `new TcpTransportServer(new TcpTransportOptions { Backlog = 100, MaxClients = Config.MaxClients })`
     3. `_serverEngine.ClientConnected += OnClientConnected`
     4. `_serverEngine.ClientDisconnected += OnClientDisconnected`
     5. `_serverEngine.MessageReceived += OnMessageReceived`
     6. `_serverEngine.Start(Config.Port)`
     7. `_captureEngine = new CaptureEngine(_serverEngine, Config)` → 创建 `IScreenCapturer` 等
     8. `_clipboardSync = new ClipboardSyncService(_serverEngine)` → 启动 STA 线程
     9. `IsRunning = true; IsStopped = false; StatusText = $"运行中 - 端口 {Config.Port}"`
     10. `AddLog(Info, $"Server started on port {Config.Port}")`

   - **`StopCommand` 执行流程**：
     1. `_captureEngine.StopAll()` — 取消所有截屏 `CancellationTokenSource`
     2. `_clipboardSync.Stop()`
     3. `_serverEngine.Stop()` — 断开所有客户端
     4. `Dispatcher.Invoke(() => { Clients.Clear(); LogEntries.Clear(); })`
     5. `IsRunning = false; IsStopped = true; StatusText = "已停止"`
     6. `AddLog(Info, "Server stopped")`

   - **`OnMessageReceived(MessageReceivedEventArgs e)`**：
     ```csharp
     switch (e.Message.Header.Type)
     {
         case HandshakeReq:
             var req = (HandshakeReqMessage)e.Message.Body;
             if (req.AuthToken != Config.AuthToken)
             {
                 var fail = new HandshakeResMessage { Result = AuthFailed };
                 _serverEngine.SendTo(e.SessionId, Encode(HandshakeRes, seq, fail));
                 _serverEngine.Disconnect(e.SessionId);
                 return;
             }
             var success = new HandshakeResMessage { Result = Success, SessionId = e.SessionId, ScreenWidth = screenW, ScreenHeight = screenH, CompressType = compressType };
             _serverEngine.SendTo(e.SessionId, Encode(HandshakeRes, seq, success));
             Dispatcher.Invoke(() => Clients.Add(new ClientSessionModel { SessionId = e.SessionId, RemoteEndPoint = ..., ConnectedAt = DateTime.Now, IsAuthenticated = true }));
             _captureEngine.StartForClient(e.SessionId);

         case InputEvent:
             _captureEngine.HandleInput((InputEventMessage)e.Message.Body);

         case ClipboardData:
             _clipboardSync.OnRemoteClipboard((ClipboardDataMessage)e.Message.Body);

         case KeepAlive:
             _serverEngine.SendTo(e.SessionId, Encode(KeepAliveAck, seq, new KeepAliveAckMessage()));
     }
     ```

   - **日志方法**：
     ```csharp
     void AddLog(LogLevel level, string msg)
     {
         Dispatcher.Invoke(() =>
         {
             if (LogEntries.Count >= 500) LogEntries.RemoveAt(0);
             LogEntries.Add(new LogEntry { Timestamp = DateTime.Now, Level = level, Message = msg });
         });
     }
     ```

### 2.5 Views (XAML)

#### `MainWindow.xaml` 布局
   ```xml
   <Window x:Class="EasyRDP.Server.Wpf.MainWindow"
           Title="EasyRDP Server" Height="600" Width="900"
           WindowStartupLocation="CenterScreen">
     <Window.Resources>
       <!-- 转换器 -->
       <local:BoolToVisibilityConverter x:Key="BoolToVis" />
       <local:StatusToColorConverter x:Key="StatusColor" />
     </Window.Resources>

     <DockPanel>
       <!-- 顶部菜单 -->
       <Menu DockPanel.Dock="Top">
         <MenuItem Header="文件">
           <MenuItem Header="保存配置" Command="{Binding SaveConfigCommand}" />
           <Separator />
           <MenuItem Header="退出" Click="OnExitClick" />
         </MenuItem>
         <MenuItem Header="帮助">
           <MenuItem Header="关于" Click="OnAboutClick" />
         </MenuItem>
       </Menu>

       <!-- 状态栏 -->
       <StatusBar DockPanel.Dock="Bottom">
         <StatusBarItem Content="{Binding StatusText}" />
         <Separator />
         <StatusBarItem Content="客户端:" />
         <StatusBarItem Content="{Binding Clients.Count}" />
       </StatusBar>

       <Grid>
         <Grid.ColumnDefinitions>
           <ColumnDefinition Width="250" />
           <ColumnDefinition Width="*" />
         </Grid.ColumnDefinitions>

         <!-- 左栏：配置 -->
         <GroupBox Grid.Column="0" Header="服务配置" Margin="5">
           <StackPanel>
             <Label>端口</Label>
             <TextBox Text="{Binding Config.Port, UpdateSourceTrigger=PropertyChanged}" />
             <Label>认证令牌</Label>
             <TextBox Text="{Binding Config.AuthToken}" />
             <Label>压缩类型</Label>
             <ComboBox SelectedItem="{Binding Config.CompressType}">
               <ComboBoxItem>Zlib</ComboBoxItem>
               <ComboBoxItem>None</ComboBoxItem>
             </ComboBox>
             <Label>帧率 (1-60)</Label>
             <TextBox Text="{Binding Config.FrameRate}" />
             <Label>最大客户端 (0=无限)</Label>
             <TextBox Text="{Binding Config.MaxClients}" />
             <Button Content="▶ 启动服务" Command="{Binding StartCommand}"
                     IsEnabled="{Binding IsStopped}" Margin="0,10" Height="30" />
             <Button Content="■ 停止服务" Command="{Binding StopCommand}"
                     IsEnabled="{Binding IsRunning}" Margin="0,5" Height="30" />
           </StackPanel>
         </GroupBox>

         <!-- 右栏：客户端列表 + 日志 -->
         <Grid Grid.Column="1" Margin="5">
           <Grid.RowDefinitions>
             <RowDefinition Height="2*" />
             <RowDefinition Height="3*" />
           </Grid.RowDefinitions>

           <!-- 客户端列表 -->
           <GroupBox Grid.Row="0" Header="已连接客户端">
             <ListView ItemsSource="{Binding Clients}"
                       SelectedItem="{Binding SelectedClient}">
               <ListView.View>
                 <GridView>
                   <GridViewColumn Header="ID" DisplayMemberBinding="{Binding SessionId}" Width="40" />
                   <GridViewColumn Header="IP 地址" DisplayMemberBinding="{Binding RemoteEndPoint}" Width="140" />
                   <GridViewColumn Header="状态" DisplayMemberBinding="{Binding DisplayStatus}" Width="60" />
                   <GridViewColumn Header="帧数" DisplayMemberBinding="{Binding FrameCount}" Width="60" />
                   <GridViewColumn Header="连接时间" DisplayMemberBinding="{Binding ConnectedAt, StringFormat='HH:mm:ss'}" Width="80" />
                 </GridView>
               </ListView.View>
               <ListView.ContextMenu>
                 <ContextMenu>
                   <MenuItem Header="断开客户端" Command="{Binding DataContext.DisconnectClientCommand,
                             RelativeSource={RelativeSource AncestorType=Window}}" />
                 </ContextMenu>
               </ListView.ContextMenu>
             </ListView>
           </GroupBox>

           <!-- 日志 -->
           <GroupBox Grid.Row="1" Header="日志">
             <DockPanel>
               <Button DockPanel.Dock="Top" Content="清空日志"
                       Command="{Binding ClearLogCommand}" Height="22" />
               <ListBox ItemsSource="{Binding LogEntries}" VirtualizingPanel.ScrollUnit="Pixel">
                 <ListBox.ItemTemplate>
                   <DataTemplate>
                     <TextBlock Text="{Binding DisplayText}"
                                Foreground="{Binding Level, Converter={StaticResource StatusColor}}"
                                FontFamily="Consolas" FontSize="11" />
                   </DataTemplate>
                 </ListBox.ItemTemplate>
               </ListBox>
             </DockPanel>
           </GroupBox>
         </Grid>
       </Grid>
     </DockPanel>
   </Window>
   ```

#### 托盘集成（`MainWindow.xaml.cs`）
   ```csharp
   private System.Windows.Forms.NotifyIcon _notifyIcon;

   // 在 MainWindow 构造函数中初始化
   _notifyIcon = new System.Windows.Forms.NotifyIcon
   {
       Icon = Properties.Resources.AppIcon,
       Visible = true,
       Text = "EasyRDP Server",
       ContextMenuStrip = new System.Windows.Forms.ContextMenuStrip()
   };
   _notifyIcon.ContextMenuStrip.Items.Add("显示窗口", null, (s, e) => { Show(); WindowState = WindowState.Normal; });
   _notifyIcon.ContextMenuStrip.Items.Add("退出", null, (s, e) => Application.Current.Shutdown());
   _notifyIcon.DoubleClick += (s, e) => { Show(); WindowState = WindowState.Normal; };

   // 窗口最小化到托盘
   protected override void OnStateChanged(EventArgs e)
   {
       base.OnStateChanged(e);
       if (WindowState == WindowState.Minimized)
       {
           Hide();
           ShowInTaskbar = false;
       }
   }

   // 关闭按钮 → 最小化而非退出
   protected override void OnClosing(CancelEventArgs e)
   {
       if (!_forceExit)
       {
           e.Cancel = true;
           WindowState = WindowState.Minimized;
       }
   }
   ```

### 2.6 Services

#### `ServerEngine.cs`
   - 封装 `TcpTransportServer`，提供简洁的 API：
     - `void Start(int port)` → `_tcpServer.Start(port)`
     - `void Stop()` → `_tcpServer.Stop()`
     - `void SendTo(uint sessionId, byte[] data)` → `_tcpServer.SendTo(sessionId, data)`
     - `void Disconnect(uint sessionId)` → `_tcpServer.Disconnect(sessionId)`
   - 事件转发（加互斥锁保护 `Dispatcher.Invoke` 封送）：
     ```csharp
     _tcpServer.ClientConnected += (s, e) =>
     {
         var handler = ClientConnected;
         if (handler != null) handler(s, e);
     };
     // ClientDisconnected、MessageReceived 同理
     ```

#### `CaptureEngine.cs`
   > ⚠️ **实现已演进**：下文为重构前设计。实际代码（commit b545cad）已改为无参构造 +
   > `SendTo`/`CompressType`/`FrameDelayMs` 属性注入，并加入双缓冲（`bufA/bufB` 复用 PrevPixels）、
   > 独立发送线程 + 队列限流（`MaxPendingFrames=3`，丢非关键帧保关键帧）、自适应帧率（空闲降 1fps）。
   > 后续 B-1 阶段（见 `docs/EasyRDP-Codec-Plan-B.md`）将把 `BuildFullFrame`/`BuildDeltaFrame`
   > 压缩逻辑抽离为 `IFrameEncoder`/`BitmapEncoder`，`CaptureEngine` 仅负责采集 + 调用编码器。
   - 构造函数：`CaptureEngine(ServerEngine server, ServerConfigModel config)`
     - `_capturer = new WindowsDesktopFactory().CreateScreenCapturer()`
     - `_input = new WindowsDesktopFactory().CreateInputSimulator()`
     - `_cursor = new WindowsDesktopFactory().CreateCursorCapturer()`
   - 管理 `Dictionary<uint, CancellationTokenSource> _clientTokens`
   - `void StartForClient(uint sessionId)`：
     1. `var cts = new CancellationTokenSource()`
     2. `_clientTokens[sessionId] = cts`
     3. `var t = new Thread(() => CaptureLoop(sessionId, cts.Token)) { IsBackground = true, Name = $"Capture-{sessionId}" }`
     4. `t.Start()`
   - `CaptureLoop(uint sessionId, CancellationToken ct)`：
     1. `int keyFrameInterval = 30`，`int frameCount = 0`
     2. `byte[] prevPixels = null; int prevW = 0, prevH = 0`
     3. **while (!ct.IsCancellationRequested)**：
        - `lock(_captureLock)`：`var frame = _capturer.CaptureScreen()`
        - `try { ... } finally { Marshal.FreeHGlobal(frame.Scan0); }`
        - `Marshal.Copy(frame.Scan0, curPixels, 0, pixelSize)`
        - 判定是否关键帧：`frameCount % keyFrameInterval == 0 || prevPixels == null || prevW != w || prevH != h`
        - 是 → `msg = BuildFullFrame(w, h, curPixels)`
        - 否 → `msg = BuildDeltaFrame(w, h, curPixels, prevPixels)`
        - 如果 `msg.Pixels.Length >= pixelSize` → 退化为全帧
        - `_server.SendTo(sessionId, MessageCodec.Encode(ScreenFrame, seq, msg))`
        - 光标：`_cursor.GetCursorPosition(out cx, out cy)` → `CursorUpdateMessage` → 发送
        - `prevPixels = curPixels; prevW = w; prevH = h; frameCount++`
        - `Thread.Sleep(1000 / Config.FrameRate)`
   - `BuildFullFrame(int w, int h, byte[] raw)`：
     - `byte[] compressed = CompressHelper.Compress(raw, compressType)`
     - 返回 `new ScreenFrameMessage { FrameType = Full, Compress = ..., Rects = [new ScreenRect { X=0,Y=0,Width=w,Height=h,Offset=0 }], Pixels = compressed }`
   - `BuildDeltaFrame(int w, int h, byte[] cur, byte[] prev)`：
     - `var rects = DirtyRectDetector.Detect(cur, prev, w, h)`
     - 无变化 → 返回全帧（`Pixels = new byte[0]`，占位）
     - 逐 Rect 提取像素合并 → `CompressHelper.Compress` → 返回
   - `void HandleInput(InputEventMessage msg)`：
     - 遍历 `msg.Units`：
       ```csharp
       switch (msg.EventType) {
           case MouseMove: _input.SendMouseMove(unit.X, unit.Y, unit.Absolute); break;
           case MouseDown: _input.SendMouseButton((MouseButton)unit.Button, true); break;
           case MouseUp:   _input.SendMouseButton((MouseButton)unit.Button, false); break;
           case MouseWheel:_input.SendMouseWheel(unit.WheelDelta); break;
           case KeyDown:   _input.SendKeyDown((VirtualKeyCode)unit.VirtualKey); break;
           case KeyUp:     _input.SendKeyUp((VirtualKeyCode)unit.VirtualKey); break;
           case UnicodeText: _input.SendText(unit.Text); break;
       }
       ```
   - `void StopForClient(uint sessionId)`：`_clientTokens[sessionId].Cancel()` + 等待线程
   - `void StopAll()`：遍历 `_clientTokens`，全部 Cancel

#### `ClipboardSyncService.cs`
   - 构造函数：`ClipboardSyncService(ServerEngine server)`
     - `_clipboard = new WindowsDesktopFactory().CreateClipboardService()`（在 STA 线程）
     - `_server = server`
   - `void Start()`：`_cts = new CancellationTokenSource()` → 新 STA 线程 `ClipboardLoop`
   - `ClipboardLoop`：
     1. `Thread.Sleep(300)` 轮询
     2. 新建 STA 子线程执行 `_clipboard.GetText()`
     3. 与 `_lastSentText` 比较，变化 → `ClipboardDataMessage` → `_server.SendTo(sessionId, encoded)`（对所有认证客户端广播）
     4. `_lastSentText = text`
   - `void OnRemoteClipboard(ClipboardDataMessage msg)`：
     1. 如果 `DateTime.Now < _cooldownUntil` → 忽略
     2. STA 线程 `_clipboard.SetText(msg.Text)`
     3. `_cooldownUntil = DateTime.Now.AddMilliseconds(500)`
     4. `_lastSentText = msg.Text`
   - 注意：剪贴板操作需 STA，子线程 `thread.SetApartmentState(ApartmentState.STA)`

---

## 3. WPF 客户端 — Client.Wpf

> **目标框架**: .NET Framework 4.0
> **输出类型**: WinExe (WPF)
> **依赖**: `EasyRDP.Core` + `EasyRDP.Client.Common` + `EasyDesk.Core` + `EasyDesk.Windows`
> **兼容**: Windows XP SP3 ~ Windows 11

### 3.1 项目清理
   - 删除 `.csproj` 中 `<Reference Include="System.Windows.Forms" />`（残存）
   - 删除 `MainWindow.xaml.cs` 中旧 WinForms 逻辑
   - 添加 `EasyRDP.Client.Common` 项目引用

### 3.2 目录结构
   ```
   Client.Wpf/
   ├── App.xaml
   ├── App.xaml.cs
   ├── MainWindow.xaml
   ├── MainWindow.xaml.cs
   ├── ViewModels/
   │   ├── ViewModelBase.cs          # （复用，与 Server.Wpf 相同）
   │   ├── RelayCommand.cs           # （复用）
   │   └── MainViewModel.cs          # 客户端主 VM
   ├── Views/
   │   └── ConnectPanel.xaml         # 连接面板 UserControl
   ├── Services/
   │   ├── WpfRenderEngine.cs        # WPF WriteableBitmap 渲染
   │   ├── WpfInputCapturer.cs       # WPF 鼠标/键盘事件捕获 + 按键映射
   │   └── WpfClipboardProvider.cs   # IClipboardProvider WPF 实现
   └── Converters/
       └── ConnectionStateToColorConverter.cs
   ```

### 3.3 MainViewModel（客户端核心，约 300 行）
   - **持有 `Client.Common` 实例**：
     ```csharp
     ConnectionManager _connection = new ConnectionManager();
     MessageDispatcher _dispatcher = new MessageDispatcher();
     FrameBuffer _frameBuffer = new FrameBuffer();
     InputEncoder _inputEncoder = new InputEncoder();
     ClipboardSyncEngine _clipboardSync = new ClipboardSyncEngine();
     KeepAliveEngine _keepAlive = new KeepAliveEngine();
     IClipboardProvider _clipboard = new WpfClipboardProvider();
     ```
   - **属性**：
     | 属性 | 类型 | 说明 |
     |------|------|------|
     | `Host` | string | 服务端 IP |
     | `Port` | int | 服务端口，默认 8750 |
     | `AuthToken` | string | 认证令牌 |
     | `IsConnected` | bool | 控制连接/远程桌面视图切换 |
     | `IsConnecting` | bool | 连接中动画 |
     | `StatusText` | string | 状态栏 |
     | `FrameBitmap` | WriteableBitmap | 渲染目标（Image.Source 绑定） |
     | `ZoomLevel` | double | 默认 1.0 |
     | `IsFullScreen` | bool | 全屏标志 |
     | `Fps` | double | 实时帧率 |
   - **命令**：
     | 命令 | 条件 | 行为 |
     |------|------|------|
     | `ConnectCommand` | `!IsConnected && !IsConnecting` | 开始连接流程 |
     | `DisconnectCommand` | `IsConnected` | 断开并回到连接面板 |
     | `ToggleFullScreenCommand` | `IsConnected` | 切换全屏 |
     | `SendCtrlAltDelCommand` | `IsConnected` | 发送 Ctrl+Alt+Del |

   - **`ConnectCommand` 执行流程**（异步 — 在后台线程）：
     1. `IsConnecting = true; StatusText = "正在连接..."`
     2. `_connection.OnConnected += () => Dispatcher.Invoke(() => OnConnectedSuccess())`
     3. `_connection.OnConnectionFailed += (reason) => Dispatcher.Invoke(() => { IsConnecting = false; StatusText = $"连接失败: {reason}"; })`
     4. `_connection.OnDisconnected += (reason) => Dispatcher.Invoke(() => OnDisconnected(reason))`
     5. `Task.Run(() => _connection.BeginConnect(Host, Port, 5000, AuthToken))`

   - **`OnConnectedSuccess()`**：
     1. `_dispatcher.RegisterHandler<ScreenFrameMessage>(msg => { _frameBuffer.ProcessFrame(msg); Dispatcher.Invoke(() => Render()); })`
     2. `_dispatcher.RegisterHandler<CursorUpdateMessage>(msg => Dispatcher.Invoke(() => UpdateCursor(msg)))`
     3. `_dispatcher.RegisterHandler<ClipboardDataMessage>(msg => { var text = _clipboardSync.OnRemoteClipboard(msg); if (text != null) { _clipboard.SetText(text); _clipboardSync.BeginCooldown(); } })`
     4. `_connection.OnMessage += body => _dispatcher.Dispatch(body)`
     5. `_keepAlive.Start(() => _connection.SendMessage(KeepAlive, new KeepAliveMessage()), cts.Token)`
     6. 启动剪贴板监控线程（300ms 轮询，30ms 静默期后发送变更）
     7. 启动 FPS 计数器（每 2 秒计算 `_frameBuffer.FrameCount / 2`）
     8. `IsConnected = true; IsConnecting = false; StatusText = "已连接"`
     9. `_renderEngine = new WpfRenderEngine(_connection.RemoteScreenWidth, _connection.RemoteScreenHeight)`

   - **`OnDisconnected(string reason)`**：
     1. `_keepAlive.Stop()`
     2. 停止剪贴板监控
     3. `IsConnected = false; StatusText = $"断开: {reason}"; FrameBitmap = null`

   - **`Render()`**（UI 线程）：
     1. `if (!_frameBuffer.TryGetFrame(out byte[] pixels, out int w, out int h)) return`
     2. `_renderEngine.Render(pixels, w, h)` → 更新 `FrameBitmap`
     3. 通知 `OnPropertyChanged(nameof(FrameBitmap))`

### 3.4 Views

#### `ConnectPanel.xaml`（UserControl）
   ```xml
   <UserControl x:Class="EasyRDP.Client.Wpf.Views.ConnectPanel">
     <GroupBox Header="连接到 EasyRDP 服务端" Width="300">
       <StackPanel>
         <Label>服务端地址</Label>
         <TextBox Text="{Binding Host}" />
         <Label>端口</Label>
         <TextBox Text="{Binding Port}" />
         <Label>认证令牌</Label>
         <PasswordBox x:Name="PwdBox" PasswordChanged="OnPasswordChanged" />
         <Button Content="连接" Command="{Binding ConnectCommand}"
                 IsEnabled="{Binding IsConnecting, Converter={StaticResource InvertBool}}"
                 Height="30" Margin="0,10" />
         <TextBlock Text="{Binding StatusText}" Foreground="Gray" TextAlignment="Center" />
       </StackPanel>
     </GroupBox>
   </UserControl>
   ```
   - `OnPasswordChanged`：`((MainViewModel)DataContext).AuthToken = PwdBox.Password`

#### `MainWindow.xaml` 布局
   ```xml
   <Window Title="EasyRDP Client" Height="768" Width="1024">
     <Grid>
       <!-- 连接面板（未连接时显示） -->
       <views:ConnectPanel Visibility="{Binding IsConnected,
             Converter={StaticResource InvertBoolToVis}}" />

       <!-- 远程桌面（已连接时显示） -->
       <Image x:Name="RemoteImage" Source="{Binding FrameBitmap}"
              Visibility="{Binding IsConnected, Converter={StaticResource BoolToVis}}"
              Stretch="Uniform"
              MouseMove="OnMouseMove" MouseDown="OnMouseDown" MouseUp="OnMouseUp"
              MouseWheel="OnMouseWheel"
              KeyDown="OnKeyDown" KeyUp="OnKeyUp" Focusable="True" />
     </Grid>

     <!-- 状态栏 -->
     <StatusBar DockPanel.Dock="Bottom">
       <StatusBarItem Content="{Binding StatusText}" />
       <Separator />
       <StatusBarItem Content="FPS:" />
       <StatusBarItem Content="{Binding Fps, StringFormat='{0:F0}'}" />
     </StatusBar>
   </Window>
   ```

### 3.5 Services

#### `WpfRenderEngine.cs`
   - 构造函数：`WpfRenderEngine(int w, int h)`
     - `_bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null)`
   - `void Render(byte[] bgra32Pixels, int w, int h)`（UI 线程调用）：
     1. 如果 `_bitmap == null || _bitmap.PixelWidth != w || _bitmap.PixelHeight != h`：重建 `_bitmap`
     2. `_bitmap.Lock()`
     3. `Marshal.Copy(bgra32Pixels, 0, _bitmap.BackBuffer, bgra32Pixels.Length)`
     4. `_bitmap.AddDirtyRect(new Int32Rect(0, 0, w, h))`
     5. `_bitmap.Unlock()`
   - `WriteableBitmap Bitmap` → `_bitmap`（绑定到 `FrameBitmap`）

#### `WpfInputCapturer.cs`
   - 构造函数：`WpfInputCapturer(InputEncoder encoder, ConnectionManager connection, int screenW, int screenH)`
   - `void OnMouseMove(MouseEventArgs e, UIElement element)`：
     1. `Point pos = e.GetPosition(element)`
     2. 计算实际屏幕坐标：`short x = (short)(pos.X * _screenW / element.RenderSize.Width)`、`short y = (short)(pos.Y * _screenH / element.RenderSize.Height)`
     3. `byte[] data = _encoder.EncodeMouseMove(true, x, y, _connection.SeqTracker.Next())`
     4. `_connection.Transport.Send(data)`
   - `void OnMouseDown(MouseButtonEventArgs e)`：`_encoder.EncodeMouseButton(true, MapButton(e.ChangedButton), seq)` → send
   - `void OnMouseUp(MouseButtonEventArgs e)`：同上，`false`
   - `void OnMouseWheel(MouseWheelEventArgs e)`：`_encoder.EncodeMouseWheel((short)(e.Delta / 120 * 120), seq)` → send
   - `void OnKeyDown(KeyEventArgs e)`：`_encoder.EncodeKey(true, MapKey(e.Key), 0, seq)` → send
   - `void OnKeyUp(KeyEventArgs e)`：同上，`false`
   - `byte MapButton(MouseButton btn)`：`Left→0, Right→1, Middle→2, XButton1→3, XButton2→4`
   - `byte MapKey(Key key)`：
     ```csharp
     if (key >= Key.A && key <= Key.Z) return (byte)(0x41 + (key - Key.A));
     if (key >= Key.D0 && key <= Key.D9) return (byte)(0x30 + (key - Key.D0));
     if (key >= Key.F1 && key <= Key.F12) return (byte)(0x70 + (key - Key.F1));
     if (key >= Key.NumPad0 && key <= Key.NumPad9) return (byte)(0x60 + (key - Key.NumPad0));
     switch (key) {
         case Key.Escape: return 0x1B; case Key.Enter: return 0x0D;
         case Key.Back: return 0x08; case Key.Tab: return 0x09;
         case Key.Space: return 0x20;
         case Key.LeftCtrl: case Key.RightCtrl: return 0x11;
         case Key.LeftAlt: case Key.RightAlt: return 0x12;
         case Key.LeftShift: case Key.RightShift: return 0x10;
         case Key.LWin: return 0x5B; case Key.RWin: return 0x5C;
         case Key.Delete: return 0x2E; case Key.Insert: return 0x2D;
         case Key.Home: return 0x24; case Key.End: return 0x23;
         case Key.PageUp: return 0x21; case Key.PageDown: return 0x22;
         case Key.Left: return 0x25; case Key.Up: return 0x26;
         case Key.Right: return 0x27; case Key.Down: return 0x28;
         default: return (byte)KeyInterop.VirtualKeyFromKey(key);
     }
     ```

#### `WpfClipboardProvider.cs`
   ```csharp
   public class WpfClipboardProvider : IClipboardProvider
   {
       public string GetText() { return System.Windows.Clipboard.GetText(); }
       public void SetText(string text) { System.Windows.Clipboard.SetText(text); }
   }
   ```

---

## 4. Avalonia 客户端 — Client.Avalonia

> **目标框架**: .NET 8
> **输出类型**: WinExe (Avalonia)
> **依赖**: `EasyRDP.Core` + `EasyRDP.Client.Common` + `EasyDesk.Core` + `EasyDesk.Windows`
> **NuGet**: Avalonia 12.0.3, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Fonts.Inter
> **兼容**: Windows 7+ / Linux / macOS

### 4.1 项目补完
   - 当前已有骨架（`App.axaml` + `MainWindow.axaml` + `Program.cs`）
   - 补充引用：`EasyRDP.Client.Common` + `EasyDesk.Core` + `EasyDesk.Windows`
   - 目录结构：
     ```
     Client.Avalonia/
     ├── App.axaml
     ├── App.axaml.cs
     ├── MainWindow.axaml
     ├── MainWindow.axaml.cs
     ├── Program.cs
     ├── ViewModels/
     │   └── MainViewModel.cs
     ├── Views/
     │   └── ConnectPanel.axaml
     ├── Services/
     │   ├── AvaloniaRenderEngine.cs
     │   ├── AvaloniaInputCapturer.cs
     │   └── AvaloniaClipboardProvider.cs
     └── Converters/
     ```

### 4.2 与 WPF 客户端的差异清单
   | 组件 | WPF | Avalonia |
   |------|-----|----------|
   | 图像控件 | `System.Windows.Controls.Image` + `WriteableBitmap(PixelFormats.Bgra32)` | `Avalonia.Controls.Image` + `WriteableBitmap(PixelFormat.Bgra8888, AlphaFormat.Premul)` |
   | 像素拷贝 | `bitmap.Lock()` → `Marshal.Copy` → `AddDirtyRect` → `Unlock()` | `using var fb = bitmap.Lock()` → `Marshal.Copy(pixels, 0, fb.Address, ...)` — Lock 返回 disposable，自动提交 |
   | 鼠标事件 | `MouseMove/Down/Up/Wheel`（`MouseEventArgs`） | `PointerMoved/Pressed/Released`（`PointerEventArgs`）+ `PointerWheelChanged`（`PointerWheelEventArgs`） |
   | 按键事件 | `KeyDown/Up` + `System.Windows.Input.Key` 枚举 | `KeyDown/Up` + `Avalonia.Input.Key` 枚举 |
   | 按键映射 | `MapWpfKey(Key key)` | `MapAvaloniaKey(Avalonia.Input.Key key)` — 两套映射表 |
   | 剪贴板 | `System.Windows.Clipboard.GetText/SetText`（同步） | `Application.Current.Clipboard.GetTextAsync/SetTextAsync`（异步，`GetAwaiter().GetResult()`） |
   | 全屏 | `WindowStyle=None + Maximized + Topmost` | `WindowState=FullScreen` |
   | 线程调度 | `Application.Current.Dispatcher.Invoke` | `Dispatcher.UIThread.Post` / `InvokeAsync` |
   | 托盘 | `System.Windows.Forms.NotifyIcon` | `TrayIcon` 控件（Avalonia 原生） |

### 4.3 `AvaloniaRenderEngine.cs`
   ```csharp
   public class AvaloniaRenderEngine
   {
       private WriteableBitmap _bitmap;

       public AvaloniaRenderEngine(int w, int h)
       {
           _bitmap = new WriteableBitmap(
               new PixelSize(w, h), new Vector(96, 96),
               PixelFormat.Bgra8888, AlphaFormat.Premul);
       }

       public void Render(byte[] bgra32Pixels, int w, int h)
       {
           if (_bitmap == null || _bitmap.PixelSize.Width != w || _bitmap.PixelSize.Height != h)
           {
               _bitmap?.Dispose();
               _bitmap = new WriteableBitmap(
                   new PixelSize(w, h), new Vector(96, 96),
                   PixelFormat.Bgra8888, AlphaFormat.Premul);
           }

           using (var fb = _bitmap.Lock())
           {
               Marshal.Copy(bgra32Pixels, 0, fb.Address, bgra32Pixels.Length);
           }
           // Lock using 自动提交脏矩形
       }

       public IImage Bitmap => _bitmap;
   }
   ```

### 4.4 `AvaloniaInputCapturer.cs`
   - `OnPointerMoved(PointerEventArgs e, Control element)`：Avalonia 使用 `PointerEventArgs`，坐标获取方式：`e.GetPosition(element)`
   - `void OnPointerPressed(PointerPressedEventArgs e)`：`e.GetCurrentPoint(element).Properties.PointerUpdateKind` → 判断左右键
   - `void OnPointerWheelChanged(PointerWheelEventArgs e)`：`e.Delta.Y` → `WheelDelta`
   - `MapAvaloniaKey(Avalonia.Input.Key key)`：
     - Avalonia `Key` 枚举值大部分直接对应 VK（A=65, Z=90）
     - 特殊键需独立映射表（`Key.Escape`→0x1B, `Key.Enter`→0x0D 等，值可能与 WPF `Key` 不同）

### 4.5 `AvaloniaClipboardProvider.cs`
   ```csharp
   public class AvaloniaClipboardProvider : IClipboardProvider
   {
       public string GetText()
       {
           return Application.Current.Clipboard.GetTextAsync().GetAwaiter().GetResult();
       }
       public void SetText(string text)
       {
           Application.Current.Clipboard.SetTextAsync(text).GetAwaiter().GetResult();
       }
   }
   ```
   - 注意：Avalonia 剪贴板 API 是异步的，但因为 `IClipboardProvider` 接口是同步的，这里用 `.GetAwaiter().GetResult()` 同步等待。需确保不在 UI 线程上调用以避免死锁——在 `ClipboardMonitor` 线程中调用即可

### 4.6 Views (AXAML)
   - `ConnectPanel.axaml` — 同 WPF 的 `ConnectPanel.xaml`，替换 WPF 控件名为 Avalonia 控件名：
     - `UserControl` → `UserControl`
     - `TextBox` → `TextBox`、`PasswordBox` → `TextBox PasswordChar="*"`
     - `Button` → `Button`
     - 绑定语法：`{Binding Host}`（Avalonia 默认 TwoWay）
   - `MainWindow.axaml` — 同 WPF 布局，`Image Source="{Binding FrameBitmap}"`
   - 托盘：`<TrayIcon Icon="/Assets/app.ico" ToolTipText="EasyRDP Client" />`
   - 全屏：`WindowState="FullScreen"`

---

## 5. 控制台入口维护

### 5.1 Server — `src/EasyRDP.Server/Program.cs`
   - 保持现有控制台无头服务端逻辑
   - 后续增加命令行参数支持：`--port 8750 --auth xxx --compress Zlib --fps 15`
   - 集成 `TcpTransportServer`（当前已完成）

### 5.2 Client — `src/EasyRDP.Client/Program.cs`
   - 保持现有 WinForms 入口作为基本测试入口
   - WPF/Avalonia 客户端完成后，此项目：
     - 保留作为 headless 测试工具
     - 或降级为仅连接+消息验证的最小桩
     - 或视需求删除

---

## 6. Avalonia 服务端（待定）

> 暂不实现。WPF 服务端已覆盖 Windows XP~11 主力场景。
> Linux 服务端需求明确后再开，届时可复用 WPF 服务端的 `CaptureEngine`、`ClipboardSyncService` 等纯逻辑组件。

---

## 7. 跨平台 EasyDesk 扩展（后续阶段）

### 7.1 EasyDesk.Linux (netstandard2.0)
   - `IScreenCapturer` — X11: `XOpenDisplay` → `XShmGetImage`（共享内存，高性能）
   - `IInputSimulator` — X11: `XTestFakeKeyEvent` / `XTestFakeButtonEvent` / `XTestFakeMotionEvent`
   - `ICursorCapturer` — X11: `XQueryPointer`
   - `IClipboardService` — X11: `XOpenDisplay` + Selection/Owner（`XA_PRIMARY` 和 `CLIPBOARD`）

### 7.2 EasyDesk.Mac (netstandard2.0)
   - `IScreenCapturer` — CoreGraphics: `CGDisplayCreateImage`
   - `IInputSimulator` — CoreGraphics: `CGEventCreateMouseEvent` / `CGEventCreateKeyboardEvent`
   - `ICursorCapturer` — CoreGraphics: `CGEventGetLocation`
   - `IClipboardService` — AppKit: `NSPasteboard.generalPasteboard`

### 7.3 条件编译工厂
   - `DesktopFactory` 根据平台 `#if WINDOWS / LINUX / MACOS` 返回对应实现

---

## 8. 测试计划

### 8.1 Client.Common 单元测试
   - `FrameBufferTests` — 全帧替换、增量合并多 Rect、`IsDirty`/`TryGetFrame` 消费语义、空帧
   - `InputEncoderTests` — 编解码往返验证（Encode→Decode）、各类型正确性
   - `ClipboardSyncEngineTests` — 静默期隔离、文本去重、远程接收后本地不发
   - `KeepAliveEngineTests` — 超时触发、正常 Ack 重置、Start/Stop 线程安全
   - `MessageDispatcherTests` — 注册/分发、未注册静默日志、多次注册覆盖

### 8.2 WPF 服务端集成测试
   - 启动停止 → 验证 `TcpTransportServer` 生命周期
   - 多客户端连接 → 验证 `ClientSessionModel` 正确创建和清理
   - 截屏推送 → 验证 `CaptureEngine` 帧率、全帧/增量帧比例
   - 输入注入 → Mock `IInputSimulator`，验证 `InputEventMessage` 分发

### 8.3 WPF 客户端集成测试
   - 连接流程 → 握手成功 → 帧渲染 → 输入发送 全流程
   - 断连重连 → `KeepAlive` 超时 → 自动重连

### 8.4 Avalonia 客户端集成测试
   - 同 WPF 测试流程
   - 验证跨平台渲染一致性（Windows/Linux 双平台各跑）

---

## 9. 打包与发布

### 9.1 WPF 版（服务端 + 客户端）
   - 目标框架：`.NET Framework 4.0`，系统预装运行时
   - 发布产物：4 个文件 — `EasyRDP.Server.Wpf.exe` / `EasyRDP.Client.Wpf.exe` + `EasyRDP.Core.dll` + `EasyDesk.Core.dll` + `EasyDesk.Windows.dll` + 配置文件
   - 打包工具：Inno Setup / NSIS 制作安装包
   - XP 兼容：编译目标 `net40`，确保 `SendInput`（XP SP2+）、`Bitmap`/`Graphics` 等 API 可用

### 9.2 Avalonia 版（仅客户端）
   - 发布命令：
     ```bash
     # Windows x64
     dotnet publish src/EasyRDP.Client.Avalonia -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
     # Linux x64
     dotnet publish src/EasyRDP.Client.Avalonia -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true
     # macOS x64
     dotnet publish src/EasyRDP.Client.Avalonia -c Release -r osx-x64 --self-contained -p:PublishSingleFile=true
     ```
   - 输出：单文件可执行（~60-80 MB 含 .NET 运行时），零依赖

---

## 开发顺序建议

| 序号 | 阶段 | 预估工时 | 依赖 |
|------|------|----------|------|
| 1 | `Client.Common` 库 + 单元测试 | 2-3 天 | EasyRDP.Core ✅ |
| 2 | WPF 服务端 | 3-4 天 | Client.Common（`InputEncoder` 共用） |
| 3 | WPF 客户端 | 3-4 天 | Client.Common |
| 4 | Avalonia 客户端 | 2-3 天 | Client.Common |
| 5 | 集成测试 | 2 天 | 2-4 全部 |
| 6 | 打包与发布 | 1 天 | 全部 |
| 7 | 跨平台 EasyDesk（后续） | 待评估 | EasyDesk.Core |
