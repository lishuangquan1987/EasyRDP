# EasyRDP 编码层抽象改进计划（B1–B4）与修改记录

> 最后更新：2026-07-23
>
> 目标：在不破坏 XP 兼容性的前提下，引入可插拔编码层（Bitmap / H.264 软编 / H.264 硬编），
> 让远程图像传输"不卡、不缺画面"，鼠标操作顺滑，服务端可配置、客户端自动识别。

---

## 0. 背景与目标

### 0.1 用户反馈的问题

1. **远程图像传输不全、不快** —— 出现马赛克/缺帧，刷新慢。
2. **鼠标操作不方便** —— 左键失效、画面卡死、坐标偏移。
3. **服务端必须兼容 XP 32 位** —— 这是硬约束。

### 0.2 业界参考

- **RustDesk / 腾讯会议共享屏幕**：均采用 H.264 视频编码，相比原始位图传输有 10–50 倍压缩比，
  配合增量 IDR/帧间预测可在低带宽下保持流畅。
- **OpenH264**：Cisco 提供的纯软件 H.264 编解码器，BSD 许可，XP 兼容，无需 GPU。
- **硬件编码（NVENC/QSV/Media Foundation）**：Win8+ 可用，延迟更低，但需 net8.0。

### 0.3 选定方案：Plan B —— 编码层抽象

将"如何把屏幕像素变成传输字节"抽象成可插拔后端，原有 Bitmap + Zlib/JPEG 路径保留为兜底，
新增 H.264 路径作为高压缩选项。传输层（TCP/UDP）保持不变。

| 方案 | 改动大小 | 兼容性 | 选用 |
|---|---|---|---|
| A：仅优化现有 Bitmap 路径 | 小 | 完全兼容 | ✗ 压缩比天花板低 |
| **B：抽象编码层 + 可插拔后端** | 中 | 向后兼容 | ✓ |
| C：重写协议+传输 | 大 | 破坏性 | ✗ 过度工程 |

**核心设计原则**：
- `EasyRDP.Core` 是唯一共享库，所有协议 + 编码逻辑集中于此，多目标 `net40;net8.0`。
- H.264 等 P/Invoke 代码用 `#if NET8_0_OR_GREATER` 条件编译，net40/XP 路径仅保留 Bitmap。
- 新消息类型放在保留区间，握手扩展字节通过负载长度检测，老客户端/老服务端无缝兼容。
- 每客户端独立 H.264 编码器实例（H.264 有参考帧状态，不能跨客户端共享）。

---

## 1. B-1：编码层抽象

### 1.1 目标

将原本硬编码在 `CaptureEngine` 中的"取像素 → 脏矩形 → 压缩"流程抽离为独立编码器，
为后续 H.264 后端预留统一入口。

### 1.2 计划改动

#### 新增 `src/EasyRDP.Core/Protocol/IFrameEncoder.cs`

位图编码器接口（ScreenFrame 路径，net40/net8.0 通用）：

```csharp
namespace EasyRDP.Core.Protocol
{
    /// <summary>位图帧编码器（ScreenFrame 消息路径）。兼容 net40 / C# 5.0。</summary>
    public interface IFrameEncoder
    {
        /// <summary>当前编码器标识</summary>
        CodecId Codec { get; }

        /// <summary>
        /// 编码一帧。返回 null 表示无变化（可跳过发送）。
        /// </summary>
        /// <param name="width">屏幕宽</param>
        /// <param name="height">屏幕高</param>
        /// <param name="curPixels">当前帧 BGRA32 像素</param>
        /// <param name="prevPixels">上一帧像素（可为 null）</param>
        /// <param name="forceKey">强制关键帧（全帧）</param>
        ScreenFrameMessage Encode(int width, int height, byte[] curPixels, byte[] prevPixels, bool forceKey);

        /// <summary>重置内部状态（客户端请求关键帧、尺寸变化、重连时调用）</summary>
        void Reset();
    }
}
```

#### 新增 `src/EasyRDP.Core/Protocol/BitmapEncoder.cs`

从 `CaptureEngine` 提取的位图编码实现：
- 脏矩形检测（`DirtyRectDetector`）
- 自动选择 `CompressType`：纯色/小图用 Zlib，复杂/大图用 JPEG
- `Encode()` 返回 `ScreenFrameMessage`（`FrameType.Full` 或 `Delta`）
- 实现 `IFrameEncoder`，`Codec == CodecId.Bitmap`

#### 修改 `src/EasyRDP.Server.Wpf/Services/CaptureEngine.cs`

- 不再内联压缩逻辑，改为持有 `IFrameEncoder _encoder` 实例
- 调用 `_encoder.Encode(w, h, cur, prev, forceKey)` 得到 `ScreenFrameMessage`
- 通过 `EncoderFactory` 创建（便于后续切换后端）

