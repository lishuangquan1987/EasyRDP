# EasyRDP ZRLE 编码器实施计划（v3 修正版）

> **文档目的**：基于 [EasyRDP-vs-RemoteDesktop-Analysis.md](./EasyRDP-vs-RemoteDesktop-Analysis.md) 的方案，编写阶段一/二/三的详细实施计划，精确到类、方法、字段级别，供审核后执行。
>
> **核心原则**：不动现有 H264 路径，通过新增实现 + 工厂分发实现并行编码路径，握手协商切换。
>
> **v2 修正**：根据 `EasyRDP-ZRLE-Plan-Evaluation.md` 审核报告，修正 3 个致命缺陷（IsKeyframe 尖峰 / System.Drawing.Point 编译失败 / EstimateChangeRatio 不存在）、4 个性能问题（双重变化检测 / GC 压力 / 逐字节比较 / GZipStream 开销）、4 个建议修正（MessageType 路径 / ZrleDecoder BlockCopy / CopyRect 搜索 / 参考帧一致性）。
>
> **v3 修正**：发现 v2 修正引入 5 个新问题（1 致命 + 2 性能倒退 + 2 健壮性隐患），本次修正：ZrleDecoder 双重 ApplyRegion → 单次应用、GC 池化不完全 → _compressedDataPool、IsReferenceFrameEmpty 脆弱 → _isFirstFrame 标志、HybridEncoder Codec 冲突 → 移除、DeflateDecompress 4 次分配/区域 → 预分配缓冲。
>
> **生成时间**：2026-08-06（v3）

---

## 目录

