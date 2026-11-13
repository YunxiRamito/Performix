# R6 — 模式切换确认浮层（ModeOsdWindow）

> 实现：`docs\TOAST-ANIMATION-SPEC.md`（布局 / 进场 / 停留 / 退场 / 降级 / 验收判据）
> + `docs\ICON-MICRO-ANIMATIONS.md`（五个模式的图标出场微动效，取代 TOAST 规范 §5 的旧关键帧）
> 代码：`Views\ModeOsdWindow.xaml`、`Views\ModeOsdWindow.xaml.cs`
> 取证脚本：`docs\osd-verify.ps1`；截图：`docs\osd-*.png`

## 1. 它是什么

模式切换**成功**（EC 已确认，`MainViewModel.NotifyConfirmedPowerMode`）时，在屏幕工作区底部居中
弹出一个 48 DIP 高的圆角卡片：左侧是该模式的图标（20×20，`#3B82F6`），右侧是模式名（14 px
SemiBold，走 `LocalizationService` / `Loc.T`）。停留 3 s 后淡出。

- 独立顶层窗口（不是壳里的 InfoBar）：壳隐藏到托盘时它也能显示。
- 无边框、`WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_LAYERED`、置顶、不进任务栏、不抢焦点。
- **复用同一个 HWND**：第一次模式确认时创建，之后每次只换字形 + 文字并重放动效。
- 失败路径没变：`ContentDialogUserPromptService`（模态）/ `ShellWindow.ShowToast`（InfoBar）继续负责
  错误与通知，成功路径不再碰它们。

## 2. 与规范的两处结构性偏离（必须知道）

### 2.1 宿主窗口 = 卡片尺寸，进场改为移动窗口

规范 §1 写「窗口高度 = 卡片高 + 32，给位移留空间」。实测：**WinUI 3 顶层窗口不支持逐像素透明**，
窗口比卡片大时，卡片外那 32 DIP 会渲染成不透明黑块（第一版就是这样，用户看到浮动黑条）。
`Background="Transparent"` + 无 SystemBackdrop 时，客户区由宿主窗口的不透明表面兜底，截图像素
实测为 `(0,0,0)`，桌面本身是 `(9,9,9)`。

因此改成：

| 项 | 规范 | 实现 |
|---|---|---|
| 窗口尺寸 | 卡片 + 32 DIP 高 | **正好等于卡片**（实测 160×48 DIP @150% = 240×72 px） |
| 进场位移 | 卡片 `Translation.Y` +24 → 0，250 ms | **移动窗口帧**：`AppWindow.MoveAndResize` 逐帧插值，Y = 终点+24 DIP → 终点，250 ms `cubic-bezier(0,0,0,1)` |
| 退场位移 | 卡片 `Translation.Y` 0 → +16，167 ms | **移动窗口帧**：Y → 终点+16 DIP，167 ms `cubic-bezier(0.7,0,1,0.5)` |
| 卡片 `Opacity` | Composition 动画 0→1 / 1→0 | **窗口 constant alpha**（`SetLayeredWindowAttributes` + `LWA_ALPHA`），167 ms / 83 ms(降级) |

为什么这样：窗口就是卡片时，整张图（含卡片背景）是一体的，窗口级 alpha 才能让「卡片淡入淡出」
叠在桌面上；如果只动画卡片 Visual 的 Opacity，淡入过程会把卡片画在窗口的黑色底上，先出现一块
深色底再亮起来。位移驱动用 `CompositionTarget.Rendering`（vsync）+ 16 ms 定时器兜底，进度取自
`Stopwatch` 的真实耗时 —— 早期的「按帧计数」实现在这台机器上把 250 ms 跑成了 ~490 ms，超出
规范 ±1 帧的容差。

### 2.2 ThemeShadow 去掉了

规范 §1 要求 `ThemeShadow`、`Translation Z = 32`。窗口贴死卡片后，阴影没有可以投的表面；把它
留在窗口里只会被窗口裁掉。**现在只有 1 px 描边（`SurfaceStrokeColorFlyoutBrush`）+ DWM 圆角
（`DWMWCP_ROUND`）+ 卡片自身 8 DIP 圆角**。如果以后要阴影，得先解决透明窗口（见 2.1）。

