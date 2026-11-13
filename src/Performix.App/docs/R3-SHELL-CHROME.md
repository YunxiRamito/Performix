# R3 — shell chrome + window behaviour

日期：2026-11-13 05:0x–05:1x（本机 Acer Predator PH317-55，150% 缩放 = 144 DPI）
范围：`app\experiments\Predex.WinUI` 的壳（标题栏 / 版本号 / 拖拽 / 二级页高度 / 展开箭头）。
红线：`app\Fork\**`、`PreySense.Core`、`PreySense.Tests`、`tools\**` 未动；没点任何 Apply / 模式 tile /
GPU tile（没有任何 EC / powercfg / NVAPI 写入）。

## 0. 一句话结论

五条改造全部落地并真机验证：标题栏换成 Predator 标志 + `PredeX`（版本号从标题栏消失），
版本号变成展开态左下角的 11 Muted `v3.0.8`，窗口不再能被拖拽，
二级页窗口统一封顶 **520 DIP**（内容超出页内 `ScrollViewer` 滚动），展开箭头命中区 **40×40 DIP** 且有 hover。
`-t:Rebuild` **0 warning / 0 error**。

## 1. 图标用的是哪个

- 搜索了 `app\branding\`、`app\experiments\Predex.WinUI\Assets\`、`decompiled\`（官方资源）、
  `predator-sense-ref\`：
  - `decompiled\app_icon.ico`（444 KB，官方 Predator Sense 应用图标，17 帧到 256 px）——含爪形标志，但整块是**深色底板**；
  - `decompiled\images\100\predatorlogo.jpg`（官方 claw + PREDATOR 字标，黑底）；
  - `predator-sense-ref\...\tray\predator-sense-tray-*.png`（白爪透明，第三方参考项目资源）。
- 本机标题栏底色实测 `#000D16`（Mica BaseAlt + 深色壁纸），官方深色底板图标在 16 DIP 下会糊成一块黑。
- **采用**：`Assets\appicon.ico`（应用一直在用的 Predator 爪形标志，蓝底），转成 48 px PNG
  `Assets\predator-mark.png`（48 px 源使 150%/200% 缩放仍清晰；只做格式转换，没有重画任何像素）。
  `app\branding\predex-logo.png` 是同一枚标志的 2048 px 版本，可作同源依据。

## 2. 改动清单

| 文件 | 改动 |
|---|---|
| `Shell\ShellWindow.xaml` | 标题栏：`▲` Path → `<Image Assets/predator-mark.png>` 16×16（L62-65）；`PREDEX` → `PredeX`（L66）；删掉版本号 TextBlock 与其 tooltip（原 L60-61）；`DragRegion` → `TitleBarLeft` 且去掉 `PointerPressed`（L49）；展开箭头 `Width/Height=40`、右外边距 6→0（L107-108）；新增 `VersionFooter`（L131-138，Row 1、左下、11 Muted、`Margin=12,0,0,3`、默认 Collapsed） |
| `Shell\ShellWindow.xaml.cs` | 新常量 `SecondaryPageMaxHeightDip = 520`（L83）；删 `SetTitleBar(DragRegion)`（原 L203）与 `DragRegion_PointerPressed`、`ReleaseCapture/SendMessage` P/Invoke、`WM_NCLBUTTONDOWN/HTCAPTION`；`SecondaryPageHeightDip()` 增加 520 DIP 封顶 + 每页一行 `layout:` 日志（L469-502）；`UpdateChrome()` 里 `VersionFooter.Visibility = home && _isExpanded`（L624） |
| `Predex.WinUI.csproj` | 新增 `Content Include="Assets\predator-mark.png"`（L101-103，复制到输出目录） |
| `Assets\predator-mark.png` | **新增**：由 `Assets\appicon.ico` 的 48 px 帧转 PNG（带 alpha，四角透明） |
| `docs\r3-verify.ps1` | **新增**：截图 + 高度 + 无拖拽 + 命中区 + hover + 合成图脚本 |
| `docs\r3-*.png` | 见 §4 |

## 3. 真机实测（`docs\r3-verify.ps1`，150% 缩放）

| 项 | 实测 |
|---|---|
| Home 折叠 | 480×174 px = **320×116 DIP**，右下 (2536,1344) |
| Home 展开 | 480×420 px = **320×280 DIP**，右下 (2536,1344) |
| 版本号 | 展开态 UIA 里读到 `TxtVersionFooter` = `v3.0.8`，距左 12 DIP、距底 3 DIP；折叠态 UIA 不存在该元素 |
| 标题栏拖拽 | 真鼠标按下并移动 (−120,−80) → 窗口 rect 不变（`dx=0 dy=0`） |
| 展开箭头 | UIA bounding rect **40×40 DIP**；hover 前后按钮区域平均亮度 14.23 → 43.08（`r3-expand-hover.png`） |
| Settings | `content=510 wanted=550 cap=520 workArea=912 DIP → 520 DIP`，实测 480×780 px = 320×520 |
| Tuning | `content=888 wanted=928 → 520 DIP`，实测 320×520 |
| Lighting | `content=803 wanted=844 → 520 DIP`，实测 320×520 |
| 三页右下角 | 全部 (2536,1344)，与首页同一条锚线 |
| 页内滚动 | 鼠标滚轮 −4 格后内容移动，右侧 ScrollBar 拇指出现（`r3-*-scrolled.png`） |

高度过渡仍是 220 ms `cubic-bezier(0.16,1,0.3,1)`、锚右下（`AnimateWindowSize` 未改）。

## 4. 产物

`docs\r3-home-collapsed.png`、`r3-home-expanded.png`、`r3-settings.png`、`r3-tuning.png`、`r3-lighting.png`，
以及 `r3-desktop-cap-520dip.png`（三页桌面合成：同高 520 DIP、同右下角、红虚线 = 封顶线）、
`r3-desktop-{settings,tuning,lighting}.png`、`r3-{settings,tuning,lighting}-scrolled.png`、
`r3-expand-nohover.png` / `r3-expand-hover.png`。

## 5. 没做 / 有代价的地方

1. **版本号只在「Home + 展开」出现**。折叠态页面区只有 76 DIP（内容 74），二级页内容贴到窗口底边，
   两处都塞不进一行 11 号字；`v3.0.8` 放在首页展开态底部那 17 DIP 空白带里（内容 235 / 页面 240）。
2. **520 DIP 是整窗高度**（标题栏 40 + 页面体 480）。若按「正文 520」理解，窗口会是 560 DIP；
   取整窗封顶是为了满足「高度 ≤ 520 DIP」的验收口径，日志里同时打印了 `content/wanted/cap/workArea` 三方数值可核对。
3. **`Window.Title` 仍是 `Predex`**（任务栏/日志里不可见，`Windows` 的 Title 属性没改）。改它会让
   `s1-verify.ps1` / `r2-verify.ps1` 里 `Title -eq 'Predex'` 的窗口枚举失效，且对用户不可见，故未动；
   用户看到的品牌文字已经是 `PredeX`。
4. `Shell\ShellWindow.xaml.cs` 里另一个 agent 的按键订阅点（`_services.ModeKeyPressed` 等）没碰，
   也没碰 `Services\KeyboardHookService.cs` / `App.xaml.cs`。
