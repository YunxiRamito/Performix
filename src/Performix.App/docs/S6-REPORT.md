# S6 收尾报告（Overlay 真页面 + ContentDialog 提示层 + 隐藏释放 UI 树 + 删垫片）

日期：2026-11-13 05:25–05:45（本机 Acer Predator PH317-55，150% 缩放）
范围：`app\experiments\Predex.WinUI`，外加点名允许的三个文档
（`docs\WINUI3-MIGRATION.md`、`app\BUILD-NOTES.md`、`.ds-ledger.tsv`）+ `docs\HANDOVER.md` / `docs\SHELL-CONTRACT.md`。
红线：`app\Fork\PreySense.Core` / `app\Fork\PreySense.Frontend` / `PreySense.Tests` / `tools\**`
源码**一个字节没改**（按修改时间核对：这三个目录下 05:25 之后没有任何 `.cs/.xaml/.csproj` 变更）。
真机纪律：没点任何模式 tile / GPU tile / Apply；没写风扇 / 灯光 / GPU 偏移；没切性能或 GPU 模式；
收尾已跑 `Restore-AutoFan.ps1`（输出见 §6）。

## 0. 一句话结论

四件事都落地：Overlay 页从骨架变成真页面（槽位/缩放/透明度/位置/主题/帧率，全部走 Core 既有
`OverlayConfig`）；提示层换成 `ContentDialog`（确认=确认+取消，主按钮危险态红色）+ `InfoBar` 3 秒
轻提示（替掉 MessageBox 与 Debug 输出）；隐藏时整棵页面树被销毁、唤起时按需重建（二级页能按自己的
高度重建）；只删掉真正无引用的垫片，剩下三个垫片有引用、删不了（红线：不能改 WPF 版），原因见 §5。
`-t:Rebuild` 0 warning / 0 error，`dotnet publish` 产物能起窗口（§6）。**内存数字见 §4，和任务书
给的 244 MB 基线对不上，如实列在这里，没有编。**

## 1. 覆盖层页（`Pages\OverlayPage.xaml(.cs)`）

### 1.1 从 WPF `Views\MetricsWindow.xaml(.cs)` 搬了什么

| WPF 控件（AutomationId） | WinUI 实现 | 说明 |
|---|---|---|
| `CheckEnableOverlay`（Enable Metrics Overlay） | `ChkEnableOverlay` ToggleSwitch | 开= `HardwareOverlayManager.Start()`，关= `Stop()` |
| `DropdownOverlayTheme`（Dark / White） | `CmbOverlayTheme` ComboBox | `ConfigurationService…Overlay.ApplyTheme()`；重选同一主题直接 return（不冲掉手改的颜色，WPF 同款） |
| `SliderHudScale` 50–200 %（5 % 吸附） | `SldHudScale` + `LabelHudScale` | 值变化写 `overlay_scale_percent`，步进在代码里吸附（WinUI 的 Slider 没有 WPF 的 `IsSnapToTickEnabled`） |
| `SliderOpacity` 0–100 %（10 %） | `SldOpacity` + `LabelOpacity` | `overlay_alpha`，旧版 0–255 alpha 字节自动换算成百分比（WPF `NormalizeSavedOpacity` 同款） |
| `SliderTextOpacity` 0–100 %（10 %） | `SldTextOpacity` + `LabelTextOpacity` | `overlay_text_alpha` |
| `CmbHorizontalPos` / `CmbVerticalPos` | 同名两个 ComboBox | Left/Center/Right、Top/Center/Bottom，写 `overlay_pos_*` + `Reposition()` |
| `ChkShowFps` | `ChkShowFps` ToggleSwitch | `overlay_show_fps` |
| `BtnAddOverlaySlot` + `PanelColumnsContainer` | `BtnAddOverlaySlot` + `SlotsPanel`（代码后置建行） | 每槽：`Slot #n`、曲线开关、删除、CPU 指标下拉、GPU 指标下拉；CPU 10 项、GPU 13 项，和 WPF 的 `CpuChoices/GpuChoices` 逐项一致 |

