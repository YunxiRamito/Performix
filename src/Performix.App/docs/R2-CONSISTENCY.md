# R2 — 跨页视觉一致性 + 交互改造报告

日期：2026-11-13 04:56（本机 Acer Predator PH317-55，150% 缩放 = 144 DPI）
范围：`app\experiments\Predex.WinUI`（`Pages\` / `Styles\` / `Controls\` / `Shell\` / `Services\` / `docs\`）
红线：`app\Fork\**`（WPF 在产版）、`PreySense.Core`、`PreySense.Tests`、`tools\**`、`Predex.WinUI.csproj` **一个字节没动**；没有点过任何 Apply / 模式 tile / GPU tile（没有任何 EC / powercfg / NVAPI 写入）。

本文件是两轮评审意见的落地记录：
- **第一轮**（task-7，6 条跨页一致性）
- **第二轮**（Claude Code 读图，8 条，给的数值更具体；与第一轮重叠的按第二轮数值改）
- **用户追加的 3 条交互改造**（二级页自动拉高窗口 / 删三个常规按钮 / 点别处收起）

---

## 0. 一句话结论

八个视觉问题全部改完，**改法集中在 `Styles\Resources.xaml` 一份样式里**，页面只保留真正页面特异的部分；
三条交互改造全部实现并在真机验证：二级页窗口按内容自适应变高（550 / 880 / 765 / 844 DIP），
标题栏三个常规按钮删除后**拖拽仍可用**、**Esc 在折叠/展开两态都能收起**、**托盘图标再点一次能收**、
**失焦自动收起在折叠态确认生效**。`-t:Rebuild` **0 warning / 0 error**。

---

## 1. 共享样式（先改样式，再改页面）

`Styles\Resources.xaml` 新增/收敛的东西（页面里不再出现私有副本）：

| 新增/修改 | 作用 | 原来散在哪 |
|---|---|---|
| `GapInline/GapTight/GapGroup/GapBlock`（4/6/12/16）、`TileHeight`（52） | 间距与 tile 高度的唯一来源 | 各页写 `Spacing="6"/"8"/"4"`、tile 各写 `Height="52"` |
| `PageSubtitleStyle`（11 Regular Secondary，Wrap） | 页面唯一一行副标题 | Tuning 用 Tertiary、Lighting 用 `BodyTextStyle` |
| `SectionTitleStyle`（12 SemiBold Secondary，**无前置图标**） | 唯一卡片标题样式 | Tuning/Lighting 另写入 12 SemiBold + `TextFillColorSecondaryBrush`（更白）+ 装饰图标 |
| `CardValueTextStyle`（12 SemiBold Primary，Tabular） | 卡片标题行右侧数值的唯一样式 | Tuning 11 Muted、Lighting 14 SemiBold Primary |
| `UnitTextStyle`（11 Regular Muted，底部对齐） | 主数值后面的单位（`RPM`/`°C`） | 原来单位跟数值挤在一个 14px run 里 |
| `ModeTileButtonStyle` + `ModeTileStackStyle` + `ModeTileIconStyle` | **唯一的模式选择器控件外观**（图标上/文字下、选中 `#263B82F6` 底 + `#3B82F6` 1px 描边） | 首页一套、调校页另一套（横排 30 高 chip + 每模式彩虹描边） |
| `ChipButtonStyle`（30 高、`MinWidth 44`、`Padding 10,0`、圆角 4） | 开关型 chip（"自动刷新"） | 原来只是 `EntryButtonStyle` + 蓝色文字 |
| `CardStyle` 的 `Padding` = `{StaticResource GapGroup}`（12） | 卡片内边距唯一来源 | 灯效页检测卡私自 `Padding="8"`，设置/调校各有一份 `*CardStyle` 副本 |
| `ThemeDictionaries`（Default/Dark/Light）覆盖 accent 体系 | 全应用唯一 accent = `#3B82F6` | WinUI 默认 accent 在本机解析成近中性灰 |