#### 新增 `src/EasyRDP.Core/Protocol/EncoderFactory.cs`（初始版本）

```csharp
public static class EncoderFactory
{
    public static IFrameEncoder CreateFrame(CodecId codec)
    {
        switch (codec)
        {
            case CodecId.Bitmap: return new BitmapEncoder();
            default: throw new NotSupportedException("Codec not supported: " + codec);
        }
    }
}
```

### 1.3 状态

**已落地**（代码已提交，编译通过）。`IFrameEncoder`/`BitmapEncoder`/`EncoderFactory` 已创建，`CaptureEngine` 已重构为使用编码器接口。

---

## 2. B-2：协议扩展 + 能力协商

### 2.1 目标

在不破坏老协议的前提下，让客户端在握手中声明"我能解哪些编码"，服务端据此选出双方都支持的编码，
并在握手响应中告知客户端最终选用哪种。**关键点：老客户端/老服务端无感知，自动降级到 Bitmap。**

### 2.2 兼容性策略

- 新消息类型 `VideoFrame = 0x50` 放在保留区间（`0x50–0x6F` 预留给视频流）。
  > ⚠️ **跨文档冲突（已协调）**：`docs/EasyRDP-Protocol-v1.md` 旧版 13.3 曾把 `0x50` 预留给 `AudioData`，
  > 与本计划的 `VideoFrame = 0x50` 冲突。协调结论：**`0x50–0x6F` 统一划归视频流**，
  > `VideoFrame = 0x50`；`AudioData` 占位挪至 `0x70–0x7F` 音频区。协议文档已同步修订。
- `HandshakeReq` 末尾追加 1 字节能力位（**可选**）。老客户端的负载长度不含此字节，
  服务端通过 `payload.Length` 检测是否存在扩展字节 —— 存在则读取，不存在则视为 `Legacy`。
- `HandshakeRes` 末尾追加 1 字节协商结果（**可选**）。同理，老服务端不含此字节，
  客户端检测负载长度决定是否读取。
- 任何一方为旧版本时，自动降级到 `CodecId.Bitmap`。

### 2.3 计划改动（已部分落地）

#### ✅ `src/EasyRDP.Core/Protocol/CodecId.cs`（已提交）

```csharp
public enum CodecId : byte
{
    Bitmap = 0,          // 位图编码（脏矩形 + Zlib/JPEG）。net40/net8.0 通用，XP 兼容。
    H264Software = 1,    // OpenH264 软件编码（Cisco BSD，XP 兼容）。仅 net8.0 服务端可用。
    H264Hardware = 2     // 硬件编码（NVENC/QSV/Media Foundation）。Win8+，仅 net8.0（B-4 阶段）。
}
```

#### ✅ `src/EasyRDP.Core/Protocol/CodecCapabilities.cs`（已提交）

`[Flags]` 枚举，握手请求中客户端声明可解码的编码：

```csharp
[Flags]
public enum CodecCapabilities : byte
{
    Legacy = 0,              // 老协议（无扩展字节）
    Bitmap = 1,              // 支持位图
    H264Software = 2,        // 支持 OpenH264 软解
    H264Hardware = 4,        // 支持硬解（B-4）
    All = Bitmap | H264Software | H264Hardware
}
```

扩展方法：
- `Normalize()`：`Legacy` → `Bitmap`（老客户端兜底）
- `Has(CodecId)`：判断是否具备指定编码能力

#### ✅ `src/EasyRDP.Core/Protocol/CodecNegotiator.cs`（已提交）

无状态协商器，优先级 `H264Hardware > H264Software > Bitmap`：

```csharp
public static CodecCapabilities GetServerCapabilities(CodecId serverCodec);
public static CodecId Negotiate(CodecCapabilities clientCaps, CodecCapabilities serverCaps);
```

- 服务端配 H264Software 时声明 `Bitmap | H264Software`（允许 Bitmap 客户端连接，自动降级）。
- 交集为空时回退 `Bitmap`（保证连接可用）。

#### ⏳ `src/EasyRDP.Core/Protocol/Messages/HandshakeReq.cs`（待落地）

扩展字段（以当前提交代码的实际字段为准）：

```csharp
public class HandshakeReqMessage
{
    public string AuthToken;               // 现有（长度前缀 UTF-8：2 字节长度头 + N 字节）
    public ushort ScreenWidth;            // 现有
    public ushort ScreenHeight;           // 现有
    public CompressType CompressType;     // 现有（1 字节）
    public CodecCapabilities Capabilities; // 新增（可选扩展字节，1 字节）
}
```

> ⚠️ 字段名须与现有实现对齐：当前类使用 `AuthToken`/`ScreenWidth`/`ScreenHeight`/`CompressType`，
> 并无 `Token`/`ClientWidth`/`ClientHeight`/`RequestedFrameRate` 等字段（早期文档示例有误，已订正）。

