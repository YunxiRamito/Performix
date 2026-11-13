# Predex WinUI 壳 ↔ 页面契约（S1 定稿）

> 适用范围：`app\experiments\Predex.WinUI` 的 S2–S6 页面接力。
> 一句话：页面 = `Predex.WinUI.Pages` 命名空间下的一个 `Page`，靠 `App.CurrentShell.ViewModel` 取数据，靠 `Loc.T()` 出文案，别的都别自己发明。

## 1. 窗口与布局常量

| 项 | 值 | 位置 |
|---|---|---|
| 窗口宽 | 320 DIP（固定，两态一致） | `Shell\ShellWindow.xaml.cs` `WindowWidth` |
| 折叠高 | 116 DIP | `CompactHeight` |
| 展开高 | 280 DIP | `ExpandedHeight` |
| 标题栏 | 40 DIP + 1 px 分隔线 | `ShellWindow.xaml` 第 0 行 |
| 页面宿主 | `PageHost`，占第 1 行 | 同上 |
| 页面可用高度 | 折叠 76 / 展开 240 DIP | `280 - 40` |

页面自己不要设 `Width/Height`，也不要负 margin；外层 `ScrollViewer` 内边距用 `12,10,12,12`。

## 2. 导航

```csharp
App.CurrentShell?.NavigateTo("Tuning");   // key 见下表
```

- `ShellWindow.NavigateTo(key)`：懒创建页面实例 → 换进 `PageHost` → 更新标题栏。
  实例在**窗口可见期间**保留（不是 `Frame.Navigate`，同一会话内切页不丢状态）。
  **S6 起**：隐藏到托盘时整棵页面树会被销毁（清 `PageHost` + 丢所有页面实例，见 `S6-REPORT.md §3`），
  下次唤起时按上次的 nav key 重建当前页、再按它的内容量算窗口高度。所以页内未保存的临时草稿会丢，
  需要跨隐藏保留的状态请写进 `ShellUiState` 或 Core 配置。
- 非 Home 页面会自动把壳切到展开态；标题栏变成「返回 + 页面名」。
- **返回目标**（IA 第一阶段）：`ShellWindow.PageBackTargets` 是一张「页面 → 返回目标」表 ——
  `General / Power / Theme / Meta → Settings`，其余页面按导航历史回上一跳（`Fans` 有首页 / 调校两个入口），
  没有历史就回 `Home`。设置子页的跳转**不**入历史栈，表和栈因此不会互相漂移。
- 新增页面必须同时改 `ShellWindow.PageTypes` 与 `PageTitleKeys`（集成 agent 收口，S1 后不动）；
  设置子页还要在 `PageBackTargets` 里加一行。

| nav key | 期望页面类 | 需要绑的 ViewModel 属性（最小集） |
|---|---|---|
| `Home` | `Pages.HomePage` | `CpuTemperatureText` `GpuTemperatureText` `CpuFanText` `GpuFanText` `PowerModeName` `SetModeCommand` `IsEco/Quiet/Balanced/Perform/Turbo Active` `SetGpuMuxModeCommand` `SetBiosDiscreteGpuCommand` `IsGpu*Active` `ShowDdsGpuTiles` `AvailableRefreshRates` `SelectedRefreshRateOption` `IsAutoRefreshRateEnabled` `ToggleAutoRefreshRateCommand` `ActiveGpuDisplayMode` |
| `Tuning` | `Pages.TuningPage` | `IsKernelDriverSupported` `IsHardwareTuningEnabled` `IsProchotOffsetEnabled` `CanToggleProchotOffset` `CanEditProchotOffset` `IsGpuProfileTuningAvailable` |
| `Fans` | `Pages.FansPage` | `FanCurveModeNames`（其余逻辑在页面代码后置） |
| `Lighting` | `Pages.LightingPage` | `LightingDevices` `SelectedLightingDevice` `LightingDeviceModes` `LightingLedEditors` `DeviceLighting*` `KbdTimeoutSeconds` `RgbBrightness` `RgbSpeed` |
| `Overlay` | `Pages.OverlayPage` | **不需要 ViewModel 属性**（S6 更正）：WPF `MetricsWindow` 的模型是 Core 的 `OverlayConfig`，页面代码后置经 `PreySense.Overlay.AppConfig` / `HardwareOverlayManager` 读写；原表里的 `OverlayMetricSlots` 在 WPF 的 `MainViewModel` 里不存在 |
| `Settings` | `Pages.SettingsPage` | 目录页：无 ViewModel 属性，四行导航（`General` / `Power` / `Theme` / `Meta`） |
| `General` | `Pages.GeneralPage` | `IsRunOnStartupEnabled` `CanChangeRunOnStartup` `ToggleRunOnStartupCommand` `IsAcerServicesEnabled` `ToggleAcerServicesDebloaterCommand` |
| `Power` | `Pages.PowerPage` | `IsBatteryLimitEnabled` `ToggleBatteryLimitCommand` `UsbChargingCutoffPercent` `CanChangeUsbCharging` `UsbChargingStatus` `SourceProfileNames` `BatteryPowerModeIndex` `AcPowerModeIndex` `IsBatteryEnergySaverEnabled` |
| `Theme` | `Pages.ThemePage` | 无 ViewModel 属性（`AppearanceService` 直接读写） |
| `Meta` | `Pages.MiniMetaPage` | 无 ViewModel 属性（`Services\AboutInfo` + `UpdateCheckService`） |