**为什么标题不带图标**：图标会在标题前吃掉 20 DIP，标题文字因此比卡片内第一行标签右偏 20 DIP。
这正是第 6 条「设置卡标签起点 ~36px」与灯效卡「三个左边缘各不同」的根因；去掉图标后标题/标签/正文/数值
全部落在卡片内容起点同一条竖线上（实测：设置页标题 14.0 DIP vs 行标签 13.3 DIP，灯效卡标题与描述均 13.3 DIP）。

**为什么 accent 要在资源里覆盖**：本机 WinUI 默认 accent 让 `ToggleSwitch` 的"开"和 `Slider` 的已填充段
变成浅灰底 + 黑滑块，"开"和"关"颜色一样。在资源 `ThemeDictionaries` 里把
`SystemAccentColor*` / `AccentFillColor*` / `ToggleSwitchFillOn*` / `ToggleSwitchStrokeOn*` /
`ToggleSwitchKnobFillOn*` / `SliderTrackValueFill*` / `SliderThumbBackground*` / `AccentButton*` /
`ProgressBarForeground` 全部钉到 `#3B82F6`，三个主题槽位都写。截图里开关和滑块现在都是同一个蓝。

---

## 2. 第二轮评审 8 条 → 改动与证据

| # | 评审意见 | 改法 | 证据 |
|---|---|---|---|
| 1 | 调校页 `4109 RPM` 被卡片右边缘截断、无省略号 | 状态卡改 **2×2 网格**（每格 ~132 DIP）；值 14 SemiBold `Tabular`，**单位拆成 11 Regular Muted、间隔 4、底对齐**（新增 `Controls\TelemetryText.cs`，`{x:Bind controls:TelemetryText.Value/Unit(Vm.CpuFanText)}`，VM 不动） | `docs\r2-tuning.png`：`4109 RPM` / `4054 RPM` 完整，右侧仍有 ~100 DIP 余量 |
| 2 | 正文重复大标题，白扔 ~50 DIP | **删掉调校/灯光页正文大标题**，只留一行副标题（11 Regular Secondary）；设置页只补副标题、不加正文标题；风扇页同样补副标题 | `r2-tuning.png` / `r2-lighting.png` / `r2-settings.png` / `r2-fans.png` 四页顶部都是「标题栏页面名 + 一行副标题」 |
| 3 | 强调色不统一（开关的"开"、滑块的已填充段是浅灰） | 资源里覆盖整套 accent（见 §1） | `r2-settings.png`：开机启动=灰（关）、Acer 服务/电池限制=蓝（开）；`r2-lighting.png`/`r2-tuning.png`：滑块已填充段是蓝 |
| 4 | 性能模式两套控件；区头右侧重复写「均衡」 | 调校页 **复用 `ModeTileButtonStyle`**（52 高、图标上文字下、未选中也有 `#14FFFFFF` 底色 → 看得出可点），选中态与首页完全一致；**删掉区头右侧的 `TxtActiveModeName`**（tile 自身已表达选中） | `r2-home-expanded.png` 与 `r2-tuning.png` 的模式行逐像素同款；两张图里都是「均衡」tile 高亮、区头不再有「均衡」 |
| 5 | "自动刷新"像超链接、看不出开关状态 | 改 `ChipButtonStyle`：30 高、`MinWidth 44`、`Padding 10,0`、11 SemiBold、圆角 4；**开=accent 实底+白字+accent 描边，关=中性底+次要灰字+1px 描边**；自定义模板保留 `TemplateBinding Background`，避免默认模板 hover 把状态色刷掉 | `r2-home-expanded.png`：`自动刷新` 是白底灰字的 chip（当前关闭） |
| 6 | 卡片左内边距不一致（8 vs 12、chip 多缩进 4）、描述还是英文 | 检测卡去掉 `Padding="8"` 用 `CardStyle` 的 12；设备 tab 改成**带底色的 chip**、`Margin` 左 0（chip 盒子落在内容起点，文字内缩 8 是 chip 自身内边距）；描述走 `Loc.T(Vm.LightingDeviceStatus)` 并在 `WinUIStrings` 补中文 | `r2-lighting.png`：标题 / chip 盒子 / 中文描述 `键盘 4 个独立分区。` 三条左边缘对齐 |
| 7 | 标题栏 `81°C 56°C` 没标 CPU/GPU | 标题栏改成 `▲ PREDEX v3.0.8 │ CPU 81°C  GPU 56°C`：`CPU`/`GPU` 用 11 Muted 标签、数值 11 Secondary + Tabular；**两个温度值统一成同一档**（原来第一个比第二个亮） | `r2-home-expanded.png` 标题栏；`shell.log` 里 `cpu=…/4109 RPM gpu=…/N/A` 证明是两个独立属性 |
| 8 | 固定宽窗口不该有最大化按钮 | **三个常规按钮全删**（最小化/最大化/关闭）→ 右侧只剩展开箭头；`OverlappedPresenter.IsMaximizable = false`（`IsMinimizable` 也置 false）；最大化/还原代码（`_customMaximized` / `UpdateMaximizeGlyph` / `MaximizeButton_Click`）整体删除 | `r2-home-expanded.png` / `r2-fans.png` 标题栏只剩一个 ⌃/⌄；`r2-verify.ps1` 拖拽与 Esc 用例 |