- 所有读写都经 `PreySense.Overlay.AppConfig` / `HardwareOverlayManager` / `Core.ConfigurationService`，
  **没有新开写路径**，也没有改 `Preysense.Core`/`Overlay` 一行（两者都在红线外）。
- 文案：指标名/位置/主题/开关态全部是 UiText 里**五语言已存在**的键；只把页面新增的 8 条加进
  `Services\LocalizationService.cs` 的 `WinUIStrings`（Position / Graph / Remove slot / Refresh interval /
  1 s (fixed) / 悬浮说明 + 两条页面文案）。
- 布局适配 320 DIP：WPF 那版是 560 宽窗口，两张卡一行放两个 180 宽的 ComboBox；这里改成单列，
  槽位块变成「标题行 + CPU 行 + GPU 行」，卡片仍是 `CardStyle`（12 内边距 / 圆角 6 / 1 px），
  字号只用 11/12/16，间距只用 4/6/8/12。
- **刷新间隔**：WPF `MetricsWindow` 没有这个控件、`Core` 的 `OverlayConfig` 也没有这个字段
  （`AppConfig.Get("overlay_*")` 里没有 refresh 项）。覆盖层的刷新其实由
  `Core\Services\TelemetryCoordinator` 的主定时器驱动：`MasterTimer = new(1000)`、
  `MinDemandPollIntervalSeconds = 1.0`，即固定 1 s。所以页面上做成**只读行**
  `刷新间隔 · 1 秒（固定）`，附 tooltip 说明；不做假滑杆（写一个没人读的配置比不写更糟）。
  要变成可配置必须改 Core/Overlay，红线外。
- 页面只读载入（`_loading` 闸门）：构造页面不写 `preysense.json`；只有用户真改控件才写（与 WPF 版
  行为一致）。本轮验证**没点任何写操作**（`preysense.json` 未被修改）。

### 1.2 证据

`docs\s6-overlay.png`（480×780 px = 320×520 DIP，PrintWindow，ink 79.8 %）：`浮窗 / 浮窗指标栏 /
添加插槽 / 栏 1 CPU 占用·GPU 占用 / 栏 2 温度 / 栏 3 有效频率·频率 / 曲线开关 / 移除`。
`docs\s6-overlay-scrolled.png`（ink 79.9 %）：显示卡（启用监控浮窗=关、主题=深色、HUD 缩放 100%、
背景不透明度 100%、文字不透明度 100%、刷新间隔 1 秒（固定））+ 位置卡（水平位置=左）。
`shell.log`：
```
overlay: page ready enabled=False theme=Dark slots=7 scale=100% bg=100% text=100% pos=Left/Top showFps=True
nav: created page Overlay (OverlayPage, Vm=n/a)
nav: page=Overlay expanded=True targetHeight=520DIP
layout: page=OverlayPage content=1489DIP wanted=1529DIP cap=520DIP workArea=912DIP → 520DIP
```

## 2. 提示层

### 2.1 `Services\ContentDialogUserPromptService.cs`（新）

- 实现 WPF 的 `IUserPromptService`（`Confirm/ConfirmAsync/Notify/NotifyAsync`），底层 WinUI
  `ContentDialog`，`XamlRoot` 从壳的根 `RootGrid` 取；服务在 `ShellWindow` 构造里创建并
  `ViewModel.UserPrompts = Prompts`（`MainViewModel` 里本来就是 internal setter，
  WPF 版在 `MainWindow.xaml.cs:66` 也是这么装的）。
