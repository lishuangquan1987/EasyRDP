# EasyRDP 故障排查手册

本文档沉淀 EasyRDP 开发与联调过程中遇到并解决的问题，便于下次遇到类似问题快速定位。
按"症状 → 根因 → 修复 → 排查工具"组织。

---

## 1. 鼠标偏移（黑框相关）— 已根治

### 症状

远程桌面中，鼠标点击/光标位置与画面内容错位：
- 非全屏：左右有黑框 → **水平方向**偏移
- 全屏：上下有黑框 → **垂直方向**偏移

### 根因（WPF Image 布局锁定）

`Image` 控件的 `ArrangeOverride` 按内容（bitmap）宽高比锁定 `ActualWidth/Height`：
即使显式设置 `Width/Height`、`HorizontalAlignment/VerticalAlignment=Stretch`、
XAML 绑定、代码赋值（四种方式全部实测无效），Image 仍按内容 **16:9 单方向自适应**。

实测数据：窗口 `RenderBorder=1493x747` 时，`RenderImage` 只有 `1327x747`（左右黑框各 83px）。
Image 未撑满父容器 → 黑框区存在 → 鼠标事件区（Image）与画面显示区错位。

### 修复（Rectangle + ImageBrush 替代 Image）

```xml
<Rectangle x:Name="RenderImage"
           HorizontalAlignment="Stretch" VerticalAlignment="Stretch"
           MouseMove="..." PreviewMouseDown="..." PreviewMouseUp="..." MouseWheel="...">
    <Rectangle.Fill>
        <ImageBrush ImageSource="{Binding RenderBitmap}" Stretch="Uniform"
                    RenderOptions.BitmapScalingMode="Fant"/>
    </Rectangle.Fill>
</Rectangle>
```

- Rectangle 是纯几何控件，**必然撑满父容器**；ImageBrush.Stretch=Uniform 在内部居中留边
- `MapCoordinates` 用 Rectangle 尺寸（=Border 尺寸）按服务端宽高比计算 draw/off，
  扣除内部黑边（letterbox 数学与 WPF Uniform 完全一致）
- 保留控件 `x:Name`，全部代码引用（Cursor/GetPosition/ActualWidth/事件）零改动
- 鼠标事件绑在画面控件而非父 Border（避免黑框区越界坐标如 `y=874>832`）

### 排查工具（已内置日志）

| 日志 | 作用 |
|------|------|
| `SizeDiag: border=… image=… bitmap=… dpiScaleX=…` | **image 尺寸 vs border 尺寸**——不一致 = 黑框区存在 |
| `SizeDiagExtra: imageMargin=…` | 确认控件无意外 Margin |
| `MapDiag: ctrl/sz/scr/draw/off/px/py/out` | 映射全流程，**draw/off 显示黑边扣除**是否生效 |
| `Click down local=(x,y) size=WxH lastEcho=(x,y)` | 点击坐标与最近回显对比 |
| 两端启动 `EasyRDP Server/Client version` + `System DPI` | 确认二进制版本与 DPI 缩放 |

### 数学验证要点

```
aspect = screenW / screenH            # 服务端宽高比
if (controlW/controlH > aspect)       # 控件比画面宽 → 左右黑边
    drawW = controlH * aspect; offX = (controlW - drawW)/2
else                                  # 控件比画面高 → 上下黑边
    drawH = controlW / aspect; offY = (controlH - drawH)/2
serverX = (localX - offX) / drawW * screenW   # 钳制 px∈[0,drawW]
serverY = (localY - offY) / drawH * screenH
```

- 映射用**纯 DIP 比例**，不受 DPI 缩放影响（实测客户端 150%、服务端 100% 均正确）
- `lastEcho` 与 `mapped` 的差值 = 鼠标移动量（非偏移），勿误判为 bug
- 黑框（letterbox）是窗口宽高比 ≠ 16:9 的正常现象，**关键是映射正确扣除黑边**，无需消除黑框

### 6 步快速排查