## 3. 页面骨架

```csharp
namespace Predex.WinUI.Pages;          // 必须
public sealed partial class XxxPage : Page
{
    public MainViewModel ViewModel => App.CurrentShell!.ViewModel;   // 只读，别 new
    public XxxPage() => InitializeComponent();
}
```

- 写硬件只经 `ViewModel`（其内部走 `IHardwareService`）；页面不 P/Invoke 硬件。
- 页面 XAML root 设 `Background="Transparent"`（材质由窗口提供）。
- 文本一律 `{x:Bind local:Loc.T('English source')}`；新 key 加进 `Services\LocalizationService.cs` 的 `WinUIStrings`（UiText 在 WPF 项目里，不许改）。

## 4. 折叠 / 展开

- 壳负责窗口高度（220 ms 展开 / 200 ms 收起，`AppWindow.MoveAndResize` 逐帧 16 ms，锚右下）。
- 页面只切密度：Home 用 `VisualStateManager`（`Collapsed` / `Expanded`）；`HomePage.SetExpandedState(bool)` 是唯一的切换口。
- 展开内容常驻，只切 `Visibility`/`Opacity`；**别**用 Composition 动 `Offset`——会覆盖布局偏移（S1 踩过）。

## 5. 可复用资产

| 用途 | 用哪个 |
|---|---|
| 卡片（浮层用） | `CardStyle`（圆角 6 / 描边 1 / Padding 12） |
| 整块按钮 | `ModeTileButtonStyle`（圆角 6）、`EntryButtonStyle`（28 高，圆角 4） |
| 设置行 | `SettingRowStyle`（36 高）`SettingLabelStyle` `SettingHelpGlyphStyle` `SettingHintStyle` `SettingComboStyle` `SettingSwitchStyle` `SettingRowButtonStyle`（导航行）`SettingChevronStyle`（`Styles\Resources.xaml`，设置目录页 / 常规 / 电源共用；别再在页面里私造行样式） |
| 文本 | `PageTitleStyle`(16) `SectionTitleStyle`(12) `StatusTextStyle`(11) `CaptionTextStyle`(11) `BodyTextStyle`(11) |
| 颜色 | `TextPrimaryBrush` `TextSecondaryBrush` `TextMutedBrush` `AccentBrush` `BorderSubtleBrush` `SectionBgBrush` |
| 布尔→刷子/可见性 | `local:Converters.BoolToBrush / BoolToVis / Telemetry`（`Converters.cs`） |
| 图标 | `Glyphs`（`Glyphs.cs`，已逐张渲染核对） |
| 字体 | 字号只用 11 / 12 / 14 / 16；间距只用 4 / 6 / 8 / 12 / 16 |

## 6. 验收与自动化

- 每个可点元素给 `AutomationProperties.AutomationId`（截图脚本靠它点击）。
- 截图：`pwsh -File docs\s1-verify.ps1 -Exe "<输出>\Predex.exe"`（`--no-autohide --trace`，PrintWindow + UIA）。
- 设置 IA 阶段一：`pwsh -NoProfile -File src\Performix.App\docs\ia-verify.ps1 -Exe "<输出>\Performix.exe"`
  拍 `docs\ia-settings-hub.png` / `ia-general.png` / `ia-back-to-settings.png` / `ia-power.png` / `ia-meta.png`，
  并打印 `shell.log` 里 `nav: navigate … -> …` / `nav: back … -> …` 的证据行。
- 折叠态首图要等 4–5 s 让遥测出首帧，否则温度是 `—`。

## 7. S1 已踩过的两个坑（别再踩）

1. **别用 Composition 动带布局偏移的元素 `Offset`**
   `ElementCompositionPreview.GetElementVisual(el).StartAnimation("Offset", …)` 会**覆盖**该元素在父级里的 layout offset。S1 在 `PageHost`（y=40）和展开三组上这么干，结果整页内容顶到标题栏上，看起来像"标题栏没留高度"。
   正确做法：只动 `Opacity`（要位移就动 `(0,0)` 元素的 Offset，或用 `Translation`）。

2. **外圈浅色描边是两层，都要处理**
   - 外层 2 px 是 DWM 画的无边框线：`DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR /*34*/, DWMWA_COLOR_NONE /*0xFFFFFFFE*/)`，`DWMWA_CAPTION_COLOR` 一起设。
   - 内层 1 px 白线来自 `WS_OVERLAPPEDWINDOW` 的非客户区（`SetBorderAndTitleBar(false,false)` 去不掉）：`MakeBorderlessPopup()` 把 `GWL_STYLE` 换成 `WS_POPUP`（去掉 `WS_CAPTION|WS_THICKFRAME`）再 `SetWindowPos(SWP_FRAMECHANGED)`。
   - 圆角交给 DWM（`DWMWA_WINDOW_CORNER_PREFERENCE = 2`），XAML 里**不要**再套整窗圆角 Border。
   - 换成 `WS_POPUP` 后窗口不能靠系统缩放/最大化，S1 的拖拽走 `WM_NCLBUTTONDOWN(HTCAPTION)`，最大化是自己 `MoveAndResize` 到工作区。