- 确认框 = `PrimaryButtonText`（确认）+ `CloseButtonText`（取消）；通知框 = 只有主按钮。
- **危险操作**：`ContentDialog` 有 `PrimaryButtonStyle` 属性（在 `Microsoft.UI.Xaml.winmd` 与
  WinUI `generic.xaml` ContentDialog 模板里都核对过：`<Button x:Name="PrimaryButton"
  Style="{TemplateBinding PrimaryButtonStyle}">`）。本页在 `Styles\Resources.xaml` 新增
  `PrimaryButtonStyle`（accent `#3B82F6`）与 `DangerPrimaryButtonStyle`（红 `#E5484D`）；
  GPU MUX / 重启这类破坏性确认用后者。
  - 坑（实测踩到）：`DefaultButton=Primary` 时 ContentDialog 的 `PrimaryButtonAsDefaultButton`
    可视状态会把 `PrimaryButton.Style` 改成框架的 `AccentButtonStyle`，**覆盖**我们设的样式
    （第一版截图里按钮是系统 accent 浅紫、文字被截断）。改成 `DefaultButton=None` 后自定义样式
    生效：截图里确认按钮是红色 `Apply & Restart Now`，取消是中性按钮。
    代价：Enter 不再等价「确认」（对破坏性操作反而是好事），Esc 仍可关闭（返回 None → false）。
  - 弹窗主题：ContentDialog 在 XamlRoot 的 popup 层，不继承 `RootGrid.RequestedTheme`，所以服务
    显式 `dialog.RequestedTheme = RootGrid.RequestedTheme`，保证和壳同主题。
- 同步 `Confirm`/`Notify`：WinUI 上无法实现（`ShowAsync` 是异步的，阻塞 UI 线程会死锁），
  保留为「记一行日志、确认返回 false」，接口注释里本来就写了「WinUI 3 只能实现异步形状」。
  链接进来的 ViewModel 只调 `ConfirmAsync`/`NotifyAsync`（`MainViewModel.Gpu.cs:318/387`）。
- 同一时刻只允许一个 ContentDialog（WinUI 限一个），并发第二个记日志后返回 false，不会崩。
- 已删除 `Views\CustomModalDialogCompat.cs`（Win32 `MessageBoxW` 版）；WinUI 工程里现在没有
  `CustomModalDialog` 类型（grep 只剩注释/文档）。

### 2.2 `ModeToastWindow` 替身 → 真提示（`Views\ModeToastCompat.cs` 重写）

**选择**：壳内 `InfoBar`（不是复用 InfoWindow 形态）。理由：
1. WinUI 里再开一个窗口 = 多一个 `AppWindow` + XAML island + composition target（自己的 HWND 和
   可视树），内存直接顶在「隐藏态 25–35 MB」指标上；壳本来就预留了这条 `ShellInfoBar`。
2. 文案/主题/关闭行为都跟壳一致，3 秒计时只有一处。
3. 与 WPF 的 `InfoWindow`（右下角信息窗）相比，顶部条更贴近「模式已确认」这种一次性反馈。

实现：`ShellWindow.ShowToast(InfoBarSeverity, title, message)` → `ShellInfoBar.IsOpen=true`，
`Translation` 0→(-8) 滑入 + Opacity 0→1，160 ms `cubic-bezier(0.23,1,0.32,1)`；3 秒后
140 ms 淡出并 `IsOpen=false`（`SetIsTranslationEnabled` 打开 Translation，避免覆盖布局偏移，
SHELL-CONTRACT §7.1 的坑）。动画关闭用 session 计数器防止「新 toast 被旧定时器关掉」。
`InfoWindowService`（`Compat\InfoWindowServiceCompat.cs`，链接的 `MainViewModel.Fans/PowerModes`
会调）也接到同一个 toast：Success/Failure/Warning/Information 分别映射 InfoBar 四种 severity。
`SetProgress`/`Dismiss` 只写 `shell.log`（壳里没有常驻「进行中」行，没有可更新的目标）。

### 2.3 证据（怎么证明「能弹出来」）

为了不碰硬件，加了只读演示开关 `--demo=notify|confirm|toast`（`App.xaml.cs` → `ShellWindow.RunUiDemo`）：
文案用真实的 GPU MUX / GPU 断电重启原文，但**不调用任何 ViewModel 命令**，所以不可能写 EC/WMI/NVAPI、
也不会重启。`docs\s6-verify.ps1` 用 PrintWindow 抓图：