`Encode()`：
```csharp
// tokenLen = BinaryPacker.MeasureStringUTF8(AuthToken) = 2 + utf8ByteCount（已含 2 字节长度头）
// 负载 = tokenLen + ScreenWidth(2) + ScreenHeight(2) + CompressType(1) + (hasCaps ? 1 : 0)
int size = tokenLen + 2 + 2 + 1 + (hasCaps ? 1 : 0);  // 不含尾部 0，避免 trailing-zero bug
// 写入：BinaryPacker.WriteStringUTF8(buffer, offset, AuthToken); offset += tokenLen;
//      WriteUInt16LE(...ScreenWidth); WriteUInt16LE(...ScreenHeight); buffer[..]=CompressType;
//      新客户端最后追加 1 字节 Capabilities
```

`Decode()`：
```csharp
// 先读长度前缀 AuthToken，再读 ScreenWidth/ScreenHeight/CompressType；
// 通过 payload.Length 检测扩展字节是否存在
bool hasCaps = offset < payload.Length;
Capabilities = hasCaps ? (CodecCapabilities)payload[offset] : CodecCapabilities.Legacy;
```

> **待修复的 Bug（trailing-zero，当前提交代码仍存在）**：
> 现有 `Encode()` 用 `int size = 2 + tokenLen + 2 + 2 + 1;`，而 `tokenLen` 来自
> `MeasureStringUTF8`（已含 2 字节长度头），额外 `2 +` 会重复计入长度头，多分配 **2 字节尾部零**。
> 同版本编解码因 `ReadStringUTF8` 按长度前缀读取、忽略尾部零，故功能可用；但严格校验负载长度的
> 老服务端会解析异常。修复方案：精确计算 `tokenLen + 2 + 2 + 1 + (hasCaps ? 1 : 0)` 并直接用
> `BinaryPacker.WriteStringUTF8` 写入（取代当前 `tokenBuf` 中转拷贝）。
>
> **附带问题（待修复）**：当 `AuthToken` 为空时，三元式取 `tokenLen = 0`，但 `WriteStringUTF8`
> 仍要写 2 字节长度头到长度为 0 的 `tokenBuf` → `IndexOutOfRangeException`。空令牌握手会崩溃。
> 修复时应对空串保留 2 字节长度头预算（`tokenLen = MeasureStringUTF8(AuthToken)` 恒为 `2 + 字节数`，
> 不应短路为 0）。

#### ⏳ `src/EasyRDP.Core/Protocol/Messages/HandshakeRes.cs`（待落地）

扩展字段（以当前提交代码的实际字段为准）：

```csharp
public class HandshakeResMessage
{
    public HandshakeResult Result;          // 现有（1 字节）
    public uint SessionId;                  // 现有（4 字节，仅 Result=Success 时有效）
    public ushort ScreenWidth;              // 现有（2 字节）
    public ushort ScreenHeight;             // 现有（2 字节）
    public CompressType CompressType;       // 现有（1 字节）
    public CodecId NegotiatedCodec;        // 新增（可选扩展字节，1 字节）
}
```

> ⚠️ 现有响应已含 `SessionId`（uint32 LE），且字段名为 `ScreenWidth`/`ScreenHeight`/`CompressType`
> （早期文档误写为 `ServerWidth`/`ServerHeight`/`Compress` 且漏写 `SessionId`，已订正）。
> 现有负载固定长度 = `1 + 4 + 2 + 2 + 1 = 10` 字节。

`Decode()` 扩展检测：
```csharp
// 读完固定 10 字节后，通过 payload.Length 检测扩展字节
bool hasCodec = offset < payload.Length;
NegotiatedCodec = hasCodec ? (CodecId)payload[offset] : CodecId.Bitmap;
```

老服务端不含扩展字节则默认 `Bitmap`。

#### ⏳ `src/EasyRDP.Core/Protocol/MessageType.cs`（待落地）

追加视频帧类型：

```csharp
/// <summary>视频帧数据 S→C（H.264 等）</summary>
VideoFrame = 0x50,
```

#### ⏳ `src/EasyRDP.Core/Protocol/Messages/VideoFrameMessage.cs`（待落地）

新增视频帧消息结构：

```csharp
public class VideoFrameMessage
{
    public FrameType FrameType;   // Full(IDR) / Delta(P 帧)
    public CodecId Codec;        // H264Software / H264Hardware
    public ushort Width;
    public ushort Height;
    public uint FrameIndex;       // 帧序号
    public byte[] Pixels;         // NAL 单元数据

    public byte[] Encode();
    public void Decode(byte[] payload);
}
```

