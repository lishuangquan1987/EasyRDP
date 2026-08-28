# EasyRDP 镜像驱动截屏接入设计（Mirror Driver Capture）

> 状态：设计稿（未实现）
> 关联：设计文档 D10（XP 抓屏双后端运行期选择）、`IScreenCapturer` 抽象
> 目标平台：Windows XP / Win7（XDDM 镜像驱动）；Win8+ 仍走 DXGI
> 技术路线：方案 B（微软 WDK7 示例驱动为底座）+ 参考 dfmirage 脏矩形读取协议

---

## 1. 背景与动机

### 1.1 为什么是镜像驱动

当前 EasyRDP 在 XP/Win7 上唯一的截屏方式是 **GDI BitBlt**。在无硬件加速的环境（实测 Win7 虚拟机 + Hyper-V 虚拟显卡）每帧需 **~250–300ms**，直接导致：

- 服务端 FPS 卡在 ~3–4
- 鼠标移动卡顿（画面背景刷新跟不上）

商用竞品 realvnc / radmin / UltraVNC（驱动版）在同样环境下流畅，**核心差异是截屏方式**：

| | BitBlt | 镜像驱动 |
|---|---|---|
| 捕获方式 | 定时轮询整帧 | 内核显示驱动，随绘图事件通知 |
| 变化区域 | 自行对比整帧 | 驱动提供脏矩形（dirty rects） |
| CPU | 高（每帧全屏+对比） | 极低（只读变化区域） |
| 无硬件加速环境 | 慢（实测 ~300ms/帧） | 快（读驱动缓冲，绕开 GDI 回读） |

### 1.2 为什么现在做

设计文档 `EasyRDP-Abstraction-Layers-Design.md` 的 **D10** 已规划："XP 无 DXGI，`IScreenCapturer` 提供 BitBlt 与镜像驱动双后端，运行期探测已装则用镜像、未装回退 BitBlt，接口不变，对上层透明，参照 UltraVNC / TightVNC。" 本设计把 D10 落地。

---

## 2. 架构总览

```
┌─────────────────────────────────────────────────────────────┐
│  EasyRDP.Server.Wpf (net40)                                  │
│   CaptureService ──> IScreenCapturer（构造注入，运行期选择）    │
└───────────────┬─────────────────────────────────────────────┘
                │ 实现
┌───────────────▼─────────────────────────────────────────────┐
│  EasyDesk.Windows (net40/netstandard2.0)                     │
│   WindowsScreenCapturer (BitBlt)  ← 现有一个                  │
│   DxgiScreenCapturer      (DXGI, Win8+)  ← 现有一个           │
│   MirrorScreenCapturer    (镜像驱动, XP/Win7)  ← 本设计新增     │
└───────────────┬─────────────────────────────────────────────┘
                │ P/Invoke + ExtEscape
┌───────────────▼─────────────────────────────────────────────┐
│  内核镜像驱动  MirrorDriver.sys (C/WDK7)  ← 本设计新增（独立工程）│
│   - XDDM 显示驱动：GDI 桌面绘图派发，记录脏矩形到共享缓冲         │
│   - 提供设备接口：attach、映射共享缓冲、读脏矩形/像素            │
└─────────────────────────────────────────────────────────────┘
```

**关键边界**：镜像驱动分两部分——
- **内核驱动**（`.sys` + `.inf`，C + WDK7）→ **独立驱动工程**，不在 EasyDesk 内
- **用户态捕获客户端**（C# + P/Invoke）→ **加入 EasyDesk**，新增 `MirrorScreenCapturer`

> ⚠️ EasyDesk 是纯 C#/P-Invoke 库（net40/netstandard2.0），**无法承载内核驱动**。内核驱动必须用 C + WDK 单独编译、单独签名，通过设备接口被用户态客户端访问。

---

## 3. 驱动工程（独立，不在 EasyDesk 内）

### 3.1 来源与参考