## 3. 图标微动效（ICON-MICRO-ANIMATIONS）

按该文档 §1 枢轴 / §2 上限与缓动 / §3 逐图标关键帧实现，全部作用在 `FontIcon` 的 Visual 上：

| 模式 | 时长 | 枢轴 | 动作 |
|---|---|---|---|
| 节能 Eco | 560 ms | (0.49,0.70) 叶柄 | 飘落 + 逐渐衰减的摆动（Rotation -6 → +3 → -1.2 → 0） |
| 静音 Quiet | 600 ms | (0.50,0.50) | 下沉过冲后回位 + 一次呼吸（Scale 0.96 → 1.02 → 1） |
| 均衡 Balanced | 440 ms | (0.50,0.50) | 横向滑入 + 刻度卡位（-1.5 → +0.3 → -0.1 → 0），不旋转 |
| 性能 Performance | 520 ms | (0.47,0.58) 指针根部 | 扫入 -12° → +2° → -0.5° → 0 |
| 狂暴 Beast | 340 ms | (0.49,0.47) | 缩小→冲大 1.08→回落 + 弹簧位移 (0.8,-1.2) ζ0.5 T90ms |

- 全局上限照 §2.3：|Translation| ≤ 1.5 DIP（狂暴初始模长 1.44）、Rotation ≤ 12°、Scale 在表内范围；
  狂暴硬顶 Scale ≤ 1.10（取 1.08）、≤ 360 ms（取 340 ms）、Opacity 单次 0→1 不闪。
- 结束态在 `ScopedBatch.Completed` 里显式写回恒等变换（§2.5）。
- 83 ms 延迟沿用 TOAST 规范 §2「图标动效延迟 83 ms 后开始」。
- **与页面瓷砖的区别**：文档 §2.1 说「每个页面实例只播一次」，那是给模式按钮的；浮层的职责就是
  每次切模式演示一次，所以每次 Show 都播，但每次都从复位状态开始（不叠加残留）。
- **已知观感差异（文档 §1 明说的限制）**：单字形只能整体变换，性能模式转的是整个字形，圆弧和
  刻度会跟着指针一起转；枢轴接近弧心，所以读感仍像指针扫动。要只动指针得把指针拆成独立图层。

## 4. 实测数据（本机 2560×1440 / 150% / 深色主题）

`pwsh -NoProfile -File docs\osd-verify.ps1 -Exe ..\..\dist-winui\Predex.exe` 输出（PASS）：

| 项 | 实测 | 规范 |
|---|---|---|
| 卡片尺寸 | 160×48 DIP（狂暴，最短宽度） | 48 DIP 高，宽 160–280 |
| 卡片底边距工作区底边 | 24 DIP | 24 DIP |
| 进场位移 | 窗口 Y 1296 → 1260 px = **24 DIP** | +24 → 0 DIP |
| 进场总耗时 | **256.6 ms** | 250 ms（容差 17） |
| 停留 | **3001 ms** | 3000 ± 50 |
| 退场耗时 | **178.7 ms** | 167 ms（容差 17） |
| 图标（狂暴） | 340 ms 后写回恒等态 | ≤ 360 ms，单次 |
| 降级进场 | 窗口 Y 全程 1260 px（位移 0），alpha 83 ms 线性，图标动效跳过 | §7 |

截图（全部是屏幕抓图，卡片外可见桌面/任务栏）：

| 文件 | 内容 |
|---|---|
| `osd-enter-080ms.png` | 进场 +80 ms：卡片抬升中、正在淡入 |
| `osd-hold.png` | 停稳态（狂暴） |
| `osd-exit-080ms.png` | 退场中：卡片下移 + 淡出 |
| `osd-eco / quiet / balanced / performance / turbo.png` | 五个模式各自停稳态 |
| `osd-degraded-enter / hold / exit.png` | `--no-anim` 降级分支 |
| `osd-real-switch.png` | **真实路径**：点 Home 的「性能」瓷砖 → EC 确认 → 浮层显示性能 |

真实路径日志证据（`--trace`，点瓷砖后 112 ms 内确认）：