1. **SizeDiag**：image 尺寸 == border 尺寸？（不等 → 黑框区）
2. **MapDiag**：draw/off 是否正确扣除黑边？
3. **Click down**：local → mapped 数学是否准确？
4. **System DPI**：两端 DPI 值（确认缩放因素）
5. **事件绑定**：绑在画面控件而非父 Border？
6. **bitmap 尺寸**：WriteableBitmap == 服务端帧尺寸？

---

## 2. ZRLE 流控死锁（画面传输一会后冻结）— 已根治

### 症状

握手成功、首帧显示后，画面不再更新（冻结）。服务端日志 `encoded=1` 后无新帧，
`capture queue full` 持续堆积。

### 根因链

1. 客户端 `FramebufferUpdateRequest` 曾用**空 payload**（`new byte[0]`），被服务端
   `MessageReassembler.OnFragment` 的 `fragDataLen <= 0` 保护静默丢弃（Keepalive 同受此害）
2. 服务端 `EncodeLoop` 流控下三个并发缺陷叠加：
   - `_clientRequestPending` 在取帧前重置 → 帧被"丢弃过期帧"逻辑丢帧时请求被浪费
   - `Monitor.Wait` 超时后 `continue` 空转（永不去取帧编码）
   - 丢弃过期帧逻辑每次只取 1 帧，队列持续积压时**永远丢弃、永不编码**

### 修复

- 客户端请求改 **1 字节占位 payload**（绕过空分片保护）+ 250ms 心跳请求
- 服务端 `MessageReassembler.OnFragment`：`fragDataLen < 0` 才拒绝（允许空控制消息送达）
- `EncodeLoop`：超时不再 continue（保底 1 FPS）、请求消费移到确认编码后（1:1）、
  流控模式"取尽队列保留最新帧"（请求不能浪费）
- `_clientRequestPending` 读写纳入同一 `_lock`（消除覆盖窗口）
- 首帧（`_framesEncoded==0`）无条件推送（防握手后双方互等黑屏）

### 排查工具

- 服务端 `flow-wait begin/end`（waitMs：<100ms=请求及时，~1000ms=超时保底）
- 客户端 `OnFramebufferUpdateRequest`（每 100 次打印：请求是否到达）
- 客户端 `MouseMove local=… mapped=…`（映射日志）
- 两端启动版本日志（确认部署的二进制含修复）

### 快速判断

- `flow-wait end waitMs≈1000` 且无请求日志 → 客户端没发请求（检查 payload 是否为 1 字节）
- 有请求日志但 `flow-wait` 无响应 → 服务端 EncodeLoop 卡死（检查取帧/丢弃逻辑）
- 部署后日志无 `EncodeLoop iter=1 dequeued` → 服务端是旧构建（检查 git 提交与部署文件）

---

## 3. 常见正常现象（勿误判为 bug）

| 现象 | 原因 | 处理 |
|------|------|------|
| `Decode mailbox overwritten`（启动 1 次） | 握手期服务端推多帧而渲染线程未就绪 | 无需修改 |
| `10053 连接被中止` | 用户主动断开 | 正常 |
| 全屏/非全屏黑框 | 窗口宽高比 ≠ 16:9 的 letterbox | 正常（映射已正确扣除） |
| `aly update error: 系统找不到指定的文件` | aly 自动更新器未部署 | 已降为 Debug |
| 偶发 `all capture buffers busy` 丢帧 | 大变化帧编码尖峰（ZRLE 全瓦片 239ms） | 捕获缓冲已 4→6 提高容限 |

---

## 4. 通用排查原则

1. **先确认二进制版本**：两端启动日志的 `EasyRDP Server/Client version`（含 exe 构建时间）
   与修复特征常量（`flowControlFix`/`requestPayloadFix`）——"现象依旧"最常见原因是部署旧构建
2. **日志数学验证**：映射/坐标类问题先用日志数据手动验证数学（如 `local→mapped` 比例）
3. **部署注意**：Windows 进程运行中 exe 被锁定，需先完全退出旧进程再覆盖
4. **诊断日志降频**：排查期加高频日志，定位后必须降频（每帧落盘 IO 会拖慢编码线程导致卡顿）