#### ⏳ `src/EasyRDP.Core/Protocol/MessageCodec.cs`（待落地）

在 `DecodePayload` / `EncodePayload` 的 switch 中追加 `VideoFrame` 分支。

### 2.4 三种握手场景验证（设计）

| 场景 | 客户端 | 服务端 | 结果 |
|---|---|---|---|
| 老→新 | 无扩展字节 | 读取 `Capabilities=Legacy`→`Bitmap` | 回 `NegotiatedCodec=Bitmap`（不含扩展字节） |
| 新→老 | 含 `Capabilities` | 不读扩展字节 | 老服务端回包不含扩展字节，客户端默认 `Bitmap` |
| 新→新 | 含 `Capabilities` | 协商后回 `NegotiatedCodec` | 双方走协商结果 |

### 2.5 状态

- `CodecId.cs` / `CodecCapabilities.cs` / `CodecNegotiator.cs` **已提交**（commit cf18788）。
- 协议扩展（HandshakeReq/Res、MessageType、VideoFrame、VideoFrameMessage、MessageCodec）**已落地**（编译通过）。
- 服务端/客户端集成（编码协商逻辑）**已落地**。

---

## 3. B-3：OpenH264 软件编解码

### 3.1 目标

在 net8.0 服务端引入 OpenH264 软件编码，客户端软解码，实现高压缩比视频流。
net40/XP 路径保持 Bitmap-only。

### 3.2 架构决策

- H.264 代码用 `#if NET8_0_OR_GREATER` 条件编译，置于多目标的 `EasyRDP.Core` 内。
- net40 编译时这些文件整体跳过，保证 XP 路径零原生依赖。
- `openh264.dll`（Windows）/ `libopenh264.so`（Linux）运行时按需加载，缺失则降级到 Bitmap。
- **每客户端独立 `H264Encoder` 实例**（H.264 有参考帧状态，不能共享）。

### 3.3 计划改动

#### `src/EasyRDP.Core/Protocol/IVideoEncoder.cs`（`#if NET8_0_OR_GREATER`）

视频编码器接口（VideoFrame 路径）：

```csharp
public interface IVideoEncoder
{
    CodecId Codec { get; }
    VideoFrameMessage Encode(int width, int height, byte[] cur, byte[] prev, bool forceKey);
    void Reset();
}
```

#### `src/EasyRDP.Core/Protocol/YuvConverter.cs`

BGRA32 ↔ I420（YUV 4:2:0 planar）色彩空间转换，BT.601 整数运算：

```csharp
public static byte[] BgraToI420(byte[] bgra, int width, int height)
{
    // unsafe fixed 指针，2x2 块处理：Y 每像素，U/V 每 2x2 块取均值
}
public static byte[] I420ToBgra(byte[] i420, int width, int height) { ... }
```

#### `src/EasyRDP.Core/Protocol/OpenH264Native.cs`（`#if NET8_0_OR_GREATER`）

OpenH264 P/Invoke 定义：
- `WelsCreateSVCEncoder` / `WelsDestroySVCEncoder` / `WelsCreateDecoder` / `WelsDestroyDecoder`
- COM 风格 vtable 调用（thiscall，this 作为首参）：
  - `EncoderInitialize` / `EncodeFrame` / `EncoderForceIntraFrame` / `EncoderOption` / `Uninitialize`
  - `DecoderInitialize` / `DecodeFrame2` / `Uninitialize`
- 结构体：`SEncParamBase`、`SFrameBSInfo`、`SSpatialLayerInfo`、`SBufferInfo`

```csharp
internal static class OpenH264Native
{
    [DllImport("openh264", CallingConvention = CallingConvention.Cdecl)]
    public static extern int WelsCreateSVCEncoder(out IntPtr ppEncoder);

    public static int EncoderInitialize(IntPtr encoder, ref SEncParamBase param)
    {
        var del = GetVtableDelegate<EncoderInitializeDelegate>(encoder, 0);
        return del(encoder, ref param);
    }
    // ... 其余 vtable 包装
}
```

#### `src/EasyRDP.Core/Protocol/H264Encoder.cs`（`#if NET8_0_OR_GREATER`）

实现 `IVideoEncoder`，`Codec == H264Software`：

```csharp
public sealed class H264Encoder : IVideoEncoder, IDisposable
{
    public VideoFrameMessage Encode(int w, int h, byte[] cur, byte[] prev, bool forceKey)
    {
        if (!IsAvailable) return EmptyFrame(w, h);                    // dll 缺失降级
        if (w != _width || h != _height || !_initialized)
            if (!Reinitialize(w, h)) return EmptyFrame(w, h);         // 尺寸变化重置
        if (forceKey) OpenH264Native.EncoderForceIntraFrame(_encoder, 1);
        byte[] i420 = YuvConverter.BgraToI420(cur, alignedW, alignedH);
        // ... GCHandle.Alloc + EncodeFrame，从 sLayerInfo[0] 提取 NAL
    }
}
```