### 第一轮（task-7）6 条的去处

| task-7 # | 第二轮对应 | 说明 |
|---|---|---|
| 1 数值截断 | 第二轮 #1 | 按第二轮数值实现（2×2 + 单位拆分） |
| 2 模式选择器两套 | 第二轮 #4 | 统一成首页形态；未选中有底色，确实"看得出能点" |
| 3 页面头部不统一 | 第二轮 #2 | **采纳第二轮**：不加大标题，只留副标题（省 ~50 DIP） |
| 4 卡片内左边距 | 第二轮 #6 | 卡片 12 + 去标题图标 + chip 归位 |
| 5 首页栅格/行距 | —（第二轮没重复提） | 见下 |
| 6 标题/右侧数值视觉重量 | 第二轮 #4 的"删重复值" | 卡片标题统一 `SectionTitleStyle`，右侧值统一 `CardValueTextStyle` |

**首页第 5 条的实测结果**（第二轮仍认可首页 tile 形态，所以只做了栅格与行距）：

- 行距：整页一个 `StackPanel Spacing="{StaticResource GapTight}"`（6），转速行与三条分隔线**不再带各自的
  `Margin`**，展开区内部间距全部由这一个 `Spacing` 决定（原来转速行上下分别是 6 和 ~13，视觉上"贴着上排"）。
- 列栅格：5 上排（模式）与 5 下排（入口）用同一个 `ColumnSpacing`，列线重合；4 列的显卡排**占满整行等分**，
  左右外沿与上排同一条内容盒。实测同一条扫描线上：模式排 22–457 physical、显卡排 20–459 physical
  （差的 2px 是两种 tile 圆角半径差导致同一扫描线截短不同，外沿本身相等）。
- 展开总高 235 DIP ≤ 页面区 240 DIP，仍在 280 DIP 窗口预算内（首页**没有** ScrollViewer，不能超）。

---

## 3. 三条交互改造

### 3.1 二级页窗口按内容自适应变高

实现在壳里，**不是每页写死常量**：

```
NavigateTo(tag, page) → CurrentTargetHeightDip() → SecondaryPageHeightDip(page)
   page.UpdateLayout()                      // 先让模板/列表项落地
   sv = 找到页面里第一个 ScrollViewer
   content = sv.ExtentHeight + sv.Padding.Top + sv.Padding.Bottom   // 与当前视口无关的内容全高
   目标 = clamp(40 + ceil(content), 200, 工作区高 - 32)
   AnimateWindowSize(目标, 220ms, 0)        // 逐帧 MoveAndResize，锚右下，cubic-bezier(0.16,1,0.3,1)
```