```
[13:50:..] osd: mode confirmed 0x04 '性能' → overlay
[13:50:..] osd: show mode=0x04 '性能' animations=full card=160x48 DIP at (1160,1260)
[13:50:..] osd: enter completed in 254.6 ms ...
[13:50:..] osd: dwell 3011 ms elapsed → exit
[13:50:..] osd: exit completed in 175.1 ms → host hidden
```

测完已把硬件模式从 0x04 还原为 0x06（`ProductionHardwareService ready (mode=0x06)` 复验），
并按收尾要求跑了 `Restore-AutoFan.ps1`。

## 5. 调试开关

| 开关 | 作用 |
|---|---|
| `--trace` | 写 `shell.log`：每个相位的实测耗时（进场/停留/退场/恢复 + 图标动效）、窗口 Y、卡片尺寸 |
| `--demo=osd` | 循环五个模式演示浮层（4.2 s 一圈），不碰硬件 |
| `--demo=osd:<mode>` | 只放一个模式，`<mode>` = Acer 字节（0/1/4/5/6）或 Eco/Quiet/Balanced/Performance/Turbo\|Beast |
| `--no-anim` | 模拟「设置→辅助功能→视觉效果→动画效果 = 关」，走规范 §7 降级分支 |

## 6. 没做 / 没验的规范条目（透明交代）

1. **§1 阴影**：见 2.2，未实现。
2. **§1 窗口高 = 卡片 + 32**：见 2.1，改成窗口贴死卡片。
3. **§5 旧图标关键帧**：被 ICON-MICRO-ANIMATIONS 取代（用户指派），TOAST §5 的关键帧不再存在；
   其硬顶（Scale ≤ 1.15 / |位移| ≤ 4 / ≤ 450 ms）由新表更严的数值天然满足。
4. **§8.2 流畅度**（UI 线程人为阻塞 300 ms 仍不丢帧）：未做。窗口位移只能跑在 UI 线程的消息
   循环里（Win32 窗口位置），所以本条本来也证明不了「合成线程」；图标动效与 alpha 在合成/窗口层。
5. **§8.5 一秒内连切 5 次**：未实测。代码路径已按「不重播进场、只换文字+重放图标、计时重置」
   实现，但没跑连续 5 次的仪器化验证。
6. **§8.6 退场中断截图**：未实测。恢复逻辑（从当前 alpha / 当前窗口 Y 用 167 ms 回位）已实现并在
   `--trace` 里打点，但没有截到半程中断的那一帧。
7. **§8.8 100% / 200% 缩放**：只验证了本机 150%；100%/200% 与浅色主题没跑（本机只有这一档配置）。
8. **§8.8 超长文本省略号**：`MaxLines=1 + TextTrimming=CharacterEllipsis` 且在 280 DIP 处封顶，
   但中文模式名都是 2 个字，没有真正触发过省略。
9. **§9 讲述人播报**：已调用 `AutomationPeer.RaiseNotificationEvent`（文本「已切换到{模式}模式」），
   但没有实际开讲述人听测。

## 7. 另外发现（不是本次改动引入，但值得跟）

本次 10 次左右启动里有 **1 次** 启动即崩：`Predex.exe` / `Microsoft.UI.Xaml.dll` / `0xc000027b`
（stowed exception），`crash.log` 为空、无窗口。崩之前的最后一条日志是
`compat: Dispatcher.CurrentDispatcher off the UI thread → ... MainViewModel.OnPowerModeChanged ←
ProductionHardwareService.SetPowerMode ← RestoreSavedPowerMode`，即启动恢复模式时从工作线程改了
XAML 侧状态。这次崩溃发生在浮层窗口创建之前，与本文档的改动无关，但它是「启动偶发崩溃」，
建议单独排（查 `MainViewModel.OnPowerModeChanged` 的线程亲和性）。

> **R7 已定位并修复（2026-11-13）→ 见 [R7-STARTUP-CRASH.md](R7-STARTUP-CRASH.md)。**
> 根因不是 `OnPowerModeChanged` 的线程亲和性，而是 `ShellLog.Write` 的阻塞锁把 UI 线程堵在
> `HomePage.OnLoaded` 里，等待时抽消息导致 XAML 重入失败（E_UNEXPECTED → 0xC000027B）。
> 修复：trace 日志改为 `Monitor.TryEnter` 零超时，绝不阻塞；修复后 60 次启动 0 崩溃。