| 文件 | 内容 | 尺寸 | ink |
|---|---|---|---|
| `s6-dialog-confirm.png` | 确认框：`Predex • Hardware Discrete GPU` / `Hardware Display MUX` / 「Would you like to switch to dGPU Only mode and restart now?」/ **红色** `Apply & Restart Now` + `Cancel` | 480×450 | 89.0 % |
| `s6-dialog-notify.png` | 通知框：`Predex • Discrete GPU Power Cycle` / 单按钮 `OK`（accent 蓝，不是红色） | 480×450 | 83.8 % |
| `s6-toast.png` | InfoBar 成功条从顶部滑入：`性能模式 / Beast — demo toast, no mode was changed` + 关闭按钮 | 480×450 | 46.3 % |
| `s6-toast-gone.png` | 同位置 3 秒后：条已消失（只剩页面） | 480×450 | 33.3 % |

`shell.log` 同一轮：
```
prompt: show 'Predex • Hardware Discrete GPU' buttons=confirm+cancel danger=True
prompt: show 'Predex • Discrete GPU Power Cycle' buttons=ok danger=False
toast: Success '性能模式': Beast — demo toast, no mode was changed
toast: auto-dismiss after 3 s        ← 与上一行时间差 3.0 s（05:43:03.043 → 05:43:06.058）
```

## 3. 隐藏→释放 / 唤起→重建

实现（`Shell\ShellWindow.xaml.cs`）：
- 隐藏动画完成回调里：`AppWindow.Hide()` → `NotifyWindowHidden()` → 关遥测 →
  **`PageHost.Children.Clear()` + `_pages.Clear()`（当前页也销毁）** → 投递一轮
  `MemoryManager.TrimNow()`（`GC.Collect` + `SetProcessWorkingSetSize(-1,-1)`，和空闲回收同一套），
  并把当时的工作集/私有内存写进 `shell.log`。原来的 `ReleaseInactivePages()`（只丢非当前页）已删除。
- 唤起（托盘/热键/第二实例信号）时：先 `RestoreCurrentPage()`（页面宿主为空就按上次的 tag 重建页面，
  Home 再套用折叠/展开态），再按新页面的内容量算窗口高度、定位、`AppWindow.Show()`。
- 二级页重建实测：Overlay 页隐藏前 320×520 DIP，唤起后仍是 **320×520 DIP**（不是 116/300 的首页高度），
  `layout: page=OverlayPage content=1489DIP … → 520DIP` 重新打了一次。
- 代价（如实写）：页面实例不再跨隐藏保留 → 页内未保存的临时编辑状态（比如拖动中的滑块草稿）会丢；
  折叠/展开密度存在 `ShellUiState`（`%LOCALAPPDATA%\Predex\shell-ui.json`）不受影响。
  这与 SHELL-CONTRACT 原来「实例在窗口会话内保留」的说法不同，本轮改了行为并在契约里注明。

证据：`s6-show-restored.png`（Home 重建后 320×116 DIP，ink 38.5 %）、
`s6-show-restored-overlay.png`（Overlay 重建后 320×520 DIP，ink 79.8 %）、
`shell.log`：`mem: page tree released (2 page(s), current=Overlay); workingSet=1MB private=120MB`
→ `nav: restoring page Overlay after hide` → `nav: created page Overlay` → 重新 layout。

## 4. 内存实测（before / after，同一脚本同一台机器）

脚本：`docs\s6-memory.ps1`（启动 → 等窗口 → 可见态 8 s 后取样 → 用托盘回调消息隐藏 →
+1.5 s / +10 s / +30 s 取样，各取 3 次取稳定值）。只发 `WM_TRAY_CALLBACK + WM_LBUTTONUP`，
不点硬件控件。

| 采样点 | 改动前（旧构建，2 次） | 改动后（本轮，3 次） |
|---|---|---|
| 可见态（8 s） | 216.7 / 217.0 MB | 216.6 / 216.9 / 217.0 MB |
| 隐藏 +1.5 s（空闲回收尚未跑） | **214.3 / 214.3 MB** | **53.1 / 53.2 / 54.6 MB** |
| 隐藏 +10 s | 8.2 / 8.2 MB | 11.1 / 11.1 / 53.8 MB（波动大：立即 trim 后页面会短暂回驻） |
| **隐藏 +30 s（验收点）** | **16.3 / 16.7 MB** | **18.0 / 18.7 / 19.0 MB** |
| 隐藏 +30 s 私有内存 | 97.5 / 97.6 MB | 96.4 / 96.9 / 98.7 MB |