- `40` = 标题栏；`32` = 放置时上/下各 16 DIP 的边距；工作区高度按 `GetDpiForWindow` 换算成 DIP 后 clamp，
  **内容再长也只会撑到工作区，然后页面内部 `ScrollViewer` 滚动**，不会把窗口顶出屏幕。
- 高度动画和内容淡入**同时开始**：`PlayPageTransition()`（PageHost opacity 0→1，160ms）先起，
  紧接着 `AnimateWindowSize`；页面先以 opacity 0 挂进 PageHost 再 `UpdateLayout()`，用户看不到"先撑开空窗再填内容"。
- Home 仍是设计稿的 116 / 280：进出二级页不再强制展开首页，返回首页恢复用户原来的折叠/展开态。

实测（`shell.log` + 窗口 rect，150% 缩放）：

| 页面 | 目标高 | 实测 | 右下角 |
|---|---|---|---|
| Home 折叠 | 116 DIP | 480×174 px = 320×116 | (2536,1344) |
| Home 展开 | 280 DIP | 480×420 px = 320×280 | (2536,1344) |
| Settings | 550 DIP | 480×825 px = 320×550 | (2536,1344) |
| Tuning | 880 DIP | 480×1320 px = 320×880 | (2536,1344) |
| Fans | 765 DIP | 480×1148 px = 320×765 | (2536,1344) |
| Lighting | 844 DIP | 480×1266 px = 320×844 | (2536,1344) |

页面切换高度证据（同一张壁纸、右下角对齐）：`r2-desktop-home-collapsed.png` /
`r2-desktop-home-expanded.png` / `r2-desktop-fans.png` / `r2-desktop-lighting.png`。

### 3.2 删掉标题栏三个常规按钮

- XAML 里 `BtnMinimize` / `BtnMaximize` / `BtnClose` 三个按钮删除，右侧只剩展开箭头（Home 才显示）；
  `SetTitleBar(DragRegion)` 保留，标题栏仍是拖动区。
- `OverlappedPresenter.IsMaximizable = false`（另外 `IsMinimizable = false`），最大化/还原的自绘逻辑整体删除。
- 替代出口，全部真机验证：
  - **Esc** 收起：折叠态 + 展开态各一次，`shell.log` 两次 `window: hide requested (escape)`，进程仍存活；
  - **托盘右键菜单「退出」**：菜单项→`QuitRequested`→`QuitApplication`（代码路径验证；自动化没有真点菜单，
    因为 `TrackPopupMenuEx` 是模态的，见 §5）；
  - **托盘左键再点一次收起**（新加 `TrayToggleRequested` → `TrayClicked`：可见就收、不可见就开）；
  - **点别处收起**（见 3.3）。
- **拖拽仍可用**：`r2-verify.ps1` 用真鼠标在标题栏按下并移动 → 窗口 `(2056,924) → (1936,844)`，`dx=-120 dy=-80`。

### 3.3 折叠态点别处自动收起

- 先验证了现有逻辑：`Activated` → Deactivated → 220ms 后查前台 → 不是自己就 `HideWindow("deactivated")`。
  **实测在折叠态生效**（`shell.log: hide requested (deactivated)` + `window: hidden`，进程存活），无需修改。
- 补了**弹层例外**：`VISUALTreeHelper.GetOpenPopupsForXamlRoot()` 有未关闭的 popup 时（ComboBox 下拉、
  ContentDialog）不收起；Esc 也走同一个判断，弹层开着时 Esc 交给弹层。
- 展开态沿用同一自动隐藏（用户确认可以）。

---

## 4. 构建与验证

```
& <repo>\tools\dotnet-sdk\dotnet.exe build Predex.WinUI.csproj -p:Platform=x64 -nodeReuse:false -t:Rebuild

  已成功生成。
      0 个警告
      0 个错误
  已用时间 00:00:26.93
```