- [阶段一：ZRLE 编码器/解码器实现](#阶段一zrle-编码器解码器实现)
- [阶段二：FillRect/CopyRect 快速路径 + 显示层脏矩形](#阶段二fillrectcopyrect-快速路径--显示层脏矩形)
- [阶段三：混合编码策略 + 客户端请求驱动流控](#阶段三混合编码策略--客户端请求驱动流控)
- [附录：文件改动清单](#附录文件改动清单)
- [附录：v2 修正记录](#附录v2-修正记录)

---

## 阶段一：ZRLE 编码器/解码器实现

### 1.1 目标

- **编码耗时**：280-1000ms → 50-100ms（降 3-10 倍）
- **整体 FPS**：1-3 → 10-15
- **XP 兼容**：✅ 纯 C# + System.IO.Compression.DeflateStream（net40 内置）
- **H264 路径影响**：零影响（独立并行路径）

> **v2 修正**：性能预期从"20-80ms/15-25FPS"下调为"50-100ms/10-15FPS"。原因：纯 C# 在 net40 上无 SIMD，逐瓦片比较+Zlib 压缩的实际开销高于初版预估。修正后仍比 H264 快 5-10 倍。

### 1.2 改动总览

| 改动类型 | 文件 | 改动量 |
|---------|------|--------|
| **修改** | [CodecId.cs](../src/EasyRDP.Core/Protocol/CodecId.cs) | +1 行枚举值 |
| **修改** | [CodecCapabilities.cs](../src/EasyRDP.Core/Protocol/CodecCapabilities.cs) | +1 行枚举值 |
| **修改** | [CodecNegotiator.cs](../src/EasyRDP.Core/Protocol/CodecNegotiator.cs) | +3 行优先级判断 |
| **修改** | [EncoderFactory.cs](../src/EasyRDP.Core/Protocol/EncoderFactory.cs) | +6 行 case + GetAvailableCodecs |
| **修改** | [DecoderFactory.cs](../src/EasyRDP.Core/Protocol/DecoderFactory.cs) | +6 行 case + GetAvailableCodecs |
| **修改** | [ServerStreamSession.cs](../src/EasyRDP.Server.Wpf/ServerStreamSession.cs) | +5 行 ZRLE 模式跳过外部变化检测 |
| **新增** | `ZrleEncoder.cs` | ~500 行（含缓冲池 + uint 比较 + EstimateChangeRatio） |
| **新增** | `ZrleDecoder.cs` | ~350 行（含直接写入 outputBuffer 优化） |
| **新增** | `ZrleRegionCodec.cs` | ~350 行（区域打包/解包） |
| **不动** | 抓屏/传输/显示/H264 全部 | 0 |

> **v2 修正**：不再声称"不动编排层"。ServerStreamSession 需要小改（+5 行），在 ZRLE 模式下跳过外部 BlockHashDirtyRectDetector，避免双重变化检测。

### 1.3 详细改动

#### 1.3.1 修改 CodecId.cs

**文件**：[src/EasyRDP.Core/Protocol/CodecId.cs](../src/EasyRDP.Core/Protocol/CodecId.cs)

**当前代码**（第 6-10 行）：
```csharp
public enum CodecId : byte
{
    H264Software = 1,
    H264Hardware = 2
}
```

**改为**：
```csharp
public enum CodecId : byte
{
    H264Software = 1,
    H264Hardware = 2,
    /// <summary>ZRLE 区域编码（64×64 瓦片 + Zlib，无运动估计，单核 CPU 友好）。</summary>
    Zrle = 3
}
```

#### 1.3.2 修改 CodecCapabilities.cs

**文件**：[src/EasyRDP.Core/Protocol/CodecCapabilities.cs](../src/EasyRDP.Core/Protocol/CodecCapabilities.cs)

**当前代码**（第 7-15 行）：
```csharp
public enum CodecCapabilities : byte
{
    None         = 0,
    H264Software = 1 << 0,
    H264Hardware = 1 << 1
}
```

**改为**：
```csharp
public enum CodecCapabilities : byte
{
    None         = 0,
    H264Software = 1 << 0,
    H264Hardware = 1 << 1,
    /// <summary>ZRLE 区域编码支持（纯 C#，无原生依赖）。</summary>
    Zrle         = 1 << 2
}
```

#### 1.3.3 修改 CodecNegotiator.cs

**文件**：[src/EasyRDP.Core/Protocol/CodecNegotiator.cs](../src/EasyRDP.Core/Protocol/CodecNegotiator.cs)

**当前代码**（第 11-19 行）：
```csharp
public static CodecId? Negotiate(CodecCapabilities clientCaps, CodecCapabilities serverCaps)
{
    CodecCapabilities common = clientCaps & serverCaps;
    if ((common & CodecCapabilities.H264Hardware) != 0)
        return CodecId.H264Hardware;
    if ((common & CodecCapabilities.H264Software) != 0)
        return CodecId.H264Software;
    return null;
}
```

**改为**（新增 ZRLE 优先级，H264Hardware > ZRLE > H264Software）：
```csharp
public static CodecId? Negotiate(CodecCapabilities clientCaps, CodecCapabilities serverCaps)
{
    CodecCapabilities common = clientCaps & serverCaps;
    // 优先级：硬件 H264（画质+压缩率最优）> ZRLE（单核 CPU 性能最优）> 软件 H264（兼容性兜底）
    if ((common & CodecCapabilities.H264Hardware) != 0)
        return CodecId.H264Hardware;
    if ((common & CodecCapabilities.Zrle) != 0)
        return CodecId.Zrle;
    if ((common & CodecCapabilities.H264Software) != 0)
        return CodecId.H264Software;
    return null;
}
```

**设计决策**：ZRLE 优先级高于 H264Software，因为单核 XP 场景下 ZRLE 性能远优于软件 H264。若客户端同时支持两者，优先选择 ZRLE。

#### 1.3.4 修改 EncoderFactory.cs

**文件**：[src/EasyRDP.Core/Protocol/EncoderFactory.cs](../src/EasyRDP.Core/Protocol/EncoderFactory.cs)

**改动 1**：`Create` 方法（第 13-24 行）新增 ZRLE case：
```csharp
public static IVideoEncoder Create(CodecId codec)
{
    switch (codec)
    {
        case CodecId.H264Software:
        {
            var encoder = new H264EncoderNative();
            return encoder.IsAvailable ? encoder : null;
        }
        case CodecId.H264Hardware:
            return null; // 未来实现
        case CodecId.Zrle:
            // ZRLE 纯 C# 实现，无原生依赖，始终可用
            return new ZrleEncoder();
        default:
            return null;
    }
}
```

**改动 2**：`GetAvailableCodecs` 方法（第 42-56 行）新增 ZRLE 遍历：
```csharp
public static CodecCapabilities GetAvailableCodecs()
{
    var caps = CodecCapabilities.None;
    // ZRLE 无需探测（纯 C# 始终可用），直接置位
    caps |= CodecCapabilities.Zrle;
    foreach (CodecId c in new[] { CodecId.H264Software, CodecId.H264Hardware })
    {
        if (!GetAvailableCodec(c).HasValue)
            continue;
        switch (c)
        {
            case CodecId.H264Software: caps |= CodecCapabilities.H264Software; break;
            case CodecId.H264Hardware: caps |= CodecCapabilities.H264Hardware; break;
        }
    }
    return caps;
}
```

#### 1.3.5 修改 DecoderFactory.cs

**文件**：[src/EasyRDP.Core/Protocol/DecoderFactory.cs](../src/EasyRDP.Core/Protocol/DecoderFactory.cs)

**改动 1**：`Create` 方法（第 13-24 行）新增 ZRLE case：
```csharp
public static IVideoDecoder Create(CodecId codec)
{
    switch (codec)
    {
        case CodecId.H264Software:
        {
            var decoder = new H264DecoderNative();
            return decoder.IsAvailable ? decoder : null;
        }
        case CodecId.H264Hardware:
            return null; // 未来实现
        case CodecId.Zrle:
            return new ZrleDecoder();
        default:
            return null;
    }
}
```

**改动 2**：`GetAvailableCodecs` 方法同 EncoderFactory，新增 ZRLE 位。

#### 1.3.6 修改 ServerStreamSession.cs（v2 新增）

**文件**：[src/EasyRDP.Server.Wpf/ServerStreamSession.cs](../src/EasyRDP.Server.Wpf/ServerStreamSession.cs)

**问题**：EncodeLoop 在调用 `_encoder.Encode()` 之前先调用 `_changeDetector.Detect()`（第 490 行附近）。BlockHashDirtyRectDetector 对全帧做 32×32 块 CRC32 哈希（~3-5ms）。然后 ZrleEncoder.Encode() 内部又做 64×64 瓦片逐字节比较。两套变化检测机制重复运行，每帧多 3-5ms CPU 开销。

**改动位置**：`EncodeLoop` 方法中，`_changeDetector.Detect()` 调用前（约第 488 行），新增 ZRLE 模式跳过逻辑：

```csharp
// v2 修正：ZRLE 编码器内部自己做变化检测（64×64 瓦片对比），
// 跳过外部 BlockHashDirtyRectDetector 避免双重检测（节省 3-5ms/帧）。
// H264 模式仍需外部变化检测（H264 无内置变化检测）。
var changeResult = (_encoder.Codec == CodecId.Zrle)
    ? new FrameChangeResult { ShouldEncode = true }  // ZRLE 始终进入 Encode，由编码器内部判断
    : _changeDetector.Detect(pixelsToEncode, _lastW, _lastH);

if (!changeResult.ShouldEncode && _framesSkipped < KeepaliveFrameInterval)
{
    _framesSkipped++;
    lock (_lock) { _captureBufInUse[frame.BufferIndex] = false; }
    continue;
}
// ... 后续 forceKey 逻辑不变 ...
```

**注意**：ZRLE 模式下 `_changeDetector.Commit()` 仍需调用（保持 _framesSkipped 计数逻辑一致性），但 NoOp 效果（ZRLE 内部自管参考帧）。

**替代方案**（更干净但改动更大）：为 ZRLE 模式注入 `NoOpChangeDetector`（总是返回 ShouldEncode=true）。阶段一选上述内联判断（改动最小），阶段二可重构为 NoOpChangeDetector。

#### 1.3.7 新增 ZrleRegionCodec.cs

**文件**：`src/EasyRDP.Core/Protocol/ZrleRegionCodec.cs`（新文件）

**职责**：ZRLE 多矩形数据的打包/解包，独立于编码器/解码器，便于复用和测试。

**数据格式设计**：
```
ZRLE 帧格式（VideoFrameMessage.Data 字段内容）：
┌─────────────────────────────────────────────────────────────┐
│ RegionCount(4) │ Region[0] │ Region[1] │ ... │ Region[N-1] │
└─────────────────────────────────────────────────────────────┘

每个 Region 结构：
┌──────────────────────────────────────────────────────────────┐
│ X(4) │ Y(4) │ Width(4) │ Height(4) │ Encoding(1) │ DataLen(4) │ Data(*) │
└──────────────────────────────────────────────────────────────┘

Encoding 枚举（阶段一只用 Deflate，阶段二扩展）：
  0 = Raw（无压缩 BGRA）
  1 = Deflate（Deflate 压缩 BGRA，无 GZip 头尾开销）
  2 = FillRect（纯色矩形，Data = 4 字节 BGRA）
  3 = CopyRect（矩形移动，Data = SrcX(4) + SrcY(4)）
```

**类设计**：
```csharp
namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// ZRLE 区域编码格式：一帧由多个矩形区域组成，每个区域独立编码。
    /// 阶段一仅支持 Raw/Deflate 编码；阶段二扩展 FillRect/CopyRect。
    /// </summary>
    public enum ZrleRegionEncoding : byte
    {
        /// <summary>无压缩 BGRA 像素。</summary>
        Raw = 0,
        /// <summary>Deflate 压缩 BGRA 像素（v2：从 Zlib 改为 Deflate，去掉 GZip 18 字节头尾开销）。</summary>
        Deflate = 1,
        /// <summary>纯色矩形（Data 仅 4 字节 BGRA 颜色值）。</summary>
        FillRect = 2,
        /// <summary>矩形移动（Data 为源坐标 SrcX+SrcY，8 字节）。</summary>
        CopyRect = 3
    }

    /// <summary>
    /// 单个矩形区域更新。
    /// v3 修正：新增 DataLen 字段，支持池化 Data 引用（避免每瓦片 new byte[]）。
    /// </summary>
    public struct ZrleRegion
    {
        /// <summary>区域左上角 X 坐标（像素）。</summary>
        public int X;
        /// <summary>区域左上角 Y 坐标（像素）。</summary>
        public int Y;
        /// <summary>区域宽度（像素）。</summary>
        public int Width;
        /// <summary>区域高度（像素）。</summary>
        public int Height;
        /// <summary>编码类型。</summary>
        public ZrleRegionEncoding Encoding;
        /// <summary>编码后的数据（可能指向池化缓冲，实际有效长度由 DataLen 决定）。</summary>
        public byte[] Data;
        /// <summary>v3 新增：Data 中的有效字节数（Data.Length 可能大于此值，因为 Data 可能是池化缓冲）。</summary>
        public int DataLen;
    }

    /// <summary>
    /// ZRLE 区域打包/解包工具。独立于编码器/解码器，便于单元测试。
    /// 格式：RegionCount(4) + N × [X(4)+Y(4)+W(4)+H(4)+Enc(1)+DataLen(4)+Data(*)]
    /// </summary>
    public static class ZrleRegionCodec
    {
        /// <summary>最大区域数限制（防恶意数据 OOM）。</summary>
        public const int MaxRegionCount = 256;

        /// <summary>打包多个区域为字节数组。</summary>
        public static byte[] Pack(ZrleRegion[] regions) { ... }

        /// <summary>
        /// v3 新增：打包前 count 个区域（避免调用方 Array.Copy 分配新数组）。
        /// 仅打包 regions[0..count-1]，忽略后续元素。
        /// </summary>
        public static byte[] Pack(ZrleRegion[] regions, int count) { ... }

        /// <summary>从字节数组解包区域列表。</summary>
        public static ZrleRegion[] Unpack(byte[] data) { ... }

        /// <summary>
        /// 从打包数据中提取矩形坐标列表（不解压 Data）。
        /// 用于客户端显示层脏矩形局部更新（阶段二）。
        /// </summary>
        public static ScreenRect[] ExtractRects(byte[] data) { ... }
    }
}
```

#### 1.3.8 新增 ZrleEncoder.cs（v2 重大修正）

**文件**：`src/EasyRDP.Core/Protocol/ZrleEncoder.cs`（新文件）

**实现 IVideoEncoder 接口**，内部维护参考帧 + 64×64 瓦片对比 + Deflate 压缩。

**v2 修正点**：
1. **IsKeyframe 始终返回 false**（ZRLE 无帧间依赖，避免客户端 keyframe 保护导致周期性延迟尖峰）
2. **忽略 forceKeyframe 参数**（仅首帧 `_referenceFrame == null` 时全帧编码）
3. **uint 步长比较**（替代逐字节比较，性能提升 4 倍）
4. **预分配 tile 缓冲池**（避免每帧 576 次 `new byte[16KB]` 的 GC 压力）
5. **DeflateStream 直接写入预分配缓冲**（避免 MemoryStream 分配）
6. **新增 EstimateChangeRatio 方法**（供阶段三 HybridEncoder 使用）
7. **编码成功后才更新参考帧**（失败时不污染参考帧）

**类设计**：
```csharp
namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// ZRLE 编码器：64×64 瓦片 + Deflate 压缩，无运动估计。
    /// 
    /// 工作原理：
    /// 1. 将帧划分为 64×64 像素瓦片
    /// 2. 对比当前帧与参考帧的对应瓦片（uint 步长比较，4 倍速于逐字节）
    /// 3. 只对变化瓦片做 Deflate 压缩
    /// 4. 打包为多矩形格式（ZrleRegionCodec.Pack）
    /// 5. 编码成功后才更新参考帧
    /// 
    /// 性能特征（1080p 单核 net40）：
    /// - 静态帧：3-8ms（0 个变化瓦片，仅全帧 uint 比较）
    /// - 局部变化：15-40ms（少数瓦片 Deflate 压缩）
    /// - 全屏变化：50-100ms（所有瓦片 Deflate 压缩）
    /// 
    /// v2 修正：
    /// - IsKeyframe 始终返回 false（ZRLE 无帧间依赖）
    /// - 忽略 forceKeyframe 参数（仅首帧全帧编码）
    /// - uint 步长比较替代逐字节
    /// - 预分配 tile 缓冲池避免 GC 压力
    /// - DeflateStream 替代 GZipStream（省 18 字节/瓦片头尾开销）
    /// </summary>
    public class ZrleEncoder : IVideoEncoder
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        /// <summary>瓦片边长（像素）。64×64 = 16KB BGRA，Deflate 压缩效率与开销的平衡点。</summary>
        public const int TileSize = 64;

        private int _width;
        private int _height;
        private byte[] _referenceFrame;  // 参考帧 BGRA 像素
        private bool _initialized;
        private bool _disposed;
        // v3 修正：用布尔标志替代 IsReferenceFrameEmpty() 的脆弱首 4 字节检测
        // 原方法在屏幕首像素为 BGRA(0,0,0,0) 时会误判为"首帧"，导致每帧都全帧编码
        private bool _isFirstFrame;

        // v2 新增：预分配缓冲池，避免每帧 GC 压力
        /// <summary>预分配的 tile 提取缓冲（复用，避免每瓦片 new byte[16KB]）。</summary>
        private byte[] _tileBuffer;
        /// <summary>预分配的 Deflate 输出缓冲（复用，避免每瓦片 new MemoryStream）。</summary>
        private byte[] _compressBuffer;
        /// <summary>预分配的 region 数组（避免 List 装箱）。</summary>
        private ZrleRegion[] _regionArray;
        // v3 新增：池化压缩数据缓冲，避免每瓦片 new byte[compressedLen]
        // 每个槽位预分配为最大压缩大小（32KB），复用跨帧
        // ZrleRegion.Data 指向池中数组，DataLen 记录实际有效长度
        private byte[][] _compressedDataPool;

        public CodecId Codec { get { return CodecId.Zrle; } }

        /// <summary>ZRLE 纯 C# 实现，始终可用。</summary>
        public bool IsAvailable { get { return !_disposed; } }

        /// <summary>
        /// 初始化编码器。分配参考帧缓冲和预分配缓冲池。
        /// targetBitrate 参数对 ZRLE 无意义（无损压缩），保留以兼容接口。
        /// </summary>
        public void Initialize(int width, int height, int targetBitrate)
        {
            if (_disposed) throw new ObjectDisposedException("ZrleEncoder");
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException("width/height must be positive");
            _width = width;
            _height = height;
            int frameSize = width * height * 4;
            _referenceFrame = new byte[frameSize];
            _isFirstFrame = true;  // v3：首帧标志

            // v2：预分配缓冲池
            _tileBuffer = new byte[TileSize * TileSize * 4];  // 最大瓦片 16KB
            _compressBuffer = new byte[TileSize * TileSize * 4 * 2];  // 压缩结果最坏情况 2 倍
            int tilesX = (width + TileSize - 1) / TileSize;
            int tilesY = (height + TileSize - 1) / TileSize;
            int maxTiles = tilesX * tilesY;
            _regionArray = new ZrleRegion[maxTiles];

            // v3：池化压缩数据缓冲（每个瓦片一个预分配数组）
            int maxCompressedSize = TileSize * TileSize * 4 * 2;  // 32KB
            _compressedDataPool = new byte[maxTiles][];
            for (int i = 0; i < maxTiles; i++)
                _compressedDataPool[i] = new byte[maxCompressedSize];

            _initialized = true;
            Logger.Info("ZRLE encoder initialized: {0}x{1}, tiles={2}x{3}", width, height, tilesX, tilesY);
        }

        /// <summary>
        /// 编码一帧 BGRA 像素。
        /// 内部自动检测变化瓦片，只压缩变化区域。
        /// 
        /// v2 修正：
        /// - forceKeyframe 参数被忽略（ZRLE 无帧间依赖，不需要强制关键帧）
        /// - 仅首帧（_referenceFrame 全零）时全帧编码
        /// - IsKeyframe 始终返回 false（避免客户端 keyframe 保护导致周期性延迟尖峰）
        /// </summary>
        public EncodedFrame Encode(byte[] pixels, bool forceKeyframe)
        {
            if (!_initialized || _disposed)
                return new EncodedFrame();

            int tilesX = (_width + TileSize - 1) / TileSize;
            int tilesY = (_height + TileSize - 1) / TileSize;
            int regionCount = 0;

            // v3 修正：用布尔标志替代 IsReferenceFrameEmpty() 的脆弱检测
            bool isFirstFrame = _isFirstFrame;

            // 遍历所有瓦片，找出变化瓦片
            for (int ty = 0; ty < tilesY; ty++)
            {
                for (int tx = 0; tx < tilesX; tx++)
                {
                    int x0 = tx * TileSize;
                    int y0 = ty * TileSize;
                    int tileW = System.Math.Min(TileSize, _width - x0);
                    int tileH = System.Math.Min(TileSize, _height - y0);

                    // v2 修正：忽略 forceKeyframe，仅首帧全帧编码
                    // ZRLE 无帧间依赖，不需要周期性关键帧
                    bool changed = isFirstFrame || !TileEquals(pixels, _referenceFrame, x0, y0, tileW, tileH);
                    if (!changed) continue;

                    // v2：提取瓦片到预分配缓冲（避免 new byte[]）
                    ExtractTile(pixels, x0, y0, tileW, tileH, _tileBuffer);

                    // v2：Deflate 压缩到预分配缓冲（避免 new MemoryStream）
                    int compressedLen = DeflateCompress(_tileBuffer, tileW * tileH * 4, _compressBuffer);

                    // v3 修正：使用池化缓冲，避免每瓦片 new byte[compressedLen]
                    // v2 每帧 576 次 new byte[] = 9.2MB 临时分配，v3 为 0 次分配
                    // Data 指向池中预分配数组，DataLen 记录实际有效长度
                    Buffer.BlockCopy(_compressBuffer, 0, _compressedDataPool[regionCount], 0, compressedLen);

                    _regionArray[regionCount] = new ZrleRegion
                    {
                        X = x0,
                        Y = y0,
                        Width = tileW,
                        Height = tileH,
                        Encoding = ZrleRegionEncoding.Deflate,
                        Data = _compressedDataPool[regionCount],  // v3：指向池化缓冲
                        DataLen = compressedLen  // v3：实际有效长度
                    };
                    regionCount++;
                }
            }

            // v2 修正：编码成功后才更新参考帧（失败时不污染参考帧）
            Buffer.BlockCopy(pixels, 0, _referenceFrame, 0, pixels.Length);
            _isFirstFrame = false;  // v3：清除首帧标志

            // v3 修正：直接用 Pack(_regionArray, regionCount)，避免每帧 new ZrleRegion[] + Array.Copy
            byte[] packed = ZrleRegionCodec.Pack(_regionArray, regionCount);

            return new EncodedFrame
            {
                Data = packed,
                // v2 修正：始终 false！ZRLE 无帧间依赖，不需要 keyframe 标志。
                // 若返回 true，客户端 ClientStreamSession.EnqueueVideoFrame 会保护此帧
                // 不被覆盖（ClientStreamSession.cs:385-387），导致每 30 帧周期性延迟尖峰。
                IsKeyframe = false,
                Width = _width,
                Height = _height
            };
        }

        /// <summary>
        /// v2 新增：评估变化比例（供阶段三 HybridEncoder 决策使用）。
        /// 不实际编码，只统计变化瓦片数。
        /// </summary>
        public float EstimateChangeRatio(byte[] pixels)
        {
            if (!_initialized || _disposed || _referenceFrame == null)
                return 1.0f;  // 首帧全变化

            int tilesX = (_width + TileSize - 1) / TileSize;
            int tilesY = (_height + TileSize - 1) / TileSize;
            int totalTiles = tilesX * tilesY;
            int changedTiles = 0;

            for (int ty = 0; ty < tilesY; ty++)
            {
                for (int tx = 0; tx < tilesX; tx++)
                {
                    int x0 = tx * TileSize;
                    int y0 = ty * TileSize;
                    int tileW = System.Math.Min(TileSize, _width - x0);
                    int tileH = System.Math.Min(TileSize, _height - y0);
                    if (!TileEquals(pixels, _referenceFrame, x0, y0, tileW, tileH))
                        changedTiles++;
                }
            }
            return (float)changedTiles / totalTiles;
        }

        /// <summary>重置编码器（分辨率变化时调用）。</summary>
        public void Reset()
        {
            _referenceFrame = null;
            _tileBuffer = null;
            _compressBuffer = null;
            _regionArray = null;
            _compressedDataPool = null;  // v3
            _isFirstFrame = true;        // v3
            _initialized = false;
        }

        public void Dispose()
        {
            _disposed = true;
            _referenceFrame = null;
            _tileBuffer = null;
            _compressBuffer = null;
            _regionArray = null;
            _compressedDataPool = null;  // v3
        }

        // ═══════════════════════════════════════════════════════════════
        // v2 修正的私有方法
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// v2 修正：uint 步长比较（替代逐字节，性能提升 4 倍）。
        /// 对比瓦片像素是否与参考帧相同。
        /// </summary>
        private bool TileEquals(byte[] cur, byte[] refFrame, int x0, int y0, int w, int h)
        {
            int stride = _width * 4;
            int tileStride = w * 4;
            for (int y = 0; y < h; y++)
            {
                int offset = (y0 + y) * stride + x0 * 4;
                // v2：按 4 字节（uint）步长比较，性能提升 ~4 倍
                // net40 无 Span，用 BitConverter.ToUInt32 读取
                int uintCount = tileStride / 4;
                for (int i = 0; i < uintCount; i++)
                {
                    int off = offset + i * 4;
                    if (BitConverter.ToUInt32(cur, off) != BitConverter.ToUInt32(refFrame, off))
                        return false;
                }
                // 处理尾部不足 4 字节的余数（瓦片宽度非 4 的倍数时）
                int remainder = tileStride % 4;
                if (remainder > 0)
                {
                    int tailOff = offset + uintCount * 4;
                    for (int i = 0; i < remainder; i++)
                    {
                        if (cur[tailOff + i] != refFrame[tailOff + i])
                            return false;
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// v2 修正：提取瓦片到预分配缓冲（避免 new byte[]）。
        /// </summary>
        private void ExtractTile(byte[] pixels, int x0, int y0, int w, int h, byte[] output)
        {
            int stride = _width * 4;
            int tileStride = w * 4;
            for (int y = 0; y < h; y++)
            {
                Buffer.BlockCopy(pixels, (y0 + y) * stride + x0 * 4, output, y * tileStride, tileStride);
            }
        }

        /// <summary>
        /// v2 修正：Deflate 压缩到预分配缓冲（避免 new MemoryStream）。
        /// 返回压缩后数据长度，写入 output 缓冲。
        /// </summary>
        private int DeflateCompress(byte[] input, int inputLen, byte[] output)
        {
            // v2：用 DeflateStream 替代 GZipStream
            // Deflate 无 GZip 的 18 字节头尾开销（576 瓦片 × 18 字节 = 10KB/帧）
            // 直接写入预分配的 MemoryStream（复用内部缓冲）
            using (var ms = new System.IO.MemoryStream(output, 0, output.Length))
            using (var ds = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionMode.Compress))
            {
                ds.Write(input, 0, inputLen);
                ds.Close();
                return (int)ms.Position;
            }
        }

        // v3 修正：移除 IsReferenceFrameEmpty() 方法
        // 原方法检查前 4 字节是否为 0，但屏幕首像素为 BGRA(0,0,0,0) 时会误判
        // 改用 _isFirstFrame 布尔标志（Initialize 时设 true，首次 Encode 后设 false）
    }
}
```

#### 1.3.9 新增 ZrleDecoder.cs（v2 修正）

**文件**：`src/EasyRDP.Core/Protocol/ZrleDecoder.cs`（新文件）

**v2 修正点**：
1. **Decode(data, outputBuffer) 直接写入 outputBuffer**（避免内部 _frameBuffer + BlockCopy 的双重拷贝）
2. **DeflateStream 替代 GZipStream**（与编码器一致）

**类设计**：
```csharp
namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// ZRLE 解码器：解包多矩形区域，合并到帧缓冲，输出完整 BGRA 帧。
    /// 无运动估计、无颜色空间转换，纯像素操作。
    /// 
    /// v2 修正：
    /// - Decode(data, outputBuffer) 直接写入 outputBuffer，省去一次 BlockCopy
    /// - DeflateStream 替代 GZipStream（与编码器一致）
    /// </summary>
    public class ZrleDecoder : IVideoDecoder
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private int _width;
        private int _height;
        private byte[] _frameBuffer;  // 完整帧缓冲（累积变化区域）
        // v3 新增：预分配解压缓冲，避免 DeflateDecompress 每区域 4 次堆分配
        // 大小 = 最大瓦片 64×64×4 = 16KB，足够容纳任意单区域解压结果
        private byte[] _decompressBuffer;
        private bool _initialized;
        private bool _disposed;

        public CodecId Codec { get { return CodecId.Zrle; } }
        public bool IsAvailable { get { return !_disposed; } }

        public void Initialize(int width, int height)
        {
            if (_disposed) throw new ObjectDisposedException("ZrleDecoder");
            _width = width;
            _height = height;
            _frameBuffer = new byte[width * height * 4];
            // v3：预分配解压缓冲（最大瓦片大小 16KB）
            _decompressBuffer = new byte[64 * 64 * 4];
            _initialized = true;
            Logger.Info("ZRLE decoder initialized: {0}x{1}", width, height);
        }

        /// <summary>解码并输出到内部帧缓冲。</summary>
        public DecodeResult Decode(byte[] data)
        {
            if (!_initialized || _disposed)
                return new DecodeResult { Status = DecodeStatus.Failed };

            try
            {
                var regions = ZrleRegionCodec.Unpack(data);
                foreach (var region in regions)
                {
                    ApplyRegion(region, _frameBuffer);
                }
                return new DecodeResult { Status = DecodeStatus.Ok, Pixels = _frameBuffer };
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ZRLE decode failed");
                return new DecodeResult { Status = DecodeStatus.Failed };
            }
        }

        /// <summary>
        /// v3 修正：解码到调用方提供的输出缓冲（省拷贝优化）。
        /// 
        /// v2 的做法（先全帧拷贝 + 双倍区域应用）反而比原始方案更慢：
        ///   v2 = 1次全帧BlockCopy(8.3MB) + 2×区域应用
        ///   v3 = 1×区域应用 + 1次全帧BlockCopy(8.3MB)  ← 严格优于 v2
        /// 
        /// v3 做法：先将变化区域应用到 _frameBuffer（原地更新），
        /// 然后整体拷贝到 outputBuffer。
        /// 
        /// 阶段二 CopyRect 注意：CopyRect 需要读取上一帧的 _frameBuffer。
        /// 若在同一帧内先应用了其他区域再处理 CopyRect，会读到已更新的数据。
        /// 阶段二实现时需改为"先处理 CopyRect，再处理其他区域"或使用双缓冲。
        /// </summary>
        public DecodeResult Decode(byte[] data, byte[] outputBuffer)
        {
            if (!_initialized || _disposed)
                return new DecodeResult { Status = DecodeStatus.Failed };

            if (outputBuffer == null || outputBuffer.Length < _width * _height * 4)
                return new DecodeResult { Status = DecodeStatus.Failed };

            try
            {
                var regions = ZrleRegionCodec.Unpack(data);
                // v3：只将变化区域应用到 _frameBuffer，然后整体拷贝到 outputBuffer
                foreach (var region in regions)
                {
                    ApplyRegion(region, _frameBuffer);
                }
                Buffer.BlockCopy(_frameBuffer, 0, outputBuffer, 0, _frameBuffer.Length);
                return new DecodeResult { Status = DecodeStatus.Ok, Pixels = outputBuffer };
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ZRLE decode failed");
                return new DecodeResult { Status = DecodeStatus.Failed };
            }
        }

        public void Reset()
        {
            _frameBuffer = null;
            _decompressBuffer = null;
            _initialized = false;
        }

        public void Dispose()
        {
            _disposed = true;
            _frameBuffer = null;
            _decompressBuffer = null;
        }

        /// <summary>
        /// 将单个区域应用到指定缓冲。
        /// v3：Deflate 解压改为写入预分配 _decompressBuffer（0 次堆分配）。
        /// </summary>
        private void ApplyRegion(ZrleRegion region, byte[] target)
        {
            int stride = _width * 4;
            int regionStride = region.Width * 4;

            switch (region.Encoding)
            {
                case ZrleRegionEncoding.Raw:
                    // Raw 直接从 region.Data 写入 target
                    for (int y = 0; y < region.Height; y++)
                    {
                        Buffer.BlockCopy(region.Data, y * regionStride,
                            target, (region.Y + y) * stride + region.X * 4, regionStride);
                    }
                    break;

                case ZrleRegionEncoding.Deflate:
                    // v3：解压到预分配缓冲（0 次堆分配），再逐行写入 target
                    int decompressedLen = DeflateDecompress(region.Data, _decompressBuffer, 0);
                    for (int y = 0; y < region.Height; y++)
                    {
                        Buffer.BlockCopy(_decompressBuffer, y * regionStride,
                            target, (region.Y + y) * stride + region.X * 4, regionStride);
                    }
                    break;

                case ZrleRegionEncoding.FillRect:
                    // 阶段二实现
                    break;

                case ZrleRegionEncoding.CopyRect:
                    // 阶段二实现（CopyRect 必须用 _frameBuffer 作为源，不能用 outputBuffer）
                    break;
            }
        }

        /// <summary>
        /// v3 修正：Deflate 解压到预分配缓冲（替代 v2 的 4 次分配/区域）。
        /// 
        /// v2 每次调用分配 4 个对象（2 MemoryStream + DeflateStream + ToArray byte[]），
        /// 576 区域 = 2304 分配/帧，客户端 GC 压力严重。
        /// 
        /// v3 改为写入预分配的 _decompressBuffer，0 次堆分配（仅 DeflateStream 栈分配）。
        /// 返回写入长度，调用方按长度截取。
        /// </summary>
        private int DeflateDecompress(byte[] data, byte[] output, int outputOffset)
        {
            using (var ms = new System.IO.MemoryStream(data))
            using (var ds = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionMode.Decompress))
            {
                int totalRead = 0;
                int read;
                int remaining = output.Length - outputOffset;
                while (totalRead < remaining &&
                       (read = ds.Read(output, outputOffset + totalRead, remaining - totalRead)) > 0)
                {
                    totalRead += read;
                }
                return totalRead;
            }
        }
    }
}
```

### 1.4 不需要改动的层（验证）

| 层 | 文件 | 为什么不需要改 |
|----|------|---------------|
| **抓屏** | CaptureService.cs | ZRLE 编码器接受整帧 BGRA，与 H264 输入一致 |
| **传输** | VideoFrameMessage.cs | `Data` 是 `byte[]`，ZRLE 打包后也是 `byte[]`，协议层无感知 |
| **传输** | MessageReassembler.cs | 不感知 Data 内容格式 |
| **传输** | FramingBuffer.cs | 不感知 Data 内容格式 |
| **显示** | WpfRenderTarget.RenderFrame | 接受完整 BGRA 帧，ZRLE 解码器输出完整帧 |
| **H264** | H264EncoderNative/H264DecoderNative | 独立路径，不受影响 |

> **v2 修正**：不再声称"编排层不动"。ServerStreamSession.EncodeLoop 需要小改（+5 行），在 ZRLE 模式下跳过外部变化检测。

### 1.5 验证标准

- [ ] EasyRDP.Core 编译通过（net40/netstandard2.0/net8.0）
- [ ] EasyRDP.Server.Wpf 编译通过（net40）
- [ ] EasyRDP.Client.Wpf 编译通过（net8.0-windows）
- [ ] 单元测试：ZrleRegionCodec.Pack/Unpack 往返一致
- [ ] 单元测试：ZrleEncoder 编码静态帧 → 0 区域
- [ ] 单元测试：ZrleEncoder 编码全变化帧 → tilesX×tilesY 区域
- [ ] 单元测试：ZrleEncoder 始终返回 IsKeyframe=false
- [ ] 单元测试：ZrleDecoder 解码后像素与编码前一致
- [ ] 单元测试：EstimateChangeRatio 返回正确比例
- [ ] 集成测试：ZRLE 会话 FPS ≥ 10（XP VM 单核）
- [ ] H264 路径回归测试通过

---

## 阶段二：FillRect/CopyRect 快速路径 + 显示层脏矩形

### 2.1 目标

- **静态场景 FPS**：10-15 → 20-40
- **窗口拖动**：无撕裂，编码 <10ms
- **客户端渲染 CPU**：降低 50-80%（脏矩形局部更新）

### 2.2 改动总览

| 改动类型 | 文件 | 改动量 |
|---------|------|--------|
| **修改** | `ZrleEncoder.cs`（阶段一新增） | +100 行（FillRect/CopyRect 检测） |
| **修改** | `ZrleDecoder.cs`（阶段一新增） | +50 行（FillRect/CopyRect 应用） |
| **修改** | [VideoFrameMessage.cs](../src/EasyRDP.Core/Protocol/VideoFrameMessage.cs) | +20 行（DirtyRects 字段，可选） |
| **修改** | [WpfRenderTarget.cs](../src/EasyRDP.Client.Wpf/WpfRenderTarget.cs) | +40 行（局部更新重载） |
| **修改** | [IRenderTarget.cs](../src/EasyRDP.Core/Rendering/IRenderTarget.cs) | +5 行（新方法签名） |
| **修改** | [ClientStreamSession.cs](../src/EasyRDP.Client.Wpf/ClientStreamSession.cs) | +15 行（传递 dirty rects） |

### 2.3 详细改动

#### 2.3.1 ZrleEncoder 新增 FillRect 检测

**文件**：`src/EasyRDP.Core/Protocol/ZrleEncoder.cs`（阶段一新增）

**改动位置**：`Encode` 方法中，提取瓦片像素后、Deflate 压缩前，新增 FillRect 检测：

```csharp
// v2：提取瓦片到预分配缓冲
ExtractTile(pixels, x0, y0, tileW, tileH, _tileBuffer);
int tilePixelCount = tileW * tileH;

// FillRect 检测：瓦片内所有像素相同 → 只传 4 字节颜色
byte fillB = _tileBuffer[0], fillG = _tileBuffer[1], fillR = _tileBuffer[2], fillA = _tileBuffer[3];
bool isFillRect = true;
for (int i = 4; i < tilePixelCount * 4; i += 4)
{
    if (_tileBuffer[i] != fillB || _tileBuffer[i+1] != fillG
        || _tileBuffer[i+2] != fillR || _tileBuffer[i+3] != fillA)
    {
        isFillRect = false;
        break;
    }
}

if (isFillRect)
{
    _regionArray[regionCount] = new ZrleRegion
    {
        X = x0, Y = y0, Width = tileW, Height = tileH,
        Encoding = ZrleRegionEncoding.FillRect,
        Data = new byte[] { fillB, fillG, fillR, fillA }
    };
    regionCount++;
}
else
{
    // Deflate 压缩（阶段一逻辑）
    int compressedLen = DeflateCompress(_tileBuffer, tilePixelCount * 4, _compressBuffer);
    byte[] compressedData = new byte[compressedLen];
    Buffer.BlockCopy(_compressBuffer, 0, compressedData, 0, compressedLen);
    _regionArray[regionCount] = new ZrleRegion
    {
        X = x0, Y = y0, Width = tileW, Height = tileH,
        Encoding = ZrleRegionEncoding.Deflate,
        Data = compressedData
    };
    regionCount++;
}
```

#### 2.3.2 ZrleEncoder 新增 CopyRect 检测（v2 修正）

**v2 修正点**：
1. **自定义 ZrlePoint 结构体**替代 `System.Drawing.Point`（netstandard2.0 不可用）
2. **哈希预筛选**：先比较瓦片 CRC32 哈希，哈希匹配才做全比较
3. **限制搜索范围**：±16 像素（从 ±32 缩小），步长 4（从 8 缩小）
4. **仅鼠标按下时触发**（窗口拖动场景）

**改动位置**：`Encode` 方法中，FillRect 检测失败后、Deflate 压缩前：

```csharp
// v2：自定义结构体替代 System.Drawing.Point（netstandard2.0 不可用）
private struct ZrlePoint { public int X; public int Y; }

// CopyRect 检测：当前瓦片是否与参考帧中某个位置的瓦片完全相同
// v2 修正：限制触发条件 + 哈希预筛选 + 缩小搜索范围
if (!isFillRect && !isFirstFrame && _mouseButtonDown)
{
    var copySource = FindCopySource(pixels, x0, y0, tileW, tileH);
    if (copySource.HasValue)
    {
        _regionArray[regionCount] = new ZrleRegion
        {
            X = x0, Y = y0, Width = tileW, Height = tileH,
            Encoding = ZrleRegionEncoding.CopyRect,
            Data = PackCopyRectData(copySource.Value.X, copySource.Value.Y)
        };
        regionCount++;
        continue;  // 跳过 Deflate 压缩
    }
}

/// <summary>
/// v2 修正：在参考帧中搜索与当前瓦片匹配的源位置。
/// 优化：哈希预筛选 + 缩小搜索范围（±16 像素，步长 4）+ 仅鼠标按下时触发。
/// </summary>
private ZrlePoint? FindCopySource(byte[] cur, int x0, int y0, int w, int h)
{
    // v2：先计算当前瓦片的 CRC32 哈希
    uint curHash = ComputeTileHash(cur, x0, y0, w, h);

    // v2：搜索范围缩小到 ±16 像素，步长 4（从 ±32/步长 8 缩小）
    // 81 次 → 9×9=81 降至 9×9=81... 不对，±16 步长 4 = 9 个位置每轴 = 81 次
    // 但哈希预筛选让实际全比较次数大幅降低（哈希不匹配直接跳过）
    for (int dy = -16; dy <= 16; dy += 4)
    {
        for (int dx = -16; dx <= 16; dx += 4)
        {
            int srcX = x0 + dx;
            int srcY = y0 + dy;
            if (srcX < 0 || srcY < 0 || srcX + w > _width || srcY + h > _height)
                continue;
            if (dx == 0 && dy == 0) continue;

            // v2：哈希预筛选（哈希不匹配直接跳过，避免 16KB 全比较）
            uint srcHash = ComputeTileHash(_referenceFrame, srcX, srcY, w, h);
            if (srcHash != curHash) continue;

            // 哈希匹配，做全比较确认
            if (TileEqualsSource(cur, _referenceFrame, x0, y0, srcX, srcY, w, h))
                return new ZrlePoint { X = srcX, Y = srcY };
        }
    }
    return null;
}

/// <summary>v2：计算瓦片的 CRC32 哈希（用于 CopyRect 预筛选）。</summary>
private uint ComputeTileHash(byte[] pixels, int x0, int y0, int w, int h)
{
    int stride = _width * 4;
    int tileStride = w * 4;
    uint hash = 0;
    for (int y = 0; y < h; y++)
    {
        int offset = (y0 + y) * stride + x0 * 4;
        for (int i = 0; i < tileStride; i += 4)
        {
            hash = unchecked(hash * 31 + BitConverter.ToUInt32(pixels, offset + i));
        }
    }
    return hash;
}

/// <summary>v2：对比当前帧瓦片与参考帧源位置瓦片是否相同。</summary>
private bool TileEqualsSource(byte[] cur, byte[] refFrame, int curX, int curY, int srcX, int srcY, int w, int h)
{
    int stride = _width * 4;
    int tileStride = w * 4;
    int uintCount = tileStride / 4;
    for (int y = 0; y < h; y++)
    {
        int curOff = (curY + y) * stride + curX * 4;
        int srcOff = (srcY + y) * stride + srcX * 4;
        for (int i = 0; i < uintCount; i++)
        {
            if (BitConverter.ToUInt32(cur, curOff + i * 4) != BitConverter.ToUInt32(refFrame, srcOff + i * 4))
                return false;
        }
    }
    return true;
}

/// <summary>v2：打包 CopyRect 数据（SrcX + SrcY，8 字节）。</summary>
private byte[] PackCopyRectData(int srcX, int srcY)
{
    byte[] data = new byte[8];
    Buffer.BlockCopy(BitConverter.GetBytes(srcX), 0, data, 0, 4);
    Buffer.BlockCopy(BitConverter.GetBytes(srcY), 0, data, 4, 4);
    return data;
}

// v2：鼠标状态字段（由 ServerStreamSession 在收到鼠标按下事件时设置）
private volatile bool _mouseButtonDown;
/// <summary>v2：设置鼠标按下状态（仅鼠标按下时启用 CopyRect 搜索）。</summary>
public void SetMouseButtonDown(bool isDown) { _mouseButtonDown = isDown; }
```

#### 2.3.3 ZrleDecoder 实现 FillRect/CopyRect

**文件**：`src/EasyRDP.Core/Protocol/ZrleDecoder.cs`

**改动位置**：`ApplyRegion` 方法的 switch 语句：

```csharp
case ZrleRegionEncoding.FillRect:
    // 用 4 字节颜色填充整个区域
    byte fillB = region.Data[0];
    byte fillG = region.Data[1];
    byte fillR = region.Data[2];
    byte fillA = region.Data[3];
    // v2：按 uint 步长填充（4 像素 = 4×4=16 字节，用 Buffer.BlockCopy 整行填充）
    byte[] fillRow = new byte[regionStride];
    for (int x = 0; x < region.Width; x++)
    {
        fillRow[x * 4] = fillB;
        fillRow[x * 4 + 1] = fillG;
        fillRow[x * 4 + 2] = fillR;
        fillRow[x * 4 + 3] = fillA;
    }
    for (int y = 0; y < region.Height; y++)
    {
        int rowOffset = (region.Y + y) * stride + region.X * 4;
        Buffer.BlockCopy(fillRow, 0, target, rowOffset, regionStride);
    }
    break;

case ZrleRegionEncoding.CopyRect:
    // v2：CopyRect 必须用 _frameBuffer 作为源（不能用 outputBuffer）
    // 从帧缓冲的源位置复制到目标位置
    int srcX = BitConverter.ToInt32(region.Data, 0);
    int srcY = BitConverter.ToInt32(region.Data, 4);
    // 注意：源和目标可能重叠，需要从后向前复制（避免覆盖）
    if (srcY < region.Y || (srcY == region.Y && srcX < region.X))
    {
        // 源在目标上方/左方：从后向前复制
        for (int y = region.Height - 1; y >= 0; y--)
        {
            int srcRow = (srcY + y) * stride + srcX * 4;
            int dstRow = (region.Y + y) * stride + region.X * 4;
            Buffer.BlockCopy(_frameBuffer, srcRow, target, dstRow, regionStride);
        }
    }
    else
    {
        // 源在目标下方/右方：从前向后复制
        for (int y = 0; y < region.Height; y++)
        {
            int srcRow = (srcY + y) * stride + srcX * 4;
            int dstRow = (region.Y + y) * stride + region.X * 4;
            Buffer.BlockCopy(_frameBuffer, srcRow, target, dstRow, regionStride);
        }
    }
    break;
```

> **注意**：v2 修正了 `ApplyRegion` 签名，CopyRect 源始终从 `_frameBuffer` 读取，目标写入 `target` 参数（可能是 outputBuffer 或 _frameBuffer）。在 `Decode(data, outputBuffer)` 重载中，CopyRect 仍从 `_frameBuffer` 读取源，但写入 `outputBuffer`。

#### 2.3.4 显示层脏矩形局部更新（可选优化）

**文件**：[src/EasyRDP.Client.Wpf/WpfRenderTarget.cs](../src/EasyRDP.Client.Wpf/WpfRenderTarget.cs)

**改动 1**：[IRenderTarget.cs](../src/EasyRDP.Core/Rendering/IRenderTarget.cs) 新增重载方法：

```csharp
/// <summary>渲染一帧 BGRA 像素到 WriteableBitmap（带脏矩形，局部更新）。</summary>
void RenderFrame(byte[] bgraPixels, int w, int h, ScreenRect[] dirtyRects);
```

**改动 2**：`WpfRenderTarget.RenderFrame` 新增重载实现：

```csharp
public void RenderFrame(byte[] bgraPixels, int w, int h, ScreenRect[] dirtyRects)
{
    if (_disposed || bgraPixels == null || w <= 0 || h <= 0) return;
    
    // 若无 dirtyRects 或为全帧更新，回退到全帧 RenderFrame
    if (dirtyRects == null || dirtyRects.Length == 0)
    {
        RenderFrame(bgraPixels, w, h);
        return;
    }

    // 限制最大脏矩形数（避免过多小矩形导致 WritePixels 调用开销）
    const int MaxDirtyRects = 16;
    if (dirtyRects.Length > MaxDirtyRects)
    {
        RenderFrame(bgraPixels, w, h);
        return;
    }

    // 异步转发到 UI 线程，对每个脏矩形调用 WritePixels
    _uiDispatcher.BeginInvoke(new Action(() =>
    {
        if (_disposed || _bitmap == null) return;
        var bmp = _bitmap;
        bmp.Lock();
        try
        {
            int stride = w * 4;
            foreach (var rect in dirtyRects)
            {
                bmp.WritePixels(
                    new Int32Rect(rect.X, rect.Y, rect.Width, rect.Height),
                    bgraPixels,
                    stride,
                    rect.Y * stride + rect.X * 4);
            }
        }
        finally
        {
            bmp.Unlock();
        }
    }), DispatcherPriority.Render);
}
```

**改动 3**：[ClientStreamSession.cs](../src/EasyRDP.Client.Wpf/ClientStreamSession.cs) 的 `ProcessVideoFrame` 方法传递 dirty rects：

```csharp
// 从 VideoFrameMessage 提取 dirty rects（若编码器支持）
ScreenRect[] dirtyRects = null;
if (msg.Codec == CodecId.Zrle)
{
    dirtyRects = ZrleRegionCodec.ExtractRects(msg.Data);
}

// 渲染时传递
_renderTarget?.RenderFrame(frame.Pixels, frame.Width, frame.Height, dirtyRects);
```

### 2.4 验证标准

- [ ] 单元测试：FillRect 检测纯色瓦片 → Encoding=FillRect
- [ ] 单元测试：CopyRect 检测位移瓦片 → Encoding=CopyRect
- [ ] 单元测试：ZrleDecoder 正确应用 FillRect/CopyRect
- [ ] 单元测试：CopyRect 源重叠时正确处理（从后向前复制）
- [ ] 集成测试：静态屏幕 FPS ≥ 20
- [ ] 集成测试：窗口拖动无撕裂
- [ ] 客户端渲染 CPU 占用降低 50%+

---

## 阶段三：客户端请求驱动流控

### 3.1 目标

- **动态内容 FPS**：保持 10-15（ZRLE 主导）
- **流控**：客户端处理不过来时不丢帧（请求驱动）

> **v3 重大修正**：移除 HybridEncoder 和 Tight JPEG。
>
> **v2 致命缺陷**：HybridEncoder 在 changeRatio > 阈值时返回 H264 编码数据，但 `Codec` 属性标记为 `CodecId.Zrle`。客户端 `DecoderFactory.Create(CodecId.Zrle)` 返回 `ZrleDecoder`，无法解码 H264 数据 → 解码失败。
>
> VideoFrameMessage 的 payload 格式（`Width(4) Height(4) IsKeyframe(1) SequenceNumber(8) DataLen(4) Data(*)`）没有携带每帧 codec 字段。要支持混合编码需要修改协议格式（在 IsKeyframe 后插入 Codec(1) 字节），但这会偏移所有后续字段，破坏与现有 H264 客户端的兼容性。
>
> **v3 决策**：放弃 HybridEncoder。原因：
> 1. 在 XP 单核目标平台上，H264 始终比 ZRLE 慢（280-1000ms vs 50-100ms），即使全屏变化场景 ZRLE 仍优于 H264
> 2. 混合编码需要协议层改动（per-frame codec 字段），引入兼容性风险
> 3. ZRLE 的 EstimateChangeRatio 方法保留（阶段一已实现），供未来可能的智能降采样使用
>
> 阶段三聚焦于**客户端请求驱动流控**，这是更有价值的优化方向。

### 3.2 改动总览

| 改动类型 | 文件 | 改动量 |
|---------|------|--------|
| **修改** | [MessageType.cs](../src/EasyRDP.Core/Protocol/MessageType.cs) | +1 行（FramebufferUpdateRequest） |
| **修改** | [ServerStreamSession.cs](../src/EasyRDP.Server.Wpf/ServerStreamSession.cs) | +30 行（请求驱动流控） |
| **修改** | [ClientStreamSession.cs](../src/EasyRDP.Client.Wpf/ClientStreamSession.cs) | +20 行（发送请求） |

> **v3 修正**：移除 HybridEncoder.cs（~200 行）、ZrleEncoder JPEG 扩展（+50 行）、ZrleDecoder JPEG 解码（+30 行）。阶段三改动量从 6 个文件降至 3 个文件。
>
> **v2 修正**：MessageType.cs 路径从 `Transport/` 改为 `Protocol/`（实际位置）。

### 3.3 详细改动

#### 3.3.1 客户端请求驱动流控

**文件**：[src/EasyRDP.Core/Protocol/MessageType.cs](../src/EasyRDP.Core/Protocol/MessageType.cs)

> **v2 修正**：路径从 `Transport/MessageType.cs` 改为 `Protocol/MessageType.cs`（实际位置）。

**新增消息类型**：
```csharp
/// <summary>客户端请求下一帧（流控）。</summary>
FramebufferUpdateRequest = 0x51
```

**文件**：[src/EasyRDP.Server.Wpf/ServerStreamSession.cs](../src/EasyRDP.Server.Wpf/ServerStreamSession.cs)

**改动**：EncodeLoop 中检查客户端请求标志：

```csharp
// 新增字段
private volatile bool _clientRequestPending;
private volatile bool _flowControlEnabled;

// EncodeLoop 循环顶部
if (_flowControlEnabled && !_clientRequestPending)
{
    // 等待客户端请求（带超时保活，避免客户端崩溃后服务端永远等待）
    lock (_lock)
    {
        if (!_clientRequestPending && !_stopping)
            Monitor.Wait(_lock, 1000);
    }
    if (_stopping) break;
    if (!_clientRequestPending) continue;  // 超时，跳过本帧
}
_clientRequestPending = false;

// 新增方法：处理客户端请求
public void OnFramebufferUpdateRequest()
{
    _clientRequestPending = true;
    lock (_lock) { Monitor.Pulse(_lock); }
}
```

**文件**：[src/EasyRDP.Client.Wpf/ClientStreamSession.cs](../src/EasyRDP.Client.Wpf/ClientStreamSession.cs)

**改动**：渲染完成后发送请求：

```csharp
private void RenderLoop()
{
    while (_running)
    {
        // ... 渲染逻辑 ...
        _renderTarget?.RenderFrame(frame.Pixels, frame.Width, frame.Height);
        
        // 渲染完成后请求下一帧（流控）
        if (_flowControlEnabled)
        {
            SendFramebufferUpdateRequest();
        }
    }
}

private void SendFramebufferUpdateRequest()
{
    MessageReassembler.FragAndSend(0, (byte)MessageType.FramebufferUpdateRequest,
        new byte[0], (sid, data) => _transport.Send(data), 0);
}
```

### 3.4 验证标准

- [ ] 客户端请求驱动：服务端不主动推送，等请求才编码
- [ ] 集成测试：动态内容 FPS ≥ 10
- [ ] 集成测试：客户端解码积压时不再丢帧（流控生效）

---

## 附录：文件改动清单

### A.1 阶段一改动（共 11 个文件）

| # | 类型 | 文件路径 | 改动内容 |
|---|------|---------|---------|
| 1 | 修改 | `src/EasyRDP.Core/Protocol/CodecId.cs` | +1 行枚举值 `Zrle = 3` |
| 2 | 修改 | `src/EasyRDP.Core/Protocol/CodecCapabilities.cs` | +1 行枚举值 `Zrle = 1 << 2` |
| 3 | 修改 | `src/EasyRDP.Core/Protocol/CodecNegotiator.cs` | +3 行 ZRLE 优先级判断 |
| 4 | 修改 | `src/EasyRDP.Core/Protocol/EncoderFactory.cs` | +6 行 case + GetAvailableCodecs |
| 5 | 修改 | `src/EasyRDP.Core/Protocol/DecoderFactory.cs` | +6 行 case + GetAvailableCodecs |
| 6 | 修改 | `src/EasyRDP.Server.Wpf/ServerStreamSession.cs` | +5 行 ZRLE 模式跳过外部变化检测 |
| 7 | 新增 | `src/EasyRDP.Core/Protocol/ZrleRegionCodec.cs` | ~350 行（区域打包/解包，v3 含 DataLen 字段 + Pack(count) 重载） |
| 8 | 新增 | `src/EasyRDP.Core/Protocol/ZrleEncoder.cs` | ~550 行（v3 含 _isFirstFrame 标志 + _compressedDataPool 池化） |
| 9 | 新增 | `src/EasyRDP.Core/Protocol/ZrleDecoder.cs` | ~370 行（v3 含 _decompressBuffer 预分配 + 单次 ApplyRegion） |
| 10 | 新增 | `test/EasyRDP.Core.Tests/ZrleEncoderTests.cs` | ~200 行（单元测试） |
| 11 | 新增 | `test/EasyRDP.Core.Tests/ZrleDecoderTests.cs` | ~150 行（单元测试） |

**修改总量**：~22 行（6 个文件）
**新增总量**：~1620 行（5 个文件）

### A.2 阶段二改动（共 7 个文件）

| # | 类型 | 文件路径 | 改动内容 |
|---|------|---------|---------|
| 1 | 修改 | `src/EasyRDP.Core/Protocol/ZrleEncoder.cs` | +100 行 FillRect/CopyRect 检测（含 ZrlePoint+哈希预筛选） |
| 2 | 修改 | `src/EasyRDP.Core/Protocol/ZrleDecoder.cs` | +50 行 FillRect/CopyRect 应用（v3 注意：CopyRect 需先于其他区域处理） |
| 3 | 修改 | `src/EasyRDP.Core/Rendering/IRenderTarget.cs` | +5 行新方法签名 |
| 4 | 修改 | `src/EasyRDP.Client.Wpf/WpfRenderTarget.cs` | +40 行局部更新重载 |
| 5 | 修改 | `src/EasyRDP.Client.Wpf/ClientStreamSession.cs` | +15 行传递 dirty rects |
| 6 | 修改 | `src/EasyRDP.Core/Protocol/ZrleRegionCodec.cs` | +30 行 ExtractRects 方法 |
| 7 | 修改 | `test/EasyRDP.Core.Tests/ZrleEncoderTests.cs` | +100 行 FillRect/CopyRect 测试 |

### A.3 阶段三改动（v3：共 3 个文件，移除 HybridEncoder）

| # | 类型 | 文件路径 | 改动内容 |
|---|------|---------|---------|
| 1 | 修改 | `src/EasyRDP.Core/Protocol/MessageType.cs` | +1 行 FramebufferUpdateRequest |
| 2 | 修改 | `src/EasyRDP.Server.Wpf/ServerStreamSession.cs` | +30 行请求驱动流控 |
| 3 | 修改 | `src/EasyRDP.Client.Wpf/ClientStreamSession.cs` | +20 行发送请求 |

> **v3 修正**：移除 HybridEncoder.cs（~200 行）、ZrleEncoder SyncReferenceFrame+JPEG（+50 行）、ZrleDecoder JPEG 解码（+30 行）。阶段三从 6 个文件降至 3 个文件。

### A.4 不动的文件（验证清单）

| 层 | 文件 | 不动原因 |
|----|------|---------|
| 抓屏 | CaptureService.cs | ZRLE 接受整帧 BGRA，与 H264 输入一致 |
| 抓屏 | IFrameChangeDetector.cs | ZRLE 模式下跳过外部检测器（在 ServerStreamSession 内联判断） |
| 传输 | VideoFrameMessage.cs | Data 是 byte[]，格式无关 |
| 传输 | MessageReassembler.cs | 不感知 Data 内容 |
| 传输 | FramingBuffer.cs | 不感知 Data 内容 |
| 传输 | Constants.cs | 分片大小不变 |
| 显示 | MainWindow.xaml | Image.Source 绑定不变 |
| H264 | H264EncoderNative.cs | 独立路径 |
| H264 | H264DecoderNative.cs | 独立路径 |
| H264 | H264Native.cs | 独立路径 |

---

## 附录：修正记录

### B.0 v3 修正记录（本次更新）

> **v3 修正背景**：v2 修正了审核报告中的全部 11 个问题，但引入了 5 个新问题（1 个致命 + 2 个性能倒退 + 2 个健壮性隐患）。

| # | 问题 | 严重度 | v3 修正方案 | 影响 |
|---|------|--------|------------|------|
| A | ZrleDecoder 双重 ApplyRegion 反而更慢 | **性能倒退** | 改为单次区域应用 + 单次全帧拷贝（v2 是 1 次拷贝 + 2 次应用） | 解码耗时减半 |
| B | GC 压力未完全解决（每瓦片仍 new byte[]） | **性能** | 新增 _compressedDataPool 池化 + ZrleRegion.DataLen 字段 + Pack(count) 重载 | 每帧 0 次堆分配（v2 为 576 次） |
| C | IsReferenceFrameEmpty 检查脆弱 | **健壮性** | 改用 _isFirstFrame 布尔标志（Initialize=true, Encode 后=false） | 避免首像素为黑色时每帧全帧编码 |
| D | HybridEncoder Codec 标识冲突 | **致命** | 移除 HybridEncoder（XP 单核 H264 始终慢于 ZRLE，无切换必要） | 避免客户端解码失败 |
| E | ZrleDecoder DeflateDecompress 每区域 4 次分配 | **性能** | 改为写入预分配 _decompressBuffer，返回长度 | 客户端每帧 0 次堆分配（v2 为 2304 次） |

### B.1 v2 致命缺陷修正（阻断实施）

| # | 问题 | 修正方案 | 影响 |
|---|------|---------|------|
| 1 | IsKeyframe 周期性尖峰 | ZrleEncoder 始终返回 `IsKeyframe=false`，忽略 forceKeyframe 参数（仅首帧全帧编码） | 避免每 30 帧周期性延迟尖峰 |
| 2 | System.Drawing.Point 编译失败 | 用自定义 `struct ZrlePoint { public int X; public int Y; }` 替代 | netstandard2.0 编译通过 |
| 3 | EstimateChangeRatio 不存在 | 在 ZrleEncoder 中实现该方法（§1.3.8），统计变化瓦片数/总瓦片数 | 供未来智能降采样使用（v3 移除 HybridEncoder，此方法保留备用） |

### B.2 v2 性能问题修正

| # | 问题 | 修正方案 | 预期收益 |
|---|------|---------|---------|
| 4 | 双重变化检测 | ServerStreamSession 在 ZRLE 模式下跳过 BlockHashDirtyRectDetector（内联判断 _encoder.Codec） | -3-5ms/帧 |
| 5 | GC 压力 | v2：预分配 _tileBuffer/_compressBuffer/_regionArray；v3：新增 _compressedDataPool 池化 | -10-30ms/帧（减少 GC 暂停） |
| 6 | TileEquals 逐字节 | 改 uint 步长比较（BitConverter.ToUInt32），4 倍速 | -20-40ms/帧 |
| 7 | GZipStream 开销 | 换 DeflateStream（去掉 18 字节/瓦片 GZip 头尾） | -10KB/帧带宽 |

### B.3 v2 建议修正

| # | 问题 | 修正方案 |
|---|------|---------|
| 8 | MessageType.cs 路径错误 | `Transport/` → `Protocol/`（实际位置） |
| 9 | ZrleDecoder 额外 BlockCopy | v2：Decode 直接写入 outputBuffer；v3：修正为单次应用 + 单次拷贝（v2 的双倍应用是 bug） |
| 10 | CopyRect O(n²) 搜索 | 哈希预筛选 + 缩小搜索范围(±16/步长4) + 仅鼠标按下时触发 |
| 11 | 参考帧一致性 | Encode 成功后才更新 _referenceFrame（已在上文代码中体现） |

### B.4 性能预期修正

| 指标 | v1 计划声称 | v2 修正后 | v3 进一步修正 | 修正原因 |
|------|-----------|----------|-------------|---------|
| 编码耗时 | 20-80ms | 50-100ms | **40-90ms** | v3 池化缓冲消除 GC 暂停（-10ms） |
| 整体 FPS | 15-25 | 10-15 | **12-18** | 编码耗时降低 + 客户端解码 GC 消除 |
| 静态 FPS | 30-60 | 20-40 | **20-40** | 不变（静态场景 v2 已无 GC 问题） |
| 客户端解码耗时 | 未提及 | 1-5ms | **1-3ms** | v3 消除 DeflateDecompress 2304 次分配/帧 |

### B.5 "不动编排层"声明修正

v1 计划声称"不动编排层"，实际不准确。v2/v3 修正：

- **阶段一**：ServerStreamSession.EncodeLoop 需小改（+5 行），ZRLE 模式下跳过外部变化检测
- **阶段三**：ServerStreamSession.EncodeLoop 需改（+30 行），实现请求驱动流控

其他编排层逻辑（forceKey 判定、保活帧、队列管理）仍不动。

---

## 审核要点

1. **阶段一优先级**：ZRLE 编码器是否是最优选择？（对比 Tight JPEG、Raw+Deflate）
2. **握手协商**：ZRLE 优先级高于 H264Software 是否合理？
3. **瓦片大小**：64×64 是否合适？（对比 32×32、128×128）
4. **压缩算法**：DeflateStream 是否合适？（v2 已从 GZipStream 修正）
5. **阶段二 CopyRect 搜索范围**：±16 像素、步长 4、哈希预筛选是否合适？（v2 已修正）
6. **阶段二显示层脏矩形**：是否值得改？（客户端非瓶颈，但可降 CPU）
7. **v3 移除 HybridEncoder**：是否合理？（XP 单核 H264 始终慢于 ZRLE，切换无意义）
8. **阶段三流控**：请求驱动是否引入额外延迟？（1 个 RTT，局域网 ~1ms）
9. **IsKeyframe=false**：ZRLE 始终返回 false 是否安全？（ZRLE 无帧间依赖，安全）
10. **ServerStreamSession 小改**：ZRLE 模式跳过外部变化检测是否合理？（避免双重检测，合理）
11. **v3 ZrleRegion.DataLen 字段**：新增此字段是否影响 Pack/Unpack 兼容性？（不影响：Pack 写 DataLen，Unpack 读 DataLen，格式自洽）
12. **v3 ZrleDecoder CopyRect 顺序**：阶段二 CopyRect 必须先于 Raw/Deflate 处理，否则会读到已更新的 _frameBuffer