结论（不美化）：
- **任务书说的「隐藏后 ≈244 MB」在本机复现不出来。** 能复现的同类数字是「隐藏了、但空闲回收
  （8 s）还没跑」的那一刻：改动前实测 **214.3 MB**。+30 s 时旧构建本来就已经是 **16.3–16.7 MB**，
  低于 25–35 MB 目标。所以「244 MB → 25–35 MB」这个前后关系在本机不成立，真实的前后关系是
  「隐藏后 <2 s 还是 214 MB」→「现在 <2 s 就退到 53–55 MB，30 s 稳定在 16–19 MB」。
- 本轮改动的实际收益：**隐藏后立刻释放**（214.3 → 53.2/54.6 MB @ +1.5 s），页面树真的被销毁并在
  唤起时重建；30 s 稳态没有变好（18.7/19.0 vs 16.3/16.7，差 ~2 MB，在两次重复测量的波动量级内）。
- 私有内存基本不动（~97–99 MB）：这部分是 WinUI/WindowsAppRuntime/DWM 的原生提交，清页面树收不回。
  要再往下压只能销毁窗口/重启进程（托盘图标绑在 HWND 上，这次没做，风险大）。
- 25–35 MB 是工作集目标：**改动前后都达标**；本轮把「达标所需等待」从 8 s 压到约 2 s。

## 5. 垫片清单

| 文件 | 处理 | 依据 |
|---|---|---|
| `Views\CustomModalDialogCompat.cs` | **删除** | 全工程 grep 无引用（Win32 `MessageBoxW` 版），功能被 `ContentDialogUserPromptService` 取代 |
| `Views\StubViews.cs` | 早已不在（S2 搬页面时删掉） | 文件不存在 |
| `Views\ModeToastCompat.cs` | **保留但重写为真提示** | 链接的 `MainViewModel.PowerModes.cs:32` 直接调 `ModeToastWindow.ShowConfirmedMode`，类型必须存在 |
| `Compat\InfoWindowServiceCompat.cs` | **保留但改成真 toast** | 链接的 `MainViewModel.Fans.cs:303/354`、`PowerModes.cs:350/363` 调 `InfoWindowService`；真实现是 WPF 窗口，不能链接 |
| `Compat\WpfShims.cs` | **保留（删不掉）** | 链接的 WPF ViewModel 直接引用这些伪造类型：`System.Windows.Threading.Dispatcher`（`MainViewModel.cs:135/303`、`Session.cs:270`）、`RenderOptions/Interop.RenderMode`（`Gpu.cs:253-291`）、`Application.Current`。删它必须改 `PreySense.Frontend`（红线） |
| `Compat\AppCompat.cs` | **保留（删不掉）** | 同上：`App.Current` / `App.SuppressAutoHide` 被链接文件引用 |

`WpfShims` 的已知行为差异（不是本轮引入，保留证据）：`debug.log` 里 04:24 有一条
`Acer mode switch failed: Dispatcher.CurrentDispatcher called off the UI thread`，栈指到
`AppCompat.Dispatcher` → `WpfShims.cs`。彻底消掉要按 `WINUI3-MIGRATION.md §5` 的退役路线，把
~20 处调用改成注入的 `IDispatcher`——那是改 WPF 版，红线外。

## 6. 构建 / 发布 / 收尾

```
& <repo>\tools\dotnet-sdk\dotnet.exe build Predex.WinUI.csproj -p:Platform=x64 -nodeReuse:false -t:Rebuild
已成功生成。 0 个警告 0 个错误（24.96 s）
输出 93 个文件（纯构建产物，运行期 shell.log 已清掉），resources.pri = 1,409,752 B，无 Predex.pri
Predex.exe FileVersion = 3.0.8.0
```