截图/交互证据脚本：`docs\r2-verify.ps1`

```
pwsh -NoProfile -File docs\r2-verify.ps1 -Exe "<...>\Predex.exe"
```

它做的事：PrintWindow 拍四页（`r2-home-expanded/r2-settings/r2-tuning/r2-lighting`）、
每个二级页记录物理/DIP 高度与右下角坐标、桌面截图（先临时置顶再降回，保证窗口真的在桌面上）、
真鼠标拖标题栏、Esc（折叠 + 展开）、托盘左键三次（开→关→开）、
第二次启动（不带 `--no-autohide`）用一个临时 WinForms 窗口抢前台验证失焦收起。

产物：`r2-home-collapsed.png`、`r2-home-expanded.png`、`r2-settings.png`、`r2-tuning.png`、
`r2-fans.png`、`r2-lighting.png`、`r2-desktop-*.png`（6 张）、`r2-desktop-after-esc-collapsed/expanded.png`。

---

## 5. 没改 / 改不了 / 有代价的（不美化）

1. **`TxtActiveModeName` 这个 AutomationId 被删掉了**。第二轮明确要求删掉区头右侧重复的「均衡」，
   而"删元素"和"保留它的 AutomationId"不可兼得。全仓检索确认没有任何脚本/测试引用该 id
   （只有 `TuningPage.xaml` 自己）；其余 **AutomationId / AutomationProperties.Name 一个没少**
   （逐文件 diff：Home 16/16、Tuning 61→61（少 1 多 1）、Settings 11→12、Lighting 44→45、Fans 0→1）。
2. **标题栏去掉了页面大标题，但没去掉版本号**：`PREDEX v3.0.8 │ CPU … GPU …` 在删掉三个按钮后宽度足够
   （实测内容 ~255 DIP / 可用 ~274 DIP）；`v3.0.8` 也挂到 PREDEX 的 tooltip 上。
3. **标题栏温度值仍是 11**（设计规则 §6.1 写的是 14 SemiBold）：4 个按钮删掉后理论上放得下，
   但 14 号值 + CPU/GPU 标签会顶到 320 DIP 的右边界，且与本轮"标题栏只做上下文"的定位冲突，故保持 11。
4. **行标签层级没统一**：设置页行标签 12 Primary（行内容主体），调校页行标签 11 Secondary（控件说明）——两者
   角色不同，本轮只统一了**卡片标题**和**右侧数值**这两种评审点名的角色。
5. **灯效页下半部分（键盘布局 / 4 个 ColorPicker / LED 网格）本轮仍没有真机点开验证**：窗口现在会撑到 844 DIP
   能看到「灯光模式/同步所有区域/预设主题/区域 1-4/取色环」，但更下面的 `KeyboardLayoutView` 画笔更新路径还是没点过
   （沿用 INTEGRATION.md §7.3 的欠账）。
6. **托盘右键菜单「退出」没有自动化点击**：`TrackPopupMenuEx` 是模态菜单，脚本点它会把整个验证流程卡住。
   代码路径（`TrayIconService.ShowContextMenu → CMD_QUIT → QuitRequested → QuitApplication`）只读核对过。
7. **`shell.log` 里 `layout: failed:` 一行空消息**：`LogLayout()` 在启动首帧（页面还没 `Loaded`）取
   `TransformToVisual` 抛 `NullReferenceException`，消息为空。**不是本轮引入**（`LogLayout` 未改），
   只影响 `--trace` 诊断日志，不影响界面；没动它以免扩大改动面。
8. **`ModeTileButtonStyle` 的选中态色值仍在调用点重复写**（`#263B82F6` / `#3B82F6` / `#14FFFFFF` / `#A8A8BC`）：
   `x:Bind` 函数实参不能接 `StaticResource`，而 `Converters.cs` 在 `Predex.WinUI` 根目录、不在本轮允许的改动目录里，
   所以没有为了实现"色值唯一来源"去动它。样式文件里已写明"这套 hex 对必须两页一致"，两页现在是逐字相同的。