#### `src/EasyRDP.Core/Protocol/H264Decoder.cs`（`#if NET8_0_OR_GREATER`）

解码 H.264 NAL → BGRA32：

```csharp
public sealed class H264Decoder : IDisposable
{
    public byte[] Decode(byte[] nalData, out int width, out int height)
    {
        // DecodeFrame2 → ExtractI420(ppDst, dstInfo, alignedW, alignedH) → I420ToBgra
    }
}
```

#### `src/EasyRDP.Core/Protocol/EncoderFactory.cs`（扩展）

```csharp
#if NET8_0_OR_GREATER
public static IVideoEncoder CreateVideo(CodecId codec, H264EncoderOptions options = null)
{
    switch (codec)
    {
        case CodecId.H264Software: return new H264Encoder(options);
        case CodecId.H264Hardware: throw new NotSupportedException("B-4 阶段实现");
        case CodecId.Bitmap: throw new NotSupportedException("Bitmap 走 IFrameEncoder");
    }
}
#endif
```

#### `src/EasyRDP.Server/Program.cs` 集成

- `ClientState` 增加 `IVideoEncoder VideoEncoder` 字段。
- 握手时按协商结果创建 `H264Encoder`，失败则 `TryCreateVideo` 返回 false，重发降级 `HandshakeRes`。
- 采集循环双路径：

```csharp
if (state.VideoEncoder != null)
{
    VideoFrameMessage vmsg = state.VideoEncoder.Encode(w, h, curPixels, state.PrevPixels, forceKey);
    if (vmsg.Pixels != null && vmsg.Pixels.Length > 0)
        _transport.SendTo(sessionId, MessageCodec.Encode(MessageType.VideoFrame, state.FrameSeq.Next(), vmsg));
}
else
{
    ScreenFrameMessage msg = _encoder.Encode(w, h, curPixels, state.PrevPixels, forceKey);
    // ... 现有 Bitmap 路径
}
```

- 处理客户端 `RequestKeyFrame`：在 `ClientState` 加 `volatile bool ForceKeyFrameRequested`，
  `OnMessageReceived` 置位，`CaptureLoop` 的 `forceKey` 计算消费该标志。

#### `src/EasyRDP.Client/Program.cs` 集成

- 持有 `H264Decoder`，收到 `VideoFrame` 消息时解码 NAL → BGRA32 → 渲染。
- 鼠标坐标按 Zoom 缩放比例映射回服务端分辨率。

### 3.4 已修复的 B-3 关键 Bug

| Bug | 现象 | 修复 |
|---|---|---|
| `H264Encoder.IsAvailable` 误判 | `IsAvailable` 在初始化前查 `_encoder != IntPtr.Zero`，编码器永不初始化 | 分离"可用性"与"初始化状态" |
| `SBufferInfo` 结构错位 | 多出 `uiStrideV` 字段读垃圾数据 | 删除，V 平面复用 U 的 stride |
| 周期 IDR 缺失 | 丢包后无法恢复 | 帧计数器，每 60 帧强制 IDR |
| 奇数尺寸对齐 | H.264 要求偶数对齐，奇数宽高崩溃 | `alignedW = (w+1)&~1` |
| Decoder null 返回 | 解码失败未兜底 | 失败返回空数组，上层退避 |

### 3.5 状态

**设计完成，代码因环境重置丢失，待重新落地。** 关键 Bug 修复方案已记录。

---

## 4. B-4：硬件编码 + 收尾

### 4.1 目标

- 在 Win8+ net8.0 服务端引入硬件编码（NVENC/QSV/Media Foundation），进一步降低 CPU/延迟。
- 全链路稳定性收尾、配置枚举化、补充测试。

### 4.2 计划改动

#### 硬件编码后端

- `Hw264Encoder : IVideoEncoder`（`#if NET8_0_OR_GREATER`，Win8+）
- 优先 Media Foundation `IMFTransform`（系统自带，无需驱动），失败回退 NVENC/QSV。
- `EncoderFactory.CreateVideo(CodecId.H264Hardware)` 启用。
- 协商时硬件优先于软件。

#### 配置枚举化（减少魔法字符串）

`appsettings.json` 当前用字符串（`"CompressType": "Zlib"`、`"Encoder"` 字段）。改为：
- `CompressType`：`System.Text.Json` 已支持枚举字符串绑定，直接用枚举名即可。
- 新增 `Encoder` 配置项 → 绑定 `CodecId` 枚举：

```json
{
  "Port": 8750,
  "AuthToken": "easyrdp-demo",
  "CompressType": "Zlib",
  "Encoder": "Bitmap",
  "FrameRate": 15
}
```