```
& ... dotnet.exe publish Predex.WinUI.csproj -p:Platform=x64 -c Release -nodeReuse:false
publish\：90 个文件（61 dll / 2 exe / 19 winmd / 3 pri / 3 json / 1 pdb / 1 png）；
          resources.pri = 1,397,232 B；Predex.pri 不存在
从 publish 目录直接启动：pid=14264 hwnd=0x9608AC class='WinUIDesktopWin32WindowClass'
title='Predex' size=480x450 px (320x300 DIP) PrintWindow=True ink=33.2%
log: window: style 0x04480000 → 0x84080000 (WS_POPUP, DWM border colour = COLOR_NONE)
log: nav: created page Home (HomePage, Vm=n/a) / window: shown
截图：docs\s6-publish-window.png
```

收尾（`Restore-AutoFan.ps1`）：

```
SetGamingFanBehavior 0x00410009 (Auto) -> True
当前: temp CPU=76C GPU=52C  fan=4054/4054 RPM  mode=1
```

## 7. 改动文件

新增：`Pages\OverlayPage.xaml`（重写，原为骨架）、`Pages\OverlayPage.xaml.cs`（重写）、
`Services\ContentDialogUserPromptService.cs`、`docs\s6-verify.ps1`、`docs\s6-memory.ps1`、本文件。
修改：`Shell\ShellWindow.xaml`（InfoBar 移到页面区顶部 + AutomationId）、
`Shell\ShellWindow.xaml.cs`（Prompts 接线、隐藏释放页面树、唤起重建、ShowToast、RunUiDemo）、
`Views\ModeToastCompat.cs`（Debug → InfoBar）、`Compat\InfoWindowServiceCompat.cs`（Debug → InfoBar）、
`Styles\Resources.xaml`（PrimaryButtonStyle / DangerPrimaryButtonStyle）、
`Services\LocalizationService.cs`（8 条 zh-CN）、`App.xaml.cs`（`--demo=` 钩子）。
删除：`Views\CustomModalDialogCompat.cs`。
文档：`docs\SHELL-CONTRACT.md`（Overlay 契约行更正）、`docs\HANDOVER.md`（§11）、
`docs\WINUI3-MIGRATION.md`（§11 状态表）、`app\BUILD-NOTES.md`（§14）、`.ds-ledger.tsv`（追加 2 行）。

## 8. 没做成 / 没做的（附原因）

1. **覆盖层 HUD 本体没验**：`PreySense.Overlay` 是独立渲染（自己的透明窗）。开启它要写
   `preysense.json` 并起 OSD 窗口，本轮按真机纪律/红线没有开；所以「改 HUD 缩放后覆盖层实际变化」
   这条验收没测。本页只验证了配置读、UI 呈现、以及写入路径的代码（与 WPF 完全同一个入口）。
2. **GPU MUX 确认/重启没走真实触发路径**：真实触发会写 EC/NVAPI 并要求重启（真机纪律禁止）。
   改用 `--demo=confirm/notify` 走同一个 `ContentDialogUserPromptService`、同一段原文验证弹出与样式。
   「ViewModel → UserPrompts → ContentDialog」这条链路的接线是代码级完成的，但没有硬件事件的端到端实测。
3. **`ModeToastWindow` 的真实触发（EC 确认模式切换）没验**：同上，切模式是硬件写。用 `--demo=toast`
   验证了 InfoBar 本体、3 秒自动消失与日志。
4. **刷新间隔不可配置**：Core/Overlay 没有这个字段（见 §1.1），做只读展示；要可配置必须改红线外项目。
5. **`Confirm`/`Notify` 同步版是空实现**：WinUI 不能在 UI 线程同步等 ContentDialog；接口注释本身
   就写了这一层只支持异步形状，链接代码只调异步版。
6. **WpfShims/AppCompat/InfoWindowService 删不掉**：链接的 WPF 文件仍引用这些类型，删除=改
   `PreySense.Frontend`（红线）。见 §5。
7. **私有内存没降**（~97–99 MB）：清页面树收不回原生内存；再降要销毁窗口/重启进程，没做。
8. **WPF 在产版没有重新构建验证**：本轮没改 `PreySense.Frontend`/`Core` 任何源码（按修改时间核对），
   但也没有跑 WPF 的 build/测试（不在本轮验收内，避免占构建窗口）。
9. **S7（切默认入口 / 计划任务指向新 exe）没做**：不在本轮任务内。