9. **Overlay 页仍是 S1 骨架**（S6 未开始），本轮没碰。
10. **窗口现在会临时置顶（截图脚本行为）**：`Save-ScreenShot` 为了把窗口拍进桌面会 `SetWindowPos(HWND_TOPMOST)`
    再 `SetForegroundWindow`，拍完降回非置顶。应用本身没有改置顶行为。

---

## 6. 改动文件

| 文件 | 改动 |
|---|---|
| `Styles\Resources.xaml` | 间距/tile 高度 token；`PageSubtitleStyle`、`CardValueTextStyle`、`UnitTextStyle`、`ModeTileStackStyle`、`ModeTileIconStyle`、`ChipButtonStyle`；`CardStyle` padding 走 token；三段 `ThemeDictionaries` 覆盖 accent |
| `Controls\TelemetryText.cs` | **新增**：把 `"4109 RPM"` / `"83°C"` / `"N/A"` 拆成数值 + 单位（`Value` / `Unit`，供 x:Bind） |
| `Pages\HomePage.xaml` | 单一 `Spacing` 行距、统一列栅格、`ChipButtonStyle` 自动刷新、tile 走共享样式、去各处 `Margin`/`Height` 硬编码 |
| `Pages\TuningPage.xaml` | 删正文大标题（留副标题）、状态卡 2×2 + 单位拆分、模式行复用 tile 样式、删 `TxtActiveModeName`、去卡片标题图标、私有 Card/Title/Value 样式删除、值样式统一 |
| `Pages\SettingsPage.xaml` | 补副标题、去卡片标题图标与私有 Card 样式、USB 读数用 `StatusTextStyle` |
| `Pages\LightingPage.xaml` | 删正文大标题、检测卡 padding 8→12、设备 chip 归位、描述走 `Loc.T`、去 8 个装饰图标、右侧值统一 `CardValueTextStyle` |
| `Pages\FansPage.xaml` | 补副标题（与另三页一致） |
| `Shell\ShellWindow.xaml` | 三个常规按钮删除；标题栏加 `CPU`/`GPU` 标签、两个温度值同档；右侧只留展开箭头 |
| `Shell\ShellWindow.xaml.cs` | `IsMaximizable/IsMinimizable=false`、删最大化/还原/最小化/关闭代码；`SetupEscapeToHide`（Esc + 弹层判断）；`HasOpenPopup` 用于失焦与 Esc；`TrayClicked`；二级页自适应高度（`SecondaryPageHeightDip` / `FindScrollViewer` / `CurrentTargetHeightDip`）、`NavigateTo` 不再强制展开首页、`ShowAndActivate` 恢复当前页高度 |
| `Services\ShellServices.cs` / `Services\TrayIconService.cs` | 托盘左键回调单独一条（`TrayToggleRequested` / `onLeftClick`），托盘左键 = 开/关切换 |
| `Services\LocalizationService.cs` | 灯效设备描述等 7 条 zh-CN（`WinUIStrings`） |
| `docs\r2-verify.ps1` | **新增**：截图 + 高度 + 拖拽 + Esc + 托盘 + 失焦收起验证脚本 |
| `docs\R2-CONSISTENCY.md` | **新增**：本文件 |

> 位置说明：`Shell\` 与 `Services\` 不在任务最初给的 `Pages/Styles/Controls/docs` 清单里，但第二轮的
> 第 7、8 条和三条交互改造（标题栏、Esc、托盘、窗口高度）只能落在壳里，所以按新指令改了；
> `app\Fork\**`、`PreySense.Core`、`tools\**`、`Predex.WinUI.csproj` 仍未动。

---

## 7. 收尾

`Restore-AutoFan.ps1` 输出见本轮回复（EC 已在 Auto）。