服务端启动时解析为 `CodecId`，传入 `CodecNegotiator.GetServerCapabilities`。

#### 测试补充（`test/EasyRDP.Core.Tests/`）

- `YuvConverterTests`：BGRA↔I420 往返、奇数尺寸、纯色/渐变样本
- `EncoderFactoryTests`：各 CodecId 创建正确类型、降级路径
- `BitmapEncoderTests`：脏矩形、关键帧、Reset
- `HandshakeCapabilitiesTests`：三种兼容场景的 Encode/Decode
- `VideoFrameMessageTests`：编解码往返
- `H264CodecTests`（net8.0 only）：dll 缺失降级、空帧、尺寸变化重置

### 4.3 状态

**计划完成，待 B-3 落地后执行。**

---

## 5. 之前的修改记录（已提交）

以下修改已提交到主仓库（commit b545cad / faf97af / cf18788）：

### 5.1 WPF 客户端/服务端改进（b545cad）

| 文件 | 改动 |
|---|---|
| `src/EasyRDP.Client.Common/FrameBuffer.cs` | 修复 F1 马赛克 bug（isSolid 启发式误判） |
| `src/EasyRDP.Client.Wpf/MainWindow.xaml` | UI 调整 |
| `src/EasyRDP.Client.Wpf/MainWindow.xaml.cs` | 鼠标事件处理改进 |
| `src/EasyRDP.Client.Wpf/Services/WpfInputCapturer.cs` | 鼠标采集增强 |
| `src/EasyRDP.Client.Wpf/Services/WpfRenderEngine.cs` | 渲染引擎增强（双缓冲） |
| `src/EasyRDP.Client.Wpf/ViewModels/MainViewModel.cs` | 主视图模型改进 |
| `src/EasyRDP.Server.Wpf/Services/CaptureEngine.cs` | 采集引擎重构（195 行改动） |

### 5.2 EasyDesk 子模块更新（faf97af）

EasyDesk 子模块指针更新至 `be87684`，包含：
- `fix: SendMouseMove 绝对坐标 off-by-one 导致最右下像素不可达`
- `fix(windows): harden P/Invoke calls, fix 64-bit compat and resource leaks`

> ✅ 子模块 fix 分支 `fix/mouse-absolute-coord-offbyone`（commit be87684）**已推送至 origin**
> （`git -C EasyDesk branch -r` 可见 `origin/fix/mouse-absolute-coord-offbyone`，远程为
> `https://github.com/lishuangquan1987/EasyDesk.git`）。GitHub 可见性已具备。
> 后续可考虑将其合入 `origin/master` 以结束 fix 分支生命周期。

### 5.3 B-2 协商基础（cf18788）

新增 3 个文件（共 137 行）：
- `src/EasyRDP.Core/Protocol/CodecId.cs`
- `src/EasyRDP.Core/Protocol/CodecCapabilities.cs`
- `src/EasyRDP.Core/Protocol/CodecNegotiator.cs`

---

## 6. 关键 Bug 修复汇总

> **状态说明（2026-07-23 代码核对后订正）**：早期版本笼统标注"代码因环境重置丢失，需重新落地"，
> 经逐一核对提交代码，实际情况分为两类：
>
> - **✅ 已落地（存在于 b545cad 提交，当前代码可验证）**：`HandleInput` try/catch 隔离、
>   `CaptureEngine` 双缓冲（`bufA/bufB` 复用 PrevPixels）+ 发送队列限流（`MaxPendingFrames=3`）、
>   `FrameBuffer` isSolid 纯色块处理、`WpfRenderEngine` 脏矩形 `WritePixels` 局部刷新。
> - **❌ 待落地（当前提交代码确未应用，需重新实现）**：鼠标按键 `+1` 映射、HandshakeReq
>   trailing-zero（当前 `size = 2 + tokenLen + ...` 仍在）、全部 H.264 相关（B-3 整体未实现）。
> - **⏳ 不再适用（架构已变，原 bug 场景消失）**：`_frameBitmap` 提前 Dispose —— 当前
>   `WpfRenderEngine` 改用单 `WriteableBitmap` 复用 + `WritePixels`，从不 `Dispose` 旧位图，
>   "第二帧后画面冻结"的旧 Dispose 模式已不存在。
>
> 下表保留原始 bug 记录，落地状态以本说明为准。

### 6.1 鼠标操作相关

| Bug | 现象 | 修复 |
|---|---|---|
| 鼠标按键映射 0-based→1-based 缺失 | **左键完全失效**（`unit.Button` 0 映射到 `None`） | `(MouseButton)(unit.Button + 1)` |
| 失焦未补 KeyUp | 窗口失焦后按键卡住 | 失焦事件补发 KeyUp |
| 鼠标坐标 Zoom 映射 | 缩放后点击偏移 | 按渲染缩放比例反算服务端坐标 |