- **底座**：微软 WDK7（Version 7600）示例，`\src\video\displays\mirror\`
  - `disp\` — 镜像显示驱动（XDDM）
  - `miniport\mini\` — 最小化 miniport
  - `app\` — 用户态 `mirror.exe` 服务 + `mirror.inf`（展示如何 attach + 读镜像表面）
  - 许可证：Microsoft Sample Code（宽松，可自由使用）
- **脏矩形协议参考**：dfmirage 用户态 `MirrorDriverClient`（`getChangesBuf()`/`getBuffer()`）——TightVNC/UltraVNC 已验证的协议

### 3.2 核心机制（XDDM 镜像驱动原理）

- 作为虚拟显示设备挂到 GDI 桌面绘图层之上，GDI 把与该镜像区域相交的 2D 绘图操作**派发**给它
- 图形 DDI 回调：`DrvEnablePDEV`（逐设备初始化）、`DrvBitBlt`、`DrvCopyBits`、`DrvTextOut`、`DrvStretchBlt`、`DrvRealizeBrush`、`DrvNotify`（WM_DISPLAYCHANGE）
- 表面：`EngCreateSurface` / `EngModifySurface`（SURFOBJ，STYPE_DEVBITMAP）
- **脏矩形通知**：驱动在 `Drv*` 回调中把被写入的矩形记录到共享结构（计数器 + `CHANGES_RECORD{type, RECT}` 数组），用户态服务轮询/事件读取后编码发送——**推送的是变化区域，而非整帧位图**
- `DEVINFO.flGraphicsCaps` 置 `GCAPS_DIRECTDRAW`（dfmirage 即如此；注意不是 `GCAPS_LAYERED`——后者是分层窗口标志，与镜像捕获无关）；每 PDEV 状态（禁全局变量）；`Attach.ToDesktop`(REG_DWORD=1) 挂到全局桌面

### 3.3 需在 WDK7 示例基础上补充的实现

| 项 | 说明 |
|---|---|
| 脏矩形记录 | 示例只展示"读镜像表面"，需按 dfmirage 协议补 `CHANGES_BUF` 共享缓冲 + 驱动侧记录 |
| 共享缓冲映射 | `dmf_esc_usm_pipe_map` ExtEscape 等价物：用户态映射驱动内存 |
| 设备控制接口 | attach / detach / 查询脏矩形 / 取像素的 IOCTL 或 ExtEscape |
| `.inf` | 声明镜像显示驱动类别 + `Attach.ToDesktop` 注册表键 + 签名 |

### 3.4 工具链与环境

- **需要 WDK7（Version 7600）**（当前环境未安装，需先安装）
- 构建：WDK 7 的 build 环境（`setenv` / NMake 或 VS2008 驱动工程）
- 驱动签名：
  - **XP 32 位**：不需要强签名（XP x64 需签名）
  - **Win7**：需要测试签名（测试模式 `bcdedit /set testsigning on`）或 WHQL 签名（正式发布）
- 仅支持 `< Win8`（Win8 起系统拒绝安装镜像驱动，转用 DXGI/IDD）

---

## 4. EasyDesk 客户端（本设计在仓库内落地的主要部分）

### 4.1 新增文件

```
EasyDesk/src/EasyDesk.Core/IScreenCapturer.cs   （可能小改，见 §4.3）
EasyDesk/src/EasyDesk.Windows/MirrorScreenCapturer.cs   （新增）
EasyDesk/src/EasyDesk.Windows/WindowsDesktopFactory.cs   （改，接入探测）
EasyDesk/src/EasyDesk.Windows/NativeMethods/（驱动访问 P/Invoke，按需）
```

### 4.2 `MirrorScreenCapturer : IScreenCapturer`

实现现有 `IScreenCapturer` 全部方法：

| 方法 | 实现策略 |
|---|---|
| `CaptureScreen()` / `CaptureScreen(options)` | 从镜像缓冲读整帧（attach 后完整拷贝） |
| `CaptureRegion(x,y,w,h)` | 从镜像缓冲读指定区域 |
| `CaptureScaled(...)` | 读区域后由调用方/本类缩放到目标尺寸 |
| `GetPrimaryScreen()` / `GetAllScreens()` | 复用现有 `WindowsDesktopInfo` 逻辑 |
| 新增（见 §4.3） | `TryReadChanges(out ScreenRect[] rects)` 读脏矩形 |

**生命周期**：
- 构造：加载驱动设备，尝试 attach；失败时抛异常（由工厂捕获回退 BitBlt）
- 每次捕获：读共享缓冲的计数器与 `CHANGES_RECORD`，仅对变化矩形取像素
- `Dispose`：detach + 关闭设备句柄

### 4.3 ⚠️ 脏矩形接口的关键设计决策（需确认）

**问题**：现有 `IScreenCapturer` 以"整帧"为中心（`CaptureScreen` 返回完整 `ScreenFrame`），**没有脏矩形接口**。镜像驱动的核心价值在增量脏矩形，若只用整帧接口，等于把镜像驱动的优势浪费掉。

**方案选项**：

- **方案 X（推荐）**：新增独立可选接口 `ICaptureChangesReader`（含 `bool TryReadChanges(out ScreenRect[] rects)`）。`IScreenCapturer` 本身不变（net40 无接口默认方法，保持向后兼容），BitBlt/DXGI 不实现该接口，镜像驱动实现之。`CaptureService` 用 `capturer as ICaptureChangesReader` 检测，有脏矩形则只编码变化区域。**接口向后兼容**（现有实现与调用方不受影响），通过"新增可选接口"而非"修改现有接口"实现，**不违背 D10 "接口不变" 的实质**（D10 指的是 `IScreenCapturer` 主接口不变）。
- **方案 Y（保守）**：保持接口完全不变，镜像驱动也走整帧 `CaptureScreen`。实现最简单、严格符合 D10，但**拿不到镜像驱动的脏矩形收益**，只剩"读驱动缓冲比 BitBlt 快"的优势。

> 建议采用 **方案 X**：镜像驱动的 90% 价值在脏矩形增量，仅靠"读缓冲快"提升有限。是否接受 `IScreenCapturer` 接口小改（新增可选方法），需你拍板。

### 4.4 工厂接入（`WindowsDesktopFactory.CreateScreenCapturer`）

```csharp
public IScreenCapturer CreateScreenCapturer()
{
    // 1. Win8+ → DXGI（现有逻辑）
    // 2. XP/Win7 → 尝试镜像驱动（探测是否已装驱动）
    //    已装 → new MirrorScreenCapturer()
    //    未装/加载失败 → 回退 new WindowsScreenCapturer() (BitBlt)
}
```

- 探测：尝试打开镜像驱动设备，成功即用；失败捕获异常回退
- 与 D10 规划一致，对上层（CaptureService）透明

### 4.5 诊断

`SystemInfoCollector.DetectCaptureMethod` 增加 `CaptureMethodMirror` 枚举值（如 =2），连接详情面板显示采集方式，便于确认走了镜像驱动。

---

## 5. 与现有管线对接

- `CaptureService` 持有 `IScreenCapturer`（构造注入），镜像驱动通过工厂替换注入；**整帧路径下无需改**——工厂在 `CaptureService` 外部完成后端选择
- 若采用方案 X（脏矩形增量），`CaptureService`/`ServerStreamSession` 需改为优先尝试 `TryReadChanges`，把变化区域传给编码器（ZRLE 增量编码，天然契合）——此路径需要改这两处
- 光标仍由 `CursorTracker` 叠加层独立同步（不受镜像驱动影响）

---

## 6. 风险与权衡

| 风险 | 影响 | 缓解 |
|---|---|---|
| 内核驱动开发复杂度高 | 工作量最大的一块 | 以 WDK7 示例为底座，参考 dfmirage 协议 |
| 驱动签名（Win7） | 正式环境装不上 | XP 免签；Win7 测试模式/WHQL |
| 镜像驱动不更新（Win8+ 弃用） | 长期维护 | 明确仅 XP/Win7 用；Win8+ 走 DXGI |
| D10 "接口不变" 被打破（方案 X） | 接口兼容性 | 可选方法向后兼容，现有实现不受影响 |
| GPL 污染 | 法律风险 | 不引入 dfmirage/TightVNC/UVNC 源码，仅参考协议思想 |
| 未装 WDK | 无法编译驱动 | 先落地 EasyDesk 客户端 + 出驱动代码，WDK 安装后编译 |
| 脏矩形环形缓冲溢出 | 变化过快时区域丢失，画面残留 | 驱动端用计数器区分"丢区域/丢整帧"语义：溢出时标记全屏需重发，客户端读到标志回退整帧；或加大缓冲/合并区域 |

---

## 7. 落地里程碑

- **M0（当前）**：设计定稿（本文档）
- **M1**：安装 WDK7，编译 WDK mirror 示例跑通（验证工具链 + 驱动可装载）
- **M2**：EasyDesk `MirrorScreenCapturer` 客户端骨架（attach + 读整帧），工厂接入，运行期探测回退
- **M3**：脏矩形读取（方案 X 需先确认接口）——驱动补 `CHANGES_BUF` + 客户端 `TryReadChanges`
- **M4**：与 CaptureService / ZRLE 增量编码对接，端到端验证
- **M5**：XP + Win7 兼容性验证，驱动签名方案落地

---

## 8. 待确认事项

1. **脏矩形接口**：采用方案 X（新增 `TryReadChanges` 可选接口，拿到增量收益）还是方案 Y（接口完全不变，仅读缓冲快）？若选方案 X，需同步修订 `docs/EasyRDP-Abstraction-Layers-Design.md` 的 D10 条目（原文"接口不变"已不成立，改为"新增可选脏矩形接口，向后兼容"）。
2. **驱动签名**：目标环境是否只有 XP（免签），还是必须覆盖 Win7 正式签名？
3. **WDK 安装**：何时安装 WDK7（决定驱动能否实际编译）？