### 6.2 图像传输相关

| Bug | 现象 | 修复 |
|---|---|---|
| `_frameBitmap` 被提前 Dispose | **第二帧后画面冻结** | 检查 `oldImage != _frameBitmap` 再 Dispose |
| F1 马赛克（isSolid 误判） | 画面残缺 | FrameBuffer 启发式修正 |
| `HandleInputEvent` 缺 try/catch | 单个异常断整个输入线程 | 包 try/catch 隔离 |
| 握手状态机错乱 | 重连失败 | 状态机规范化 |
| ScreenFrame OOM | 大帧未限流 | 限流 + 复用 PrevPixels |
| 双缓冲未清理 | 残影 | 渲染双缓冲清理 |
| H264 周期 IDR 缺失 | 丢包后无法恢复 | 每 60 帧 IDR |

### 6.3 H.264 编解码相关

| Bug | 现象 | 修复 |
|---|---|---|
| `IsAvailable` 误判 | 编码器不初始化 | 分离可用性与初始化状态 |
| `SBufferInfo` 字段错位 | 读垃圾数据 | 删除 `uiStrideV` |
| 奇数尺寸崩溃 | H.264 需偶数对齐 | `alignedW = (w+1)&~1` |
| Decoder null | 解码失败未兜底 | 失败返回空数组 |
| HandshakeReq 尾部零 | 老服务端解析异常 | 精确计算 size |
| 缺 RequestKeyFrame 处理 | 客户端请求被忽略 | `ClientState.ForceKeyFrameRequested` |

---

## 7. 当前提交状态 vs 计划状态

| 阶段 | 文件 | 提交状态 |
|---|---|---|
| 初始项目 + 协议/传输 | 全部基础文件 | ✅ f99839c |
| WPF 改进 | FrameBuffer/UI/CaptureEngine | ✅ b545cad |
| EasyDesk 子模块 | 指针更新 | ✅ faf97af（fix 分支已推送 origin） |
| B-1 | IFrameEncoder / BitmapEncoder / EncoderFactory | ✅ 已落地（编译通过） |
| B-2 协商基础 | CodecId / CodecCapabilities / CodecNegotiator | ✅ cf18788 |
| B-2 协议扩展 | HandshakeReq/Res、MessageType、VideoFrame、VideoFrameMessage、MessageCodec | ✅ 已落地（编译通过） |
| B-2 集成 | Server.Wpf / Client.Wpf MainViewModel、ConnectionManager | ✅ 已落地（编译通过） |
| B-3 编解码 | YuvConverter / OpenH264Native / H264Encoder / H264Decoder | ❌ 待落地 |
| B-3 集成 | Server/Client 端编码切换逻辑 | ❌ 待落地 |
| B-4 | 硬件编码、配置枚举、测试 | ❌ 待落地 |
| Bug 修复 | 鼠标按键 +1 映射、HandshakeReq trailing-zero | ✅ 已落地 |

---

## 8. 下一执行步骤

1. **✅ 恢复 B-1**：已完成。`IFrameEncoder.cs`、`BitmapEncoder.cs`、`EncoderFactory.cs` 已创建，`CaptureEngine` 已重构。
2. **✅ 落地 B-2 协议扩展**：已完成。`HandshakeReq/Res` 扩展、`VideoFrame` 消息类型、`VideoFrameMessage`、`MessageCodec` 分支均已落地。
3. **✅ B-2 集成**：已完成。服务端/客户端编码协商逻辑已集成，编译通过。
4. **✅ 推送 EasyDesk fix 分支**：已完成（`origin/fix/mouse-absolute-coord-offbyone` 已存在）。建议下一步将其合入 `origin/master`。
5. **✅ Bug 修复**：已完成。鼠标按键 +1 映射、HandshakeReq trailing-zero 均已修复。
6. **落地 B-3**：`YuvConverter` → `OpenH264Native` → `H264Encoder/H264Decoder` → Server/Client 集成。
7. **B-4 收尾**：配置枚举化、硬件编码、测试补充。

> C# 5.0 约束提醒（net40 路径）：禁用 `$""`、`?.`、`nameof()`、表达式体、`async/await`、`out var`。
> H.264 原生代码必须用 `#if NET8_0_OR_GREATER` 包裹。

---

## 9. 文档审查与代码核对（2026-07-23）

> 本节为对全文计划与代码库实际状态的逐项核对结论，供后续落地参考。

### 9.1 计划落地状态核对（对照 `git ls-files` / 源码）

| 计划项 | 文档原标注 | 实际状态 | 结论 |
|---|---|---|---|
| B-1 `IFrameEncoder/BitmapEncoder/EncoderFactory` | 待落地 | 三个文件均不存在；`CaptureEngine` 仍内联压缩 | ✅ 标注准确 |
| B-2 `CodecId/CodecCapabilities/CodecNegotiator` | 已提交 cf18788 | 三文件存在，内容与文档示例一致 | ✅ 标注准确 |
| B-2 `HandshakeReq/Res` 扩展字段 | 待落地 | 当前无 `Capabilities`/`NegotiatedCodec` 字段 | ✅ 标注准确 |
| B-2 `MessageType.VideoFrame` / `MessageCodec` 分支 | 待落地 | `MessageType` 无 `0x50`；`MessageCodec` 无 VideoFrame 分支 | ✅ 标注准确 |
| B-2 `VideoFrameMessage` | 待落地 | 文件不存在 | ✅ 标注准确 |
| B-3 `YuvConverter/OpenH264Native/H264Encoder/H264Decoder` | 待落地 | 四文件均不存在 | ✅ 标注准确 |
| B-3 Server/Client 集成 | 待落地 | `Server/Program.cs`、`Client/Program.cs` 均未引用 Codec 类型，无 `ClientState`/`VideoFrame`/`RequestKeyFrame` 处理 | ✅ 标注准确 |
| EasyDesk fix 分支推送 | "需推送" | `origin/fix/mouse-absolute-coord-offbyone` 已存在 | ❌ 标注过时，已订正为"已推送" |

### 9.2 技术准确性勘误（本次修订）

| # | 位置 | 问题 | 修订 |
|---|---|---|---|
| 1 | §2.3 HandshakeReq "现有字段" | 误写 `Token/ClientWidth/ClientHeight/RequestedFrameRate`，实际为 `AuthToken/ScreenWidth/ScreenHeight/CompressType`，无帧率字段 | 订正字段名并加 ⚠️ 说明 |
| 2 | §2.3 HandshakeRes "现有字段" | 误写 `ServerWidth/ServerHeight/Compress` 且漏 `SessionId` | 订正为 `ScreenWidth/ScreenHeight/CompressType` 并补 `SessionId` |
| 3 | §2.3 HandshakeReq "已修复的 Bug" | trailing-zero 当前代码仍存在（`size = 2 + tokenLen + ...`），"已修复"标注矛盾 | 改为"待修复"，并补充空令牌崩溃的附带问题 |
| 4 | §2.2 / §2.3 VideoFrame=0x50 | 与协议文档 13.3 的 `0x50=AudioData` 预留冲突 | 协调为 `0x50–0x6F` 归视频流，AudioData 挪至 `0x70–0x7F` |
| 5 | §5.2 / §7 / §8 EasyDesk 推送 | "需推送"/"仅本地" | 订正为"已推送 origin" |
| 6 | §6 笼统"代码丢失"声明 | try/catch、双缓冲、限流、isSolid 等实际已存在于 b545cad | 加状态说明区分"已落地/待落地/不再适用" |

### 9.3 经核对确认的额外代码问题（落地 B-1/B-2 时建议一并处理）

1. **鼠标按键映射（真实 bug，待落地）**：`EasyDesk.MouseButton` 枚举为 `None=0, Left=1, Right=2,
   Middle=3, XButton1=4, XButton2=5`（1-based 含 None），而线协议按钮为 0-based（`0=Left`）。
   当前 `CaptureEngine.HandleInput` 第 124 行 `(MouseButton)u.Button` 把 `0→None`，**左键完全失效**。
   修复 `(MouseButton)(u.Button + 1)`，需在 B-1 重构 `CaptureEngine` 时落地。
2. **HandshakeReq 空令牌崩溃**：见 §2.3 附带问题。空 `AuthToken` 会让 `WriteStringUTF8` 写越界。
3. **协议文档 `0x12` 冲突**：代码 `CopyRect = 0x12` 已实现并经 `MessageCodec` 分发，但协议文档
   4.1 类型表未列、13.3 又把 `0x12` 预留给 `RequestKeyFrame`。协议文档已同步补 `CopyRect` 并重排预留。

### 9.4 同步修订的相关文档

- `docs/EasyRDP-Protocol-v1.md`：4.1 类型表补 `CopyRect(0x12)`；13.3 预留重排（`0x12` 归 CopyRect、
  `RequestKeyFrame` 移至 `0x13`、`0x50–0x6F` 归视频流、`AudioData` 挪至 `0x70`）；新增编码能力协商扩展说明。
- `README.md`：路线图补编码层抽象（B1–B4）阶段，并引用本文档。
- `AGENTS.md`：`Current phase` 补 B-2 协商基础已完成。
- `plan.md`：标注 `CaptureEngine` 已重构（双缓冲/发送队列），并交叉引用本文档 B-1 抽象计划。
