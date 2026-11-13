# Predex：WPF → WinUI 3 单窗口渐进迁移蓝图

> 版本：1.0（2026-11-13）
> 范围：只读代码调研 + 迁移方案。**本文不授权修改任何源码**。
> 目标形态：**单窗口 + 页内导航（GHelper 式）**，不是每功能一个 Window。
> 硬件层 `app\Fork\PreySense.Core`（ACPI-WMI / 命名管道 / NVAPI / powercfg）**一行不改**。

---

## 目录

| # | 章节 | 一句话 |
|---|---|---|
| 1 | [结论摘要](#1-结论摘要) | 能不能做、卡在哪、先做哪三件 |
| 2 | [现状地图](#2-现状地图) | 两套前端 + 禁区边界 + 构建现实 |
| 3 | [八个界面逐页拆解](#3-八个界面逐页拆解) | 控件 / 结构 / VM 依赖 / WinUI 映射 |
| 4 | [跨页「没有对应物」总表](#4-跨页没有对应物总表) | 6 类硬骨头逐条给替代方案 |
| 5 | [Predex.WinUI 实验壳逐文件裁决](#5-predexwinui-实验壳逐文件裁决) | 留 / 改 / 删 + 理由 |
| 6 | [Dafeiyu-Go 可照抄做法](#6-dafeiyu-go-可照抄做法) | 文件 + 行号 + 要点 |
| 7 | [目标架构：单窗口 + 页导航](#7-目标架构单窗口--页导航) | 壳、导航、状态、内存 |
| 8 | [分阶段路线](#8-分阶段路线s0s7) | S0–S7，每阶段可 build / 可跑 / 可截图 |
| 9 | [不做什么与红线](#9-不做什么与红线) | 沿用项目红线 + 新增迁移红线 |
| 10 | [附录](#10-附录) | 命令、验收模板、风险登记表 |

---

## 1. 结论摘要

| 问题 | 结论 |
|---|---|
| 技术上可行吗 | 可行。`Predex.WinUI` 已证明：非打包 + 自包含 + `requireAdministrator` + WinUI 3 能跑，已链 21 个 VM + 8 个 Service 编译通过 |
| 最大阻塞项 | **PRI 生成**（`AppxGeneratePriEnabled=false` 导致启动崩 `ms-appx:///Microsoft.UI.Xaml/Themes/themeresources.xaml`）。见 §6.6，Dafeiyu 的做法是反过来 **开着** `EnableMsixTooling` |
| 第二阻塞项 | **VM 里硬编码 WPF 视图**：`MainViewModel.Gpu.cs:315-334 / 410-429` 直接 `new Views.CustomModalDialog{ Owner = Application.Current.MainWindow }` 并**同步** `ShowDialog()`。单窗口形态下没有「Owner 模态」这东西，必须抽 `IDialogService` + 改 `async` |
| 第三阻塞项 | **3 个自绘控件靠 `OnRender`**（`WpfFanCurveControl` / `ColorWheelControl` / `RgbKeyboardLayout`）。WinUI 3 的 `FrameworkElement` **没有 `OnRender`**，必须改用 Canvas 元素树 / `Shape` / Win2D 重画 |
| 页面难度排序 | 设置（易）< 指标（易）< 调校（中）< 主窗（中）< 风扇曲线（难，自绘+拖拽）< 灯效（最难，792 行 + 5 个色轮 + 键盘图）< 对话框/提示窗（形态变了，得重设计） |
| 必须重写而非「换命名空间」的 | `ColorWheelControl`、`RgbKeyboardLayout`、`WpfFanCurveControl`、`WindowTitleBar`、`LabeledTabSwitcher`、`SettingXxxRow`（6 个）、`WindowChrome` 逻辑、`InfoWindow` 透明窗 |

### 最该先做的三件事

1. **先把「构建可信」立住**：把 `EnableMsixTooling` 打开（对齐 Dafeiyu 已验证配置），让 `Predex.pri` 由构建自动产出，删掉手工 `makepri` 那一套 + `tools/pri-stub` 替身 DLL。否则后面每个阶段都要人肉补救。
2. **抽 `IDialogService` + 干掉 VM 里的 `ShowDialog()`**：这是「单窗口导航」与现有 VM 的唯一硬冲突。改法小、影响面明确（`MainViewModel.Gpu.cs` 2 处、`CustomModalDialog.xaml.cs` 1 处）。
3. **S1 先把单窗口壳跑通并截图**：`NavigationView` + 页宿主 + 首页 = 现 `MainWindow.xaml` 内容 + 托盘/热键/真硬件模式切换。这一步做完，后面每一页就都只是「往壳里塞内容」，风险被切成 6 份。

---

## 2. 现状地图

### 2.1 两套前端

| 项目 | 路径 | 类型 | 规模 | 状态 |
|---|---|---|---|---|
| 产品前端（WPF） | `app\Fork\PreySense.Frontend` | .NET 10 + WPF，`AssemblyName=Predex` | 约 60 个源文件；8 个 Window + 2 个小窗 | **在产**，红线级 |
| 实验壳（WinUI 3） | `app\experiments\Predex.WinUI` | WinUI 3（WindowsAppSDK 1.6.250205002） | csproj 87 行 / 主窗 338 行 XAML | 已移出 `PreySense.slnx`，独立 csproj |
| 硬件层 | `app\Fork\PreySense.Core` | 类库 | `IHardwareService` 全量契约 | **禁区** |
| 覆盖层 | `app\Fork\PreySense.Overlay` | — | `OverlayBridge` 等 | 仅经桥接调用 |

`PreySense.slnx` 当前 5 个项目（Core / Overlay / Frontend / Tests / Probe），**不含 WinUI**——已确认。

> ⚠️ `app\experiments\Predex.WinUI\docs\HANDOVER.md` 第 7 节写「`PreySense.slnx` 已加入 `Predex.WinUI` 项目」，**与当前仓库状态不符**（已被移出）。该文件第 7 节还记录了另一处历史副作用：`PreySense.Frontend\Services\StartupDiagnostics.cs` 的 `typeof(App)` 曾被改成 `typeof(StartupDiagnostics)` 以适配 WinUI 无同名 `App`。S7 落地 `IDialogService` 后，这个改动应重新评估能否还原。

### 2.2 界面清单与体量（read 实测总行数）

| 界面 | 文件 | XAML 行数 | 代码后置行数 | 迁 WinUI 难度 |
|---|---|---|---|---|
| 主窗口 | `MainWindow.xaml` | 315 | 201（`MainWindow.xaml.cs`）+ 171（`MainWindow.Visibility.cs`） | 中 |
| 设置 | `Views\SettingsWindow.xaml` | 162 | 51 | 易 |
| 调校 | `Views\ModeSettingsWindow.xaml` | 380 | 882 | 中 |
| 风扇曲线 | `Views\FanCurveWindow.xaml` | 146 | 661 | 难 |
| 灯效 | `Views\KeyboardWindow.xaml` | **792** | 555 | **最难** |
| 指标/覆盖层 | `Views\MetricsWindow.xaml` | 142 | 383 | 易 |
| 模态对话框 | `Views\CustomModalDialog.xaml` | 71 | 84 | 形态要重设计 |
| 信息小窗 | `Views\InfoWindow.xaml` | 86 | 159 | 形态要重设计 |
| （附带）模式提示 | `Views\ModeToastWindow.xaml` | 36 | 154 | 形态要重设计 |

### 2.3 自定义控件清单（`Controls\`）

| 控件 | 基类 | 职责一句话 |
|---|---|---|
| `WindowTitleBar` | `UserControl` | 无边框窗的统一标题栏：品牌+标题+副标题、`Actions` 插槽、最小化/最大化/关闭、拖拽与双击最大化 |
| `FluentIcon` | `TextBlock` | 把 Segoe Fluent Icons 字形当文本渲染，统一字号/颜色/主题 |
| `FluentGlyphs` | `static class` | 字形常量表（29 个键，见 §3.9） |
| `ThemedIcon` | `Control` | 资源位图图标（`Source` 依赖属性），按主题换图 |
| `LabeledTabSwitcher` | `Control`（模板化） | 两段式左右标签切换（如 CPU 风扇 / GPU 风扇） |
| `RectangularSwitch` | `ToggleButton` | 胶囊双段开关，点击哪段就取哪段值，Space/Enter 恒翻转 |
| `ActionButton` | `Button` | 带语义强调色的动作按钮 |
| `SettingCard` | `UserControl` | 卡片容器（图标 + 标题 + 内容） |
| `SettingSliderRow` | `UserControl` | 标签 + 滑杆 + 数值框 + 可选说明的单行控件 |
| `SettingSwitchRow` | `UserControl` | 标签 + 胶囊开关 + 可选 On/Off 文案 + 说明 |
| `SettingDropdownRow` | `UserControl` | 标签 + 下拉 + 可选说明；内含防回环镜像逻辑 |
| `SettingValueRow` | `UserControl` | 标签 + 可编辑数值框（带上下限/单位/空值语义/提交事件） |
| `SettingActionRow` | `UserControl` | 标签 + 动作按钮行 |
| `ColorWheelControl` | `FrameworkElement`（`OnRender`） | 自绘 HSV 色轮 + 拖动取色 + `SelectedColor`/`ColorPicked` |
| `RgbKeyboardLayout` | `FrameworkElement`（`OnRender`） | 自绘笔记本键盘图，按灯的编辑项着色/选中 |
| `WpfFanCurveControl` | `FrameworkElement`（`OnRender`） | 自绘风扇占空比折线图（X=°C、Y=%），可拖点 |
| `HexRgbColorConverter` | `IValueConverter` | `#RRGGBB` ↔ `Color` |
| `TranslateConverter` | `IValueConverter` | 文案 key → 当前语言文本（`UiText`） |

### 2.4 构建现实（本机实测）

| 事实 | 证据 |
|---|---|
| 系统无 .NET SDK | `C:\Program Files\dotnet\dotnet.exe --list-sdks` → **空** |
| 便携 SDK 唯一可用 | `tools\dotnet-sdk\dotnet.exe --list-sdks` → `10.0.404` |
| `Microsoft.Build.AppxPackage.dll` 是自制替身 | `tools\dotnet-sdk\sdk\10.0.404\...\AppxPackage\Microsoft.Build.AppxPackage.dll` 13312 字节，源码在 `tools\pri-stub\`（8 个任务：`ExpandPayloadDirectories` / `GetDefaultResourceLanguage` / `GetPackageArchitecture` / `ValidateConfiguration` / `RemoveRedundantXamlFilesFromSdkPayload` / `GetSdkFileFullPath` / `GetSdkPropertyValue` / `RemovePayloadDuplicates`）——**不含 PRI 生成任务** |
| `makepri.exe` 可用 | `%USERPROFILE%\.nuget\packages\microsoft.windows.sdk.buildtools\10.0.22621.756\bin\10.0.22621.0\x64\makepri.exe` |
| 截图脚本可复用 | `docs\capture-ui.ps1` 有 `-Exe` 参数（默认指向 WPF 的 `app\dist\Predex.exe`），可直接指 WinUI 产物 |

---

## 3. 八个界面逐页拆解

> 统计口径：`grep 'controls:(\w+)'` 取去重控件名；行数取 `read` 报的总行数；VM 依赖取 `{Binding ...}` 与 `x:Name` 代码后置两路。

### 3.1 `MainWindow.xaml`（315 行）— 主窗口

**自定义控件**：`WindowTitleBar`（L13，`Actions` 槽里塞 Settings 按钮）、`FluentIcon` / `FluentGlyphs`（16 处）

**页内结构**

| 行 | 内容 |
|---|---|
| Row0 | `WindowTitleBar`（Subtitle 绑 `UpdateCheckService.DisplayVersion`）+ Settings 按钮 |
| Row1 | `StackPanel` 三张 `OriginalSectionPanelStyle` 卡片：① Performance Mode（`UniformGrid Columns=5`，5 个模式磁贴）② GPU Mode（`UniformGrid Columns={Binding GpuModeTileColumns}`，4 个磁贴，Hybrid/Adv.Optimus 受 `ShowDdsGpuTiles` 控显隐）③ Display（`Grid` 1\*/2\*：Auto 按钮 + 刷新率 `ComboBox`）+ `Separator` |
| Row2 | 底部导航两行：`UniformGrid Columns=3`（Lighting / Overlay / Fans）+ `UniformGrid Columns=2`（Tuning / Quit） |

**依赖的 ViewModel 成员**（23 个）
`CpuTemperatureText`、`CpuFanText`、`GpuTemperatureText`、`GpuFanText`、`ActiveGpuDisplayMode`、`GpuModeTileColumns`、`ShowDdsGpuTiles`、`AvailableRefreshRates`、`SelectedRefreshRateOption`、`IsAutoRefreshRateEnabled`、`IsEcoActive`、`IsQuietActive`、`IsBalancedActive`、`IsPerformActive`、`IsTurboActive`、`IsGpuAutoActive`、`IsGpuOptimusActive`、`IsGpuAdvancedOptimusActive`、`IsBiosDiscreteGpuActive`；命令 `SetModeCommand`、`SetGpuMuxModeCommand`、`SetBiosDiscreteGpuCommand`、`ToggleAutoRefreshRateCommand`

**WinUI 3 映射**

| WPF | WinUI 3 | 备注 |
|---|---|---|
| `WindowChrome CaptionHeight=0 …`（L3-5） | `AppWindow.TitleBar.ExtendsContentIntoTitleBar = true` + `SetTitleBar(dragRegion)` | 只要还能看到系统三按钮，就要么设 `ButtonBackgroundColor=Transparent`（Dafeiyu 说这招会留禁用态灰块），要么走 `WM_NCCALCSIZE` 归零非客户区（`WindowChrome.cs` 做法，§6.3） |
| `SizeToContent="Height"`（L2） | **无对应物**。固定高度 + `ScrollViewer`，或按内容 `ActualHeight` 调 `AppWindow.Resize` | 壳内页宿主建议固定：主窗 420×490（现实验值），页内容滚动 |
| `UniformGrid Columns=5`（L40） | `Grid` 5 个 `*` 列，或 `ItemsRepeater` + `UniformGridLayout` | 固定 5 个别用 Repeater，省事 |
| `UniformGrid Columns={Binding GpuModeTileColumns}`（L153） | 同上，列数用代码换算成 `Grid.ColumnDefinitions`，或 `ItemsRepeater` | 动态列数时 Repeater 更自然 |
| `DataTrigger` 改 `BorderBrush`（L42-53 等 9 处） | `Style` 里的 `DataTrigger` **在 WinUI 不存在** → `{x:Bind local:Converters.BoolToBrush(ViewModel.IsEcoActive, '#…', 'Transparent'), Mode=OneWay}` | 现成样板：`Predex.WinUI\Converters.cs` 的 `BoolToBrush` 已写好 |
| `Separator`（L270） | `Border Height="1" Background="{ThemeResource …}"` | WinUI 的 `Separator` 在 `Microsoft.UI.Xaml.Controls` 里存在但样式不同，用 Border 更可控 |
| `Run` 内联文本（L34-37） | `TextBlock` + `Run`（WinUI 支持 `Run`）或 `x:Bind` 拼好的字符串 | 推荐后者，少一层绑定 |
| `ComboBox ItemsSource/SelectedItem` | 同名，`x:Bind … Mode=TwoWay` | 无差异 |
| `ShowInTaskbar="False"`、`ResizeMode="NoResize"` | `OverlappedPresenter.IsResizable/IsMaximizable/IsMinimizable` + `SetBorderAndTitleBar(false,false)` + `WS_EX_TOOLWINDOW` | 现实验壳 L108-119 已实现 |

**单窗口形态下这一页怎么变**：这不再是「一个窗口」，而是**壳的 Home 页 + 全局标题栏 + 全局导航栏**。三张卡片原样搬进 Home 页，底部 5 个按钮改成左侧 `NavigationView` 菜单项（Lighting→灯效、Overlay→指标、Fans→风扇、Tuning→调校、Quit→FooterMenuItems），Settings 从标题栏按钮改成菜单项。

---

### 3.2 `Views\SettingsWindow.xaml`（162 行）— 设置

**自定义控件**：`SettingDropdownRow` ×3、`SettingSwitchRow` ×3、`SettingSliderRow` ×1、`FluentIcon`、`WindowTitleBar`

**页内结构**（2 张卡片，全在 `ScrollViewer` 里，无代码后置布局）

| 卡片 | 行 |
|---|---|
| General | 语言下拉（5 语言，`SelectionChanged` 代码事件）· 主题下拉（Dark/Light/Glass，双向绑 `SelectedThemeIndex`）· Run on Startup · Acer Services（`OnText/OffText` + `SwitchWidth=120`）· Battery Limit（80%/100%）· USB Power-Off Charging（`SettingSliderRow` 0..30，step 10）+ 状态 `TextBlock`（空串时 `DataTrigger` 隐藏） |
| Power Profiles | On Battery 下拉（`SourceProfileNames`）· Energy Saver on Battery · On AC 下拉 |

**依赖 VM**（16 个）
`SelectedThemeIndex`、`CanChangeRunOnStartup`、`IsRunOnStartupEnabled`、`ToggleRunOnStartupCommand`、`IsAcerServicesEnabled`、`ToggleAcerServicesDebloaterCommand`、`IsBatteryLimitEnabled`、`ToggleBatteryLimitCommand`、`UsbChargingCutoffPercent`、`UsbChargingStatus`、`CanChangeUsbCharging`、`IsBatteryEnergySaverEnabled`、`SourceProfileNames`、`BatteryPowerModeIndex`、`AcPowerModeIndex`

**WinUI 3 映射**

| WPF | WinUI 3 | 备注 |
|---|---|---|
| `SettingSwitchRow`（自绘胶囊） | `ToggleSwitch`（`OnContent`/`OffContent`）或 `ToggleButton` | WinUI `ToggleSwitch` 是滑轨样式，与现在胶囊不同；要保视觉就重写一个 `SettingSwitchRow`（纯 UserControl，成本低） |
| `Slider` 的 `PreviewMouseLeftButtonUp` / `LostMouseCapture` / `PreviewKeyUp`（USB 充电「松手才提交」） | `PointerReleased` / `PointerCaptureLost` / `KeyUp` | 语义一一对应 |
| `ComboBoxItem Tag`（语言） | `ComboBoxItem.Tag` 仍可用 | 无差异 |
| `DataTrigger` 空字符串隐藏 | `{x:Bind local:Conv.NotEmpty(ViewModel.UsbChargingStatus), Mode=OneWay}` | 需要新写一个字符串→Visibility 静态函数 |
| `DynamicResource` 字号/颜色（6 处） | `ThemeResource` | 主题切换必须走 `ThemeDictionaries` |

**难度评价**：全项目最易迁的一页。控件全是标准件，代码后置只有 51 行。

---

### 3.3 `Views\ModeSettingsWindow.xaml`（380 行）— 调校/Tuning

**自定义控件**：`RectangularSwitch` ×5、`SettingSliderRow` ×2、`SettingValueRow` ×5、`SettingDropdownRow` ×1、`FluentIcon`、`WindowTitleBar`

**页内结构**（5 行 Grid）

| 行 | 内容 |
|---|---|
| Row0 | 标题栏 "Tuning" |
| Row1 | 实时状态条 `UniformGrid Columns=4`：CPU / GPU / CPU Fan / GPU Fan（全部 `x:Name` + 代码后置写值） |
| Row2 | `ScrollViewer` 内 5 张卡片：① 性能模式 5 按钮（`UniformGrid`，代码后置高亮）② CPU Scheduling：Power source 下拉 + 4 组 `Slider`+`TextBox`（Max State / EPP / Core Parking Min / Core Parking Max）+ Boost 下拉 + 2 按钮 ③ GPU Overclocking：`RectangularSwitch` + 2 个 `SettingSliderRow` + 2 按钮 + 不可用提示 ④ Fans：Fan Policy 文本 + Open Curve Editor ⑤ `Expander "Advanced"`（默认折叠）：Enable Tuning 开关 + `SettingDropdownRow` + CPU Power Limits（PL1/PL2 `SettingValueRow` + Tau `Slider`）+ PROCHOT（开关 + `SettingValueRow`）+ GPU Power Limits（Power `SettingValueRow` + Delta/DynamicBoost/PlatformOffset 三个 `Slider`） |
| Row3 | 校验结果 `TextBlock` |
| Row4 | footer `UniformGrid Columns=3`：Reset / Apply / Save |

**依赖 VM**（只有 6 个，其余全走代码后置）
`IsKernelDriverSupported`、`IsHardwareTuningEnabled`、`IsProchotOffsetEnabled`、`CanToggleProchotOffset`、`CanEditProchotOffset`、`IsGpuProfileTuningAvailable`

**WinUI 3 映射**

| WPF | WinUI 3 | 备注 |
|---|---|---|
| `Slider` + `TextBox`（4 组，共 8 个 Slider、2 个 TextBox） | `Slider` + `NumberBox` | `NumberBox` 自带范围/步进/`ValueChanged`，比 `TextBox` + 手动钳制好；**但** `SettingSliderRow` 内部要重写绑定逻辑（双向+防抖） |
| `Expander`（L266） | `Expander`（WinUI 内置） | 直接换命名空间即可 |
| `RectangularSwitch`（自绘 `ToggleButton`） | `ToggleSwitch` 或 `ToggleButton` 重写 | 5 处，建议重写一个 WinUI 版 `RectangularSwitch`（`ToggleButton` 派生 + 两段模板），保持视觉与手感冒 |
| 隐藏的 `ComboBox`（L109-115，`Height=1 Width=1 Visibility=Collapsed`，为 UIA 兼容留的模式选择入口） | **删掉**，改用 `AutomationProperties.AutomationId` 直接标在模式按钮上 | 这是给自动化测试用的 hack，WinUI 里按钮本身可带 `AutomationId` |
| `ToolTip`（L70/127/212） | `ToolTipService.ToolTip` | 换属性名 |
| 3 个 footer 按钮 / `WindowFooterPanelStyle` | `Grid` 3 列 + 统一样式 | — |

**风险**：`.cs` 882 行几乎全是「读控件 → 写硬件 → 回写控件」，迁移时**逻辑可原样保留**，但要逐控件替换事件签名（`RoutedEventArgs` → `RoutedEventArgs`，`SelectionChangedEventArgs` 同名）。

---

### 3.4 `Views\FanCurveWindow.xaml`（146 行）— 风扇曲线

**自定义控件**：`WpfFanCurveControl`（L77）、`LabeledTabSwitcher`（L66）、`SettingValueRow` ×8、`SettingSliderRow` ×1、`SettingDropdownRow` ×1

**页内结构**（标题栏 + ScrollViewer + footer）

| 区 | 内容 |
|---|---|
| 顶部 | Profile Mode 下拉（`FanCurveModeNames`）· 3 段提示文本 · `UniformGrid Columns=2` 两个 `CheckBox`（Load prediction / Closed-loop RPM trim）· `SettingValueRow` Prediction lead · Live RPM 只读 |
| 中 | 进程绑定 `TextBox`（多行）· `UniformGrid Columns=4` 4 个 `RadioButton`（Auto/Max/Custom/Curve，`SegmentedTabRadioStyle`）· `LabeledTabSwitcher`（CPU Fan / GPU Fan） |
| `CurveEditor` | `SettingSliderRow` Fan Control Delay · `WpfFanCurveControl Height=260` · 3 个 `SettingValueRow`（Temp/Duty/RPM） |
| `CurveEditorPanel`（默认 Collapsed） | 2 个 `Canvas Height=180`（CPU/GPU 拖点，`MouseLeftButtonDown/Move/Up` + `Tag="cpu"/"gpu"`）· 2 个 `CheckBox`（Clamp to Grid / Auto Apply）· Apply/Reset · `Expander "Precise Input"`（曲线点文本 + 3 个 `SettingValueRow` 斜率/迟滞） |
| footer | `UniformGrid Columns=3`：Reset / Apply / Save |

**依赖 VM**：只 `FanCurveModeNames`（其余全靠代码后置 661 行）

**WinUI 3 映射**

| WPF | WinUI 3 | 备注 |
|---|---|---|
| `WpfFanCurveControl : FrameworkElement` + `OnRender`（L90） | **无 `OnRender`**。三条路：① `Canvas` + `Polyline`/`Line`/`Ellipse` 手工搭 ② `CanvasControl`（Win2D，**新增 NuGet 依赖**，违反「不新增依赖」约定，需批准）③ 用 `Path` + `Geometry` 拼 | 推荐 ①：本控件画的东西就 1 条折线 + 网格 + 8 个圆点 + 轴标签，Canvas 完全够，且能复用拖拽命中测试 |
| `Canvas` 的 `MouseLeftButtonDown/Move/Up` | `PointerPressed/PointerMoved/PointerReleased` + `CapturePointer` | 坐标从 `e.GetCurrentPoint(canvas).Position` 取 |
| `FrameWorkElement.OnMouseLeftButtonDown` 命中测试 | 自己算最近点（阈值 ~10px） | 逻辑可照抄 |
| `RadioButton GroupName` 分段样式 | 相同（WinUI `RadioButton` 有 `GroupName`） | 视觉模板要重写（WinUI `RadioButton` 是圆点） |
| `LabeledTabSwitcher` | 重写为 `RadioButtons`（WinUI 内置 `RadioButtons` 控件）或自绘 Grid 两段 | `RadioButtons` 是 WinUI 3 里最接近的现成件 |
| `Viewbox`（本页无，灯效页有） | `Viewbox` 存在 | — |

**难度评价**：中偏难。难点不在布局，在**折线控件的重画 + 拖拽交互重做**。好在逻辑独立，可单独验收（拖点后 `TextBox` 数字跟着变）。

---

### 3.5 `Views\KeyboardWindow.xaml`（792 行）— 灯效

**自定义控件**：`ColorWheelControl` ×5、`RgbKeyboardLayout`（L725）、`LabeledTabSwitcher`（L151）、`SettingSwitchRow` ×4、`FluentIcon`、`HexRgbColorConverter`（页面资源）

**页内结构**（4 大块，靠 `Visibility` 绑定互斥显示）

| 块 | 内容 |
|---|---|
| 设备标签栏 | `ListBox ItemsSource={Binding LightingDevices}` + `ItemsPanel=UniformGrid Rows=1 Columns={Binding … LightingDeviceTabColumns}`（`RelativeSource AncestorType=ListBox`）+ 每项 `RadioButton` 分段样式（受 `ShowLightingDeviceTabs` 控显） |
| 回退标签 | `LabeledTabSwitcher`（Keyboard/Logo，`ShowLegacyTargetTabs`）+ Backlight Timeout 卡片（`Slider` 0..120 + `KbdTimeoutDisplay`） |
| Section 1 键盘 | Brightness `Slider` · Lighting Mode 下拉（Static/Dynamic/Software）· `PanelStaticControls`（Sync All Zones 开关、19 项预设 `ComboBox`、4 区色块 + `ColorWheelControl` + Hex `TextBox`）· Dynamic 硬件效果（速度 `Slider`）· Software 效果（速度 `Slider`） |
| Section 2 Logo | Sync With Keyboard 开关 · 亮度 `Slider` · Logo 效果模式下拉 · `ColorWheelControl` ×1 + 预设 · 动画速度 |
| Section 3 OEM 设备 | Sync 开关 · Lighting Mode 下拉（`DisplayMemberPath="Name"`）· 亮度 `Slider` · 动画速度 `Slider` · Random colors 开关 · 模式-功耗模式配色 `ItemsControl`（`UniformGrid Columns=5`）+ `ColorWheelControl` · 设备颜色 `ColorWheelControl` · `RgbKeyboardLayout`（在 `Viewbox` 里）· LED `ListBox`（`UniformGrid Columns=4`）+ 单键 `ColorWheelControl` |

**依赖 VM**（70+，本页最重；摘主要）
`LightingDevices`、`SelectedLightingDevice`、`LightingDeviceModes`、`SelectedLightingDeviceMode`、`LightingLedEditors`、`SelectedLightingKeys`、`SelectedLightingKey`、`DeviceLighting*`（Brightness/ColorHex/IndividualColors/RandomColors/SpeedLevel/SpeedMin/SpeedMax）、`ModePowerModeColorEditors`、`SelectedModePowerModeColor`、`Show*` 系列 11 个可见性开关、`IsLegacyKeyboardEditorVisible` / `IsDeviceLightingEditorVisible`、`KbdTimeoutSeconds`、`RgbBrightness`、`RgbSpeed`

**WinUI 3 映射**

| WPF | WinUI 3 | 备注 |
|---|---|---|
| `ColorWheelControl : FrameworkElement` + `OnRender`（L136） | `ColorPicker`（含色轮形态，`IsColorSpectrumVisible` + `ColorSpectrumShape=Ring`）**或**保留自绘 | `ColorPicker` 输出 `Windows.UI.Color`、内置 HSV/Hex/RGB 页签；本页到处是 `#RRGGBB` 字符串，需要一个 `Color`↔hex 双向绑定层。**视觉会变**（`ColorPicker` 自带边框/浮出），需要产品确认 |
| 自绘 `RgbKeyboardLayout`（335 行，`OnRender` + 字形绘制） | 重写：`Grid`/`Canvas` + 每键一个 `Border`+`TextBlock`（键位表本来就是数据驱动的），或 `ItemsRepeater` + 绝对定位 | `FormattedText` 在 WinUI 不可用；`TextBlock` 更省事。键位几何数据可照抄 |
| `ListBox` + `ItemsPanel=UniformGrid`（L112、L734） | `GridView` 或 `ItemsRepeater` + `UniformGridLayout` | `ItemsRepeater` 更轻，但要注意虚拟化对「选中态」的影响 |
| `ItemsControl`（L670，模式-功耗配色按钮） | `ItemsRepeater` 或 `ItemsControl`（WinUI 有 `ItemsControl`） | 5 个固定项，直接 `ItemsControl` 即可 |
| `Viewbox`（L724） | `Viewbox` | 存在 |
| `HexTextBoxStyle` / `ZoneColorButtonStyle`（`Styles\Controls.xaml`） | 重写为 WinUI Style | 142 个 `DynamicResource` 的设计系统要整体重做一遍 |
| 55 处 `DynamicResource` | `ThemeResource` | 本页最密集 |

**难度评价**：最难。三个自绘/半自绘件（色轮 ×5、键盘图、4 区/单键色块）+ 70 个绑定 + 55 个动态资源。**建议放到 S5，前面把设计系统和行控件都稳定了再做**，否则会反复返工。

---

### 3.6 `Views\MetricsWindow.xaml`（142 行）— 指标/覆盖层配置

**自定义控件**：`RectangularSwitch` ×2、`SettingDropdownRow` ×3、`FluentIcon`、`WindowTitleBar`

**页内结构**（2 张卡片）

| 卡片 | 内容 |
|---|---|
| Display & Appearance | 总开关 `RectangularSwitch` · Theme 下拉（Dark/White）· HUD Scale `Slider` 50..200 · Background Opacity · Text Opacity · Horizontal/Vertical Position 下拉 · Show FPS 开关 |
| Overlay Metric Slots | "Add Slot" 按钮 + `StackPanel x:Name="PanelColumnsContainer"`——**槽位行是代码后置动态 new 出来的**（`MetricsWindow.xaml.cs` L178/L202/L209/L218/L254 手搭 `TextBlock`/`ComboBox`/`Button`） |

**依赖 VM**：XAML 里 0 个绑定，全走代码后置 → 迁 WinUI 时这一块几乎零阻力。

**WinUI 3 映射**

| WPF | WinUI 3 | 备注 |
|---|---|---|
| 代码后置 new 控件拼行 | 同样可行（WinUI 控件 API 类似），或改用 `ItemsRepeater` + `DataTemplate` | 后者更「WinUI 化」，但要先把 VM 槽位集合暴露出来；建议先原样搬，后续再重构 |
| `Slider ValueChanged`（3 个，实时改覆盖层） | 相同 | 直接改 |
| `SettingDropdownRow` | 同 §3.2 | — |

**难度评价**：全项目最易（与设置页并列）。

---

### 3.7 `Views\CustomModalDialog.xaml`（71 行）— 模态确认框

**自定义控件**：`WindowTitleBar`（`CloseClick` 事件 + 隐藏最小化/最大化）

**页内结构**：`Window.Resources` 手动 merge 5 个 `pack://application:,,,/Predex;component/Styles/*.xaml`（L21-31）→ 标题栏 + 消息文本 + `UniformGrid Columns=2`（Cancel / Confirm）

**关键事实**：它是**同步模态**（`ShowDialog()` 返回 `bool?`），调用点：
- `MainViewModel.Gpu.cs:315-324`（`Owner = System.Windows.Application.Current?.MainWindow`）
- `MainViewModel.Gpu.cs:410-429`
- `CustomModalDialog.xaml.cs:28-36`（`App.SuppressAutoHide` 保护 + `finally` 还原）

**WinUI 3 映射**

| WPF | WinUI 3 | 备注 |
|---|---|---|
| `Window.ShowDialog()`（同步模态 + `Owner`） | **无对应物**。`Window` 没有 `Owner`，也没有 `ShowDialog` | 两条路：① `ContentDialog` + `XamlRoot`（单窗口内嵌，异步 `ShowAsync()`）② 独立 `Window` + Win32 `SetWindowLongPtr(GWLP_HWNDPARENT)` 伪 owner，但仍是异步 |
| `ContentPresenter` + `WindowChrome` 标题栏 | 不需要：`ContentDialog` 自带标题/按钮区 | 视觉风格会变（更 Fluent），需产品接受 |
| 同步返回值 | `ContentDialogResult`（异步） | **必须改 VM**：`ShowDialog()` → `await _dialogs.ConfirmAsync(...)` |

**结论**：这是**唯一必须动 VM 的地方**。落地方案见 §7.4。

---

### 3.8 `Views\InfoWindow.xaml`（86 行）— 信息小窗/Toast

**自定义控件**：无

**页内结构**：`WindowStyle=None` + `AllowsTransparency=True` + `Background=Transparent` + `Topmost` + 圆角 `Border` + `DropShadowEffect` + 状态圆点/旋转环（`Ellipse` + `RotateTransform` + `StrokeDashArray`）+ 标题/详情 + 底部 `ProgressBar`

**WinUI 3 映射**

| WPF | WinUI 3 | 备注 |
|---|---|---|
| `AllowsTransparency=True`（逐像素透明窗） | **无对应物**。WinUI 3 窗口不支持逐像素 alpha | 首选改成**主窗口内嵌 `InfoBar` / `TeachingTip`**（单窗口形态本来就该这样）；确需独立浮窗只能用 `SystemBackdrop` 近似，圆角+阴影会不一样 |
| `DropShadowEffect` | `ThemeShadow` 或不用 | WinUI 无 `DropShadowEffect` |
| `RotateTransform` + 代码 `BeginAnimation` | `Storyboard`（WinUI 支持）或 `Composition` `ScaledRotation` | 现实验壳的 `MainWindow.Animation.cs` 就是 Composition 打法，可照抄模式 |
| `ProgressBar` | `ProgressBar` | 存在 |

**建议**：不要 1:1 迁。单窗口 + 页导航的产品里，这类提示应该是 **`InfoBar`（页内）+ `TeachingTip`（引导）**，独立窗口只在「主窗隐藏到托盘时」才需要——那种情况直接用 `AppWindow` 无边框小窗 + `Mica` 背板，透明效果放弃。

（`ModeToastWindow` 同理：现 154 行代码 + 36 行 XAML，迁成 `InfoBar` 或 `Composition` 浮层。）

---

### 3.9 自定义控件逐个迁移结论

| 控件 | 迁 WinUI 结论 | 工作量 |
|---|---|---|
| `WindowTitleBar` | **重写**。WPF 版靠 `MouseLeftButtonDown` 拖窗口（`DragMove`），WinUI 要 `AppWindow` + Win32；三按钮可直接用系统标题栏，壳内只需 drag region | 小 |
| `FluentIcon` | **重写为附加属性或直接 `FontIcon`**。WinUI 有 `FontIcon`（`Glyph` + `FontFamily="Segoe Fluent Icons"`），`FluentIcon` 可退役；`FluentGlyphs` 常量表**原样保留** | 极小 |
| `FluentGlyphs`（29 个） | 保留。清单：`Add, AdvancedGpu, AutoGpu, Balanced, Brightness, CheckMark, Close, Cpu, Dash, DiscreteGpu, Display, Eco, Fans, Gpu, HybridGpu, Info, Keyboard, Lighting, Maximize, Minimize, Overlay, Performance, PerformanceMode, Quiet, Refresh, Settings, Thermal, Tuning, Turbo` | 0 |
| `ThemedIcon` | 保留（`Control` 基类 WinUI 也有，改命名空间即可） | 极小 |
| `LabeledTabSwitcher` | **重写**。WinUI 最接近的现成件是 `RadioButtons`；若保视觉就自绘 `Control` + 模板 | 小 |
| `RectangularSwitch` | **重写**（`ToggleButton` 派生），WinUI `ToggleSwitch` 手感和胶囊不同 | 小 |
| `ActionButton` | 可直接删（WinUI 有 `AccentButtonStyle`） | 极小 |
| `SettingCard` | **重写**为 WinUI `Border` + 样式（原版只有 30 行 XAML，成本低） | 小 |
| `SettingSliderRow` / `SettingSwitchRow` / `SettingDropdownRow` / `SettingValueRow` / `SettingActionRow` | **重写**（`UserControl`，5 个，合计约 400 行）。WinUI 版应内建：`x:Bind` 友好的强类型属性、`NumberBox` 替代 `TextBox`、`ToggleSwitch` 替代自绘开关 | 中 |
| `ColorWheelControl`（227 行，`OnRender`） | **重写**。`ColorPicker`（`Ring` 谱）优先；保视觉则 Canvas/Shape 重画 | 中 |
| `RgbKeyboardLayout`（335 行，`OnRender`） | **重写**。键位数据表保留，绘制改 `Canvas` + `Border`/`TextBlock` | 中大 |
| `WpfFanCurveControl`（280 行，`OnRender`） | **重写**为 `Canvas` 折线（`Polyline` + `Ellipse`）。类名里的 `Wpf` 也要去掉 | 中 |
| `HexRgbColorConverter` | **重写为静态函数**（`x:Bind` 不支持 `IValueConverter`；`{Binding}` 才支持） | 极小 |
| `TranslateConverter` | **保留 `IValueConverter`**（灯效页 `{Binding DisplayName, Converter=…}` 在 `DataTemplate` 里，`x:Bind` 需 `x:DataType` 才能用） | 极小 |

---

## 4. 跨页「没有对应物」总表

> 这是迁移真正的成本所在。逐条给可执行替代方案。

| # | WPF 特性 | 出现位置 | WinUI 3 有对应物吗 | 替代方案 |
|---|---|---|---|---|
| 1 | `WindowChrome`（`CaptionHeight=0`、`ResizeBorderThickness`、`GlassFrameThickness`、`UseAeroCaptionButtons`） | 6 个 Window 全部（`MainWindow.xaml:3-5`、`SettingsWindow:19-21`、`ModeSettingsWindow:17-19`、`FanCurveWindow:9-11`、`MetricsWindow:19-21`、`CustomModalDialog:17-19`） | **没有** | ① 简单路：`AppWindow.TitleBar.ExtendsContentIntoTitleBar=true` + `SetTitleBar(el)`，三按钮由系统画（`ButtonBackgroundColor=Transparent` 可隐）；② 彻底路：子类化 `WndProc` 处理 `WM_NCCALCSIZE` 把非客户区归零（Dafeiyu `WindowChrome.cs:189-206`），拖拽靠 `PointerPressed` 里调 `SendMessage(WM_NCLBUTTONDOWN, HTCAPTION)`——**注意 `WM_NCHITTEST` 在 WinUI 3 收不到**（`WindowChrome.cs:202-205` 实测注释） |
| 2 | `SizeToContent="Height"` | `MainWindow.xaml:2`、`ModeSettingsWindow:10`、`CustomModalDialog.xaml:10`、`InfoWindow.xaml:6` | **没有** | 固定高度 + `ScrollViewer`；或 `SizeChanged`/`Loaded` 里算 `DesiredSize` 后 `AppWindow.Resize`。**不要**试图等自动布局 |
| 3 | `DynamicResource`（WPF 换主题即时生效） | 全项目 185 处页面级 + `Styles\Controls.xaml` 142 处 | `ThemeResource`（语义相近，但要 `ThemeDictionaries` 才换得动） | 建 `ThemeDictionaries`（Light/Dark/HighContrast 三套 key 同名），页面一律 `{ThemeResource}`；运行时换主题改 `FrameworkElement.RequestedTheme`。现实验壳 `Styles\Resources.xaml` **只有单一深色调色板，没有 ThemeDictionaries**——这是 S0/S1 必修项（照抄 Dafeiyu `Styles\Theme.xaml:12-79`） |
| 4 | `IValueConverter`（`BooleanToVisibilityConverter`、`TranslateConverter`、`HexRgbColorConverter`） | `Styles\Controls.xaml:9-10` 全局注册；灯效页多处 | 有，但**只对 `{Binding}` 有效，`x:Bind` 不吃** | `x:Bind` 用**静态函数**：`{x:Bind local:Conv.BoolToVis(ViewModel.Flag), Mode=OneWay}`（现成样板 `Predex.WinUI\Converters.cs`）。需要跨 `DataTemplate` 的地方（如灯效设备名翻译）保留 `{Binding}` + `IValueConverter` |
| 5 | `DataGrid` | **本项目未使用（全仓 0 处命中）** | 有 `ListView`/`GridView` 方案 | 无需迁移 |
| 6 | Storyboard + `DoubleAnimation`（`ScaleX`/`ScaleY`）做滑杆拇指缩放 | `Styles\Controls.xaml:326-355` | WinUI 的 `VisualState` 支持 `Storyboard` + `Setter Target="x.ScaleX"` | 用 `Setter Target="ThumbScale.ScaleX"` 形式，或整体改 `Composition`（壳动画已是 Composition，风格统一） |
| 7 | `Window.Owner` + `ShowDialog()` 模态 | `MainWindow.xaml.cs:209`、`MainViewModel.Gpu.cs:317/412`、`App.xaml.cs:221-222` | **没有 `Owner`，没有模态** | 抽 `IDialogService` → `ContentDialog`（`XamlRoot` 由壳提供）。`Topmost` 用 `OverlappedPresenter.IsAlwaysOnTop` |
| 8 | `WindowStartupLocation=CenterOwner/CenterScreen` | 全部 | 无 | `DisplayArea.GetFromWindowId(...).WorkArea` 手算（实验壳 `PositionAboveTaskbar()` 已有样例） |
| 9 | `FrameworkElement.OnRender` / `DrawingContext` / `FormattedText` | `ColorWheelControl`、`RgbKeyboardLayout`、`WpfFanCurveControl` | **完全没有** | Canvas + `Shape` 元素树（推荐）/ Win2D `CanvasControl`（要加 NuGet，需批准） |
| 10 | `WindowStyle=None` + `AllowsTransparency=True`（真正透明窗） | `InfoWindow.xaml:7-9` | **没有逐像素透明** | 改为主窗内 `InfoBar`/`TeachingTip`；独立浮窗只能 `SystemBackdrop` 近似 |
| 11 | `RenderOptions.ProcessRenderMode = SoftwareOnly`（WPF 专属，切 GPU 模式时防渲染崩溃） | `MainViewModel.Gpu.cs:248-251, 286` | **没有** | WinUI 无等价开关。要么删（接受风险），要么在切换 GPU 前隐藏窗口（`AppWindow.Hide()`）。**这是需要在真机上验证的行为差异** |
| 12 | `Window.Owner` 关窗级联 | `MainWindow` 的 `_toolWindows` 字典 + `Owner=this` | 无 | 单窗口后自然消失；若保留子窗，自己在主窗 `Closed` 里逐个 `Close()` |
| 13 | `pack://application:,,,/Predex;component/...` | `CustomModalDialog.xaml:24-28` | 资源 URI 不同 | 改 `ms-appx:///Styles/xxx.xaml`（Dafeiyu `App.xaml:11` 写法），且**只该在 `App.xaml` merge 一次**，不要在每页重复 merge（反模式） |
| 14 | `Separator` | 5 处 | 有但样式不同 | 用 `Border Height=1` |
| 15 | `UniformGrid` | 19 处 | **没有** | `Grid` 等宽列（固定列数）或 `ItemsRepeater` + `UniformGridLayout`（动态列数） |

---

## 5. Predex.WinUI 实验壳逐文件裁决

> 裁决口径：**留**=几乎照用；**改**=结构可留但必须重构；**删**=技术债，别带进正式项目。

### 5.1 工程与启动

| 文件 | 行 | 裁决 | 理由 |
|---|---|---|---|
| `Predex.WinUI.csproj` | 87 | **改** | ① `AppxGeneratePriEnabled=false` / `AppxGeneratePrisForPortableLibrariesEnabled=false` / `EnableMsixTooling=false`（L26-28）是 PRI 崩溃的根因 → 改为 `EnableMsixTooling=true`（§6.6）② 补 `DefineConstants=DISABLE_XAML_GENERATED_MAIN` + 自写 `Program.cs`（§6.1）③ 21 个 VM + 8 个 Service 的 `Compile Include` 链接表**结构可留**，但要逐步迁成 `ProjectReference`（见 5.4）④ `WindowsPackageType=None` + `RuntimeIdentifier=win-x64` + `WindowsAppSDKSelfContained=true` 保留 |
| `App.xaml` | 16 | **留** | `XamlControlsResources` + `Styles/Resources.xaml` 的顺序正确；只差 `ThemeDictionaries`（在 Resources.xaml 里补） |
| `App.xaml.cs` | 103 | **改** | 单实例 mutex **名字必须保持** `PredexWinUI_Unique_Mutex_1234`？→ **不**。红线要求「不改单实例 mutex 名」，指的是 WPF 版在用的 `PreySenseApp_Unique_System_Mutex_999`（`App.xaml.cs:111`）。WinUI 版要么沿用同一个名字（推荐，避免两版同时跑），要么明确改名并说明**切换期**共存策略。此外：`--no-autohide` 缺失（截图需要）、崩溃日志路径应用 `AppLogger` 而非工作目录 |
| `app.manifest` | 22 | **留** | `requireAdministrator` + `PerMonitorV2` 与非打包 WinUI 3 完全兼容（Dafeiyu 启动器同款，§6.4） |
| `AssemblyInfo.cs` | 2 | 留 | — |
| `ModeIconData.cs` | 22 | 留 | 纯数据 |
| `Converters.cs` | 49 | **留** | `BoolToVis` / `BoolToBrush` / `TempToForeground` 正是 `x:Bind` 静态函数范式的样板。后续每页需要的函数往这里加 |

### 5.2 兼容垫片（重点）

| 文件 | 行 | 裁决 | 理由 |
|---|---|---|---|
| `Compat\WpfShims.cs` | 243 | **删（有条件）** | 这层是**明确的技术债**：为了不改 WPF VM，在 WinUI 里伪造了 `System.Windows.Threading.Dispatcher`、`DispatcherOperation<T>`、`Application`、`RenderOptions`、`System.Windows.Interop.RenderMode` 共 5 个类型（L11-242）。问题有三：① **命名空间伪装**——`System.Windows.*` 是 WPF 的地盘，长期会让「这个项目还有没有 WPF 依赖」无法用一行 `grep` 判断 ② `RenderOptions.ProcessRenderMode` 是**假实现**（L15-24 只是存了个字段），`MainViewModel.Gpu.cs:248-251` 以为切了软件渲染，实际没切 → **静默的行为差异** ③ `Dispatcher.Invoke()` 同步等待（L135-148）在 UI 线程重入时会死锁，WPF 里也是雷。**退役路线**：把这些呼叫点改成注入的 `IDispatcher`/`IUserPrompt` 抽象（约 20 处调用，见 5.4），垫片即可整文件删除。**阶段性可留**：S0–S3 期间留着能省事，但每迁一页就消掉对应调用点，S7 必须清零 |
| `Compat\AppCompat.cs` | 13 | **删** | 伪造 `PreySense.Frontend.App`（`SuppressAutoHide` + `Current.Dispatcher`）。真实需求只有两件事：抑制自动隐藏（→ 壳的一个 bool 属性）、拿 UI 调度器（→ `DispatcherQueue`）。用同名类型假装是 WPF 的 `App` 会让「谁是应用入口」永久混乱 |
| `Views\CustomModalDialogCompat.cs` | 27 | **删** | 用 Win32 `MessageBoxW` 冒充自绘对话框。外观不跟主题（`HANDOVER` 已知问题 5 自认）、无法截图对比、且**绕开了真正要解决的问题**（VM 里 `ShowDialog()` 的同步语义）。正确解：`IDialogService` + `ContentDialog`（§7.4） |
| `Views\ModeToastCompat.cs` | 33 | **改** | 目前只 `Debug.WriteLine`。改成主窗内 `InfoBar`/`Composition` 浮层；API 面（`ShowConfirmedMode(byte,string)`）可保留 |
| `Views\StubViews.cs` | 114 | **删** | 5 个占位 Window 是「每功能一个 Window」思路的残留。单窗口形态下它们全部消失，被 `Pages\*.xaml` 取代 |

### 5.3 壳层

| 文件 | 行 | 裁决 | 理由 |
|---|---|---|---|
| `MainWindow.xaml` | 338 | **改** | 现版本是**把 WPF 主窗硬翻译了一遍**：磁贴用手写 5 列 Grid（L95-167）、图标用 `TextBlock` 硬编码 emoji（`▲ ☽ ◈ ⚡ 🔥`，L45/113-161）而不是 `FontIcon`、颜色写死 `#18181818` 而非资源。**可留**：`ContentPanel` 圆角壳、`DragRegion` 结构、`MicaBackdrop`。**要改**：加 `NavigationView` + 页宿主，磁贴抽成 `ModeTile` UserControl，图标换 `FontIcon` |
| `MainWindow.xaml.cs` | 255 | **改** | 可留：`SetupWindowChrome()`（L99-126，`ExtendsContentIntoTitleBar` + `WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE` + DWM 圆角）、`PositionAboveTaskbar()`（L128-135）、托盘/键盘钩子/HID 装配（L60-80）。**要改**：① `ShowSubWindow<T>()`（L235-247）+ `_subWindows` 字典 → 换成 `NavigateTo(PageTag)` ② `Window_Deactivated`（L204-208）**根本没被订阅**（`Activated` 只接了 `OnWindowShown`，L86）→ 失焦自动隐藏实际没生效 ③ `AppWindow_Changed`（L193-202）是空实现注释占位 ④ 托盘/钩子/内存管理应移出窗口，进 `App` 或 `ShellService` |
| `MainWindow.Animation.cs` | 110 | **改（保留思路）** | Composition 贝塞尔滑入（`(0.16,1.4)→(0.30,1.0)`，L49-51）+ `ScopedBatch` 收尾（L96-107）是干净的好东西，**直接留**。要改的只有两处：目标元素从 `ContentPanel` 变成「页宿主的进入动画」（单窗口下每次切页都该有），以及窗口高度硬编码 490 的 fallback（L42/74）应读实际尺寸 |
| `Styles\Resources.xaml` | 266 | **改** | 颜色令牌（L9-31）+ 6 个按钮 Style（L71-264）质量不错，可作 WinUI 设计系统的**起点**。缺三样：① `ThemeDictionaries`（Light/Dark/HighContrast）② 排版/间距令牌（WPF 有 `Typography.xaml` / `Spacing.xaml` / `Cards.xaml` / `Controls.xaml` 四份，这里只有一份）③ 行控件样式（`SettingXxxRow` 全无） |

### 5.4 服务层

| 文件 | 行 | 裁决 | 理由 |
|---|---|---|---|
| `Services\ThemeManager.cs` | 79 | **改** | 只有 `ApplicationTheme` 存值 + `_hostWindow`，没有真正驱动 `RequestedTheme`/`ThemeDictionaries`。要重写成「切主题 = 改 `FrameworkElement.RequestedTheme` + 换窗口背景」 |
| `Services\TrayIconService.cs` | 200 | **留** | 原生 `Shell_NotifyIcon` + `WndProc` 子类化，非打包应用唯一正路（`Application.Current` 拿不到 `NotifyIcon`）。需真机验证点：WinUI 窗口子类化后托盘回调是否稳定收到 |
| `Services\KeyboardHookService.cs` | 106 | **留** | 低级键盘钩子 `WH_KEYBOARD_LL`，与 UI 框架无关。**红线**：`VK_PREDATOR=0xCC` / `VK_MODE_KEY=0xC3`（L20-21）要与 WPF 版一致，别「顺手改」 |
| `Services\HidDeviceArrivalWatcher.cs` | 63 | **改** | 只注册了通知，**没处理 `WM_DEVICECHANGE`**（`HANDOVER` 已知问题 5）。要么补完，要么去掉（灯效页有手动刷新也算能用） |
| `Services\MemoryManager.cs` | 69 | **改（先量再改）** | `GC.Collect` + `SetProcessWorkingSetSize` 思路对，但 `HANDOVER` 自认「没实测过隐藏后数值」。**S1 验收必须量**：隐藏 30 秒后 `Get-Process Predex \| Select WorkingSet64`。若仍 >100MB，说明 `Microsoft.UI.Xaml` 原生内存是大头 → 考虑隐藏时 `Window.Content = null` 或销毁重建 |
| `Services\LocalizationService.cs` | 53 | **改** | 现在是「读 JSON，失败就把 key 原样返回」，与 WPF 的 `UiText`（1259 行，4 语言硬编码在 C# 里）**不是同一套数据源**。必须统一：`UiText` 是唯一文案源（红线 7），WinUI 侧直接用链接过来的 `UiText.cs`，删掉另一个 JSON 通道 |
| `Services\UpdateCheckService.cs` | 70 | **留** | 去掉 `AppNotificationBuilder` 后直接用浏览器打开 release 页，非打包应用的正确做法 |
| `Services\WindowVisibilitySession.cs` | 45 | **改/删** | 单窗口后「哪个子窗在前台」的整套逻辑（`HiddenWindows`/`PreferredWindow`）失去意义。建议删 |

### 5.5 对「链接 ViewModel」策略的评估（关键判断）

**现状**：csproj L44-67 链接 21 个 VM 文件，csproj L70-79 链接 8 个 Service 文件。`HANDOVER` 第 4 节把这评为「优点：业务逻辑只有一份」。

**这个判断只对了一半**：

| 维度 | 评价 |
|---|---|
| 编译期能不能撑住 | **能**，但靠的是 `WpfShims` + `AppCompat` + `CustomModalDialogCompat` + `ModeToastCompat` 四个替身。替身越多，「链接」越像是「假装没依赖」 |
| 单窗口导航形态下还撑不撑得住 | **撑不住**。证据：`MainViewModel.Gpu.cs:315-334` 里 `new Views.CustomModalDialog{ Owner = Application.Current?.MainWindow }` + 同步 `ShowDialog()`。单窗口**没有** `MainWindow` 可当 Owner，也**没有**同步模态。这不是命名空间问题，是**交互模型冲突** |
| 链接带来的真实收益 | 21 个 VM 文件里，与视图强耦合的只有 **3 个文件 4 处**（`MainViewModel.Gpu.cs` 的 `RenderOptions` ×2 + `CustomModalDialog` ×2、`MainViewModel.Theme.cs` 通过 `ThemeManager`、`MainViewModel.cs:303` 的 `App.Current?.Dispatcher`）。其余全是 `IHardwareService` + `ObservableProperty` |
| 正确路线 | **保留链接，但把强耦合点抽成接口**：新增 `PreySense.Frontend\Services\IUserPromptService`（`Task<bool> ConfirmAsync(...)`）与 `IUiDispatcher`（`bool HasAccess` / `void Post(Action)` / `Task InvokeAsync(Func<Task>)`），在 `MainViewModel` 构造注入（默认实现走 WPF，WinUI 侧注入自己那份）。这样：① 链接照旧 ② `WpfShims`/`AppCompat`/`CustomModalDialogCompat` 全部可删 ③ WPF 版行为不变（默认实现就是现在的逻辑） |
| 长期 | S7 之后可考虑把 VM 抽成独立 `Predex.Core.ViewModels` 类库（**不引用任何 UI 框架**），WPF 和 WinUI 各引一次。但这是**可选优化**，不是迁移前提——S7 前不要动，避免双线作战 |

---

## 6. Dafeiyu-Go 可照抄做法

> 目标仓库：`G:\DeepSeek DSH\DSH Works\Project\Dafeiyu-Go`
> 两个 WinUI 3 应用都是**非打包 + 单窗口 + 多页**，与本项目目标形态一致。

### 6.1 自建 `Program.cs` 入口（非打包必需）

| 项 | 文件 | 行 | 要点 |
|---|---|---|---|
| 关掉 XAML 生成的 `Main` | `DshInstaller.csproj` | L20 | `<DefineConstants>$(DefineConstants);DISABLE_XAML_GENERATED_MAIN</DefineConstants>` |
| 自写 `[STAThread] Main` | `DshInstaller\Program.cs` | L16-149 | 顺序：解析命令行 → `TryInitializeRuntime()` → `WinRT.ComWrappersSupport.InitializeComWrappers()` → `Application.Start(...)` → `new App()` |
| 初始化同步上下文 | `Program.cs` | L123-127 | `DispatcherQueue.GetForCurrentThread()` + `SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(q))`——**漏这步 await 回来的线程会错** |
| 运行时引导 | `Program.cs` | L271-294 | 非自包含走 `Bootstrap.Initialize(0x00010008)`（主次版本号编码）；自包含走 `#if SELF_CONTAINED_WINAPPSDK return true;`——**注释明确：自包含包里再调 bootstrap 会 `0xC000027B` 直接崩**（L275-281）。本项目是 `WindowsAppSDKSelfContained=true`，**必须走 skip 分支** |
| 启动期崩溃落盘 | `Program.cs` | L176-210 | 每步 `LogStep`，`Describe()` 连 `exception.StackTrace` 都要 try/catch（注释 L224-227：程序被挪走后取堆栈会抛 `FileNotFoundException`，导致「报错本身把程序搞崩」） |
| 致命错误弹框 | `Program.cs` | L296-310 | 用 `System.Windows.Forms.MessageBox`（`FrameworkReference Microsoft.WindowsDesktop.App.WindowsForms`，L52）——比 WinUI 对话框更可靠，因为可能 WinUI 还没起来 |

**对本项目的意义**：`Predex.WinUI` 现在是靠 XAML 生成的 `Main` + `WindowsPackageType=None`。S0 必须补上 `Program.cs` + `DISABLE_XAML_GENERATED_MAIN`，否则自包含发布路径会撞 `0xC000027B`。

### 6.2 App / 单例窗口

| 项 | 文件 | 行 | 要点 |
|---|---|---|---|
| 极简 `App` | `DshInstaller\App.xaml.cs` | L8-45 | 只做：`PlatformStyle.ApplyApplicationResources(Resources)` → `new MainWindow()` → 存静态 `MainWindowInstance`（L11，页面靠它跳转）→ `Activate()` |
| 全局异常落盘 | 同上 | L27-44 | `OnLaunched` 整体 try/catch，写 `installer-crash.log` 后 rethrow |
| 静态窗口入口 | 同上 | L10-11 | `internal static MainWindow MainWindowInstance { get; private set; }` —— 页面里 `App.MainWindowInstance.Navigate(...)`。**简单直接，比 DI 容器更适合这种 6 页规模** |

### 6.3 自定义标题栏（两种打法，都验证过）

| 打法 | 文件 | 行 | 效果与代价 |
|---|---|---|---|
| **A. Frame + `ExtendsContentIntoTitleBar`** | `DshInstaller\MainWindow.xaml.cs` | L857-902 | `OverlappedPresenter.IsResizable=false`（L870）；**故意不设 `IsMaximizable=false`**（L872-877 注释：设了系统会画禁用态灰按钮，且不吃 `ButtonForegroundColor`，留个灰方块）；真正禁最大化靠 `NativeMethods.RemoveMaximizeButton`（L889） |
| **B. `WM_NCCALCSIZE` 归零非客户区** | `DshInstaller\WindowChrome.cs` | L92-209 | `Attach(hwnd, dragHeight, buttonWidth)`；`WM_NCCALCSIZE` 返回 0 不改矩形 → 客户区=整个窗口 → 系统没地方画三按钮（L189-197）；`WM_NCACTIVATE` 返回 1（L199-200）；**拖拽不能用 `WM_NCHITTEST`**（L202-205 实测：命中测试跑在 ContentIsland，顶层窗口收不到），改 `NativeMethods.BeginDragMove` 在 `PointerPressed` 里调 |
| 拿回投影/圆角 | 同上 | L128-158 | 保留 `WS_THICKFRAME|WS_CAPTION|WS_SYSMENU|WS_MINIMIZEBOX` 让 DWM 画框，再 `DwmExtendFrameIntoClientArea` 1px（L146-148）+ `DWMWA_WINDOW_CORNER_PREFERENCE=DWMWCP_ROUND`（L151-153） |
| **更简单的一派：纯 `ExtendsContentIntoTitleBar`** | `Click-To-Run\source\SettingsWindow.xaml.cs` | L147-148 | 只有 `ExtendsContentIntoTitleBar = true; SetTitleBar(AppTitleBar);` 两行，系统三按钮保留（默认 Fluent 外观）。**推荐本项目用这一派**——Predex 的主窗是圆角弹出面板，不需要 Win11 标题栏按钮的定制折磨 |

### 6.4 主题资源字典组织

| 项 | 文件 | 行 | 要点 |
|---|---|---|---|
| merge 结构 | `DshInstaller\App.xaml` | L6-14 | `XamlControlsResources` + `<ResourceDictionary Source="ms-appx:///Styles/Theme.xaml" />`。**只在 App 层 merge 一次** |
| Light/Dark 双套 | `DshInstaller\Styles\Theme.xaml` | L12-79 | `<ResourceDictionary.ThemeDictionaries>` 里 `x:Key="Light"`（L15-45）与 `x:Key="Dark"`（L48-77），**同名 key 两套值**：`CardBackgroundBrush`、`PrimaryTextBrush`、`SecondaryTextBrush`、`TertiaryTextBrush`、`AccentBrush`、`OnAccentBrush`、`DividerBrush`、`HoverBrush`、`PressedBrush`、`Success/Warning/Danger(+Soft)` |
| 与主题无关的令牌放外面 | 同上 | L81-88 | `CornerRadius`、`FontSize` 等不进 ThemeDictionaries |
| 页面一律 `ThemeResource` | 同上 | L6 注释 | 「一律用 ThemeResource 取，别写死色值」 |
| 运行时切主题 | `MainWindow.xaml.cs` | L967-974 | `ActualThemeChanged` 里同步 `SystemBackdropConfiguration.Theme`；`RequestedTheme` 设在根 `FrameworkElement` 上（L284 `RequestedTheme = ElementTheme.Default`） |
| Mica → Acrylic 回退 | 同上 | L904-965 | `MicaController.IsSupported()` → 失败退 `DesktopAcrylicController`（`DesktopAcrylicKind.Thin`）→ 再失败用纯色背板（`ApplyShellFallbackBackground`）。**这套回退链本项目要照抄**，因为 Glass 主题就是这个需求 |

**没有 HighContrast 专门字典**——两套靠系统默认。本项目若要 4 语言 + 3 主题，建议 Light/Dark 两套 + 允许系统高对比度覆盖（`ThemeDictionaries` 里可加 `x:Key="HighContrast"`）。

### 6.5 非打包 + `requireAdministrator`

| 事实 | 证据 |
|---|---|
| WinUI 3 非打包 + `requireAdministrator` **可以正常工作** | `Click-To-Run\source\app.manifest:8` `<requestedExecutionLevel level="requireAdministrator" uiAccess="false" />`，同一 csproj 是 `WindowsPackageType=None`（`DeepSeekHarness.csproj`，PropertyGroup 里） |
| 不需要特殊开关 | 两个 csproj 都没有为提权加任何额外属性；兼容性靠 `app.manifest` 的 `trustInfo` |
| 另一派：`asInvoker` + 运行期自提权 | `DshInstaller\app.manifest:11` 是 `asInvoker`，注释（L6-7）说明「需要管理员时由 `ElevationHelper` 用 runas 拉一次自己」，好处是「仅为我安装」完全不弹 UAC |
| 对本项目的选择 | Predex 主程序**全程需要管理员**（WMI/管道/EC），沿用 `Predex.WinUI\app.manifest` 现在的 `requireAdministrator` 即可——已实测能跑（`HANDOVER` 第 1 节「可编译、可运行」） |
| 计划任务/自启动注意 | `StartupTaskService` 走「提权计划任务」，与 `requireAdministrator` 无冲突；**红线**：计划任务名不能改 |

### 6.6 PRI / 打包配置（← 本项目最该抄的一条）

| 项 | 文件 | 行 | 值 | 说明 |
|---|---|---|---|---|
| `EnableMsixTooling` | `DshInstaller.csproj` | **L16-18** | **`true`** | 注释原文：「**必须开**：关掉的话 `dotnet publish` 不会把 `.xbf`（编译后的 XAML）和 `.pri` 带进输出，运行时会 `XamlParseException: XAML parsing failed`，页面全加载不出来」 |
| `WindowsPackageType` | 同上 | L22 | `None` | 非打包 |
| `WindowsAppSDKSelfContained` | 同上 | L23 | `false`（安装器）/ `false`（启动器） | 框架依赖；本项目是 `true`（自包含），差异只在要不要 `Bootstrap.Initialize` |
| `RuntimeIdentifier(s)` | 同上 | L14 | `win-x64` | 与本项目一致 |
| `AppxGeneratePriEnabled` | — | **未设置**（默认 true） | 本项目**显式设成 false**，正是 PRI 缺失的根因 |
| 产物验证 | `dist\selfcontained\DSH-Installer.pri` | — | 41224 字节 | **构建确实产出了 per-app PRI** |
| 另一个产物 | `Click-To-Run\source\dist\DeepSeek Harness.Core.pri` | — | 156360 字节 | 启动器同样有 |
| 构建用的 SDK | `Dafeiyu-Go-DeepSeek-Harness-Setup\build.ps1:20` | — | `G:\DeepSeek DSH\.tools\dotnet\dotnet.exe`（8.0.425） | 该 SDK 目录下**也找不到** `Microsoft.Build.AppxPackage.dll` —— 说明 **PRI 生成不依赖 VS 的 AppxPackage 任务**，而是 Windows App SDK NuGet 自带的 build targets 在做 |

**可执行结论（S0 的核心动作）**

1. 把 `Predex.WinUI.csproj` 里 `AppxGeneratePriEnabled` / `AppxGeneratePrisForPortableLibrariesEnabled` / `EnableMsixTooling` 三行**删掉或改成对齐 Dafeiyu**（`EnableMsixTooling=true`，另两个不设）。
2. 重新构建，检查输出目录是否出现 `Predex.pri`。
3. **如果仍不生成**：保留 `tools\pri-stub` 的替身 DLL 方案，但把 `makepri` 步骤做成 csproj 的 `AfterBuild` Target（`HANDOVER` 已知问题 1 的待办），用 `$(NuGetPackageRoot)` 定位 `makepri.exe`，暂存 `.xbf` + 两个框架 `.pri` 再 `new`（不能直接对整个输出目录 `new`，会撞 `PRI277 Conflicting values`）。
4. 判据：**全新 clone + 一次 `dotnet build` → 直接双击 exe 能起窗口**，不再需要任何人肉步骤。
5. 顺带：`Microsoft.Build.Utilities.Core 17.11.4` 的 `NU1903` 漏洞告警一并升版本，否则替身方案永远带着一条高危告警。

### 6.7 单窗口页导航的两种实现（都见过，选一个）

| 派别 | 例子 | 文件行 | 机制 | 优点 | 缺点 |
|---|---|---|---|---|---|
| **A. `Frame.Navigate`** | `DshInstaller` | `MainWindow.xaml.cs` L109（`Frame _contentFrame`）、L292-294（塞进 shell Grid）、L1336-1357（`Navigate(page, param)` → `_contentFrame.Navigate(type, param, new DrillInNavigationTransitionInfo())`） | `Frame` + `Page` 类型注册表（`Dictionary<WizardPage, PageInfo>`，L106-107 / L183-260） | 每页独立类、天然有返回栈和转场；页面 `OnNavigatedTo` 取参 | 每次切页**重建**页面（状态不保留）；`Page` 不能用构造参数传依赖，要靠 `App.MainWindowInstance` |
| **B. `NavigationView` + `Visibility` 切页** | `Click-To-Run` | `SettingsWindow.xaml` L800-809（`NavigationView`，`PaneDisplayMode=Left`、`IsSettingsVisible=False`）、L1636-1676（6 个页宿主 `StackPanel` + 5 个 `Pivot`）；`.cs` L5580-5590（`Visibility` 三目）、L5618-5631（`navTag` → 页面/菜单项 的 switch） | 所有页实例**常驻**，靠 `Visibility` 切换；组内再套 `Pivot` 做二级页签 | 状态天然保留（滑杆拖了一半切走再回来还在）；无重建开销；二级页签用 `Pivot` 很自然 | 全部页一次性 XAML 解析 → 启动慢、内存高（该文件 5685 行 XAML）；**不适合本项目「隐藏到托盘 25–35MB」的目标** |

**对本项目的推荐：A 派（Frame）为主，配合「页缓存」**

- Predex 的内存目标是隐藏时 25–35MB（`HANDOVER` 目标 + 已知问题 2）。B 派把 792 行的灯效页常驻在内存里是致命的。
- 采用 `Frame` + 按需 `Navigate`；对「往返频繁且状态重要」的页（调校页的滑杆未提交值）用**独立 `ViewModel` 保存草稿**，而不是靠页实例常驻。
- 页里需要共享的只有 `MainViewModel`（单例，放 `ShellService` 或 `App` 静态属性），不需要 DI 容器。
- 转场用 `DrillInNavigationTransitionInfo`（Dafeiyu 同款）；壳的弹出动画继续用 Composition（`MainWindow.Animation.cs`）。

### 6.8 Click-To-Run 是 WPF 还是 WinUI？

**是 WinUI 3**，不是 WPF。`DeepSeekHarness.csproj`：`<UseWinUI>true</UseWinUI>`、`<UseWPF>false</UseWPF>`、`<WindowsPackageType>None</WindowsPackageType>`、`<EnableMsixTooling>true</EnableMsixTooling>`、`<DefineConstants>DISABLE_XAML_GENERATED_MAIN</DefineConstants>`。

与 `DshInstaller` 的差异只在：`AssemblyName` 带空格、`DISABLE_XAML_GENERATED_MAIN` 没带 `$(DefineConstants);` 前缀（直接覆盖）、多了一堆 `Compile Remove` 排除并行构建产物（注释说明：共享源码目录里有多次构建的生成物，不排除会把旧 `AssemblyInfo`/`XamlTypeInfo` 当源码再编一遍——**本项目若做多配置并行构建也要防这个**）。

---

## 7. 目标架构：单窗口 + 页导航

### 7.1 壳结构

```
App (Program.cs → Bootstrap → Application.Start → App)
└─ ShellWindow  (唯一的 Window)
   ├─ TitleBar 区（ExtendsContentIntoTitleBar + SetTitleBar；左侧品牌+版本，右侧温度摘要）
   ├─ NavigationView
   │   ├─ MenuItems:  Home / Tuning / Fans / Lighting / Overlay / Settings
   │   ├─ FooterMenuItems: Quit
   │   └─ Content: Frame  ← 页宿主
   └─ （全局 InfoBar 区：模式切换提示 / 错误 / 进展）
```

### 7.2 页面清单（对应现 8 个界面）

| 页 | Tag | 来源 WPF 界面 | 页类 |
|---|---|---|---|
| 首页 | `Home` | `MainWindow.xaml` | `Pages\HomePage.xaml` |
| 调校 | `Tuning` | `ModeSettingsWindow.xaml` | `Pages\TuningPage.xaml` |
| 风扇 | `Fans` | `FanCurveWindow.xaml` | `Pages\FansPage.xaml` |
| 灯效 | `Lighting` | `KeyboardWindow.xaml` | `Pages\LightingPage.xaml` |
| 指标 | `Overlay` | `MetricsWindow.xaml` | `Pages\OverlayPage.xaml` |
| 设置 | `Settings` | `SettingsWindow.xaml` | `Pages\SettingsPage.xaml` |
| 对话框 | — | `CustomModalDialog.xaml` | `ContentDialog`（服务化） |
| 提示 | — | `InfoWindow` / `ModeToastWindow` | 全局 `InfoBar` + `TeachingTip` |

### 7.3 目录规划（新项目建议独立，不塞进 `experiments`）

```
app\Predex.App\                     ← 新 WinUI 3 项目（可先借壳 experiments）
├─ Program.cs                       入口 + Bootstrap（SKIP for self-contained）
├─ App.xaml(.cs)                    资源 merge + 单例 + 全局异常
├─ app.manifest                     requireAdministrator / PerMonitorV2
├─ Shell\ShellWindow.xaml(.cs)      标题栏 + NavigationView + Frame
├─ Shell\ShellWindow.Animation.cs   Composition 滑入滑出（现成可搬）
├─ Shell\ShellServices.cs           托盘 / 键盘钩子 / HID / 内存 / 退出
├─ Shell\IDialogService.cs          契约（放在 Frontend 侧更合适，见 7.4）
├─ Pages\*.xaml(.cs)                6 个页面
├─ Controls\                        WinUI 版行控件 + 3 个重绘控件
├─ Styles\Theme.xaml                ThemeDictionaries（Light/Dark）
├─ Styles\Controls.xaml             按钮/行/卡片样式
├─ Services\                        托盘 / 钩子 / 内存（从 experiments 搬）
└─ Assets\
```

**关键纪律**：项目**不链接** `PreySense.Frontend` 的 XAML/Styles，只链接 `ViewModels\*.cs` 与 `Services\UiText.cs` 等无 UI 依赖的文件。

### 7.4 唯一必须动 WPF 代码的地方：`IDialogService`

当前冲突点（3 处，全部在 WPF 项目里）：

| 位置 | 代码性质 |
|---|---|
| `ViewModels\MainViewModel.Gpu.cs:313-334` | `new Views.CustomModalDialog{ Owner = Application.Current?.MainWindow, … }.ShowDialog()` |
| `ViewModels\MainViewModel.Gpu.cs:405-429` | 同上（另一条 GPU 路径） |
| `Views\CustomModalDialog.xaml.cs:28-36` | `App.SuppressAutoHide = true … finally 还原` |

**建议改法（向后兼容，WPF 行为不变）**：

```
PreySense.Frontend\Services\IUserPromptService.cs   （新增，接口）
    Task<bool> ConfirmAsync(string title, string message, string confirmText, bool showCancel);
    bool Confirm(string ...);   // 同步重载，WPF 默认实现继续弹真窗口
PreySense.Frontend\Services\WpfUserPromptService.cs （新增，默认实现 = 现在的 ShowDialog 逻辑）
MainViewModel 构造函数加可选参数 IUserPromptService? prompts = null
    → null 时 new WpfUserPromptService()
WinUI 侧：WinUIUserPromptService → ContentDialog（XamlRoot 由 Shell 提供）
```

**代价**：WPF 项目新增 2 个小文件 + `MainViewModel` 构造签名加一个可选参数 + 3 处调用点改写。**收益**：`WpfShims` / `AppCompat` / `CustomModalDialogCompat` / `ModeToastCompat` 四个垫片全部可删，WinUI 项目从此不再「假装自己是 WPF」。

### 7.5 内存策略（对齐 25–35MB 目标）

| 措施 | 说明 |
|---|---|
| 页按需创建 | `Frame.Navigate` 不缓存 `NavigationCacheMode`；首页/当前页存在，其余离开即释放 |
| 隐藏时降级 | 托盘隐藏后 `AppWindow.Hide()`；`MemoryManager` 空闲 8s 后 `GC.Collect` + `SetProcessWorkingSetSize` |
| 隐藏时释放重资源 | 若实测仍高：卸载 `Styles` 中的大图、`Frame.Content = null`，显示时重建（**必须先量再决定**，见 `HANDOVER` 问题 2） |
| 灯效页特殊处理 | 该页是内存黑洞（5 个色轮 + 键盘图 + 70 绑定）。切走时务必释放，不要 `NavigationCacheMode=Required` |
| 验收 | 隐藏 30s 后 `Get-Process Predex \| Select-Object WorkingSet64` 记入阶段报告 |

---

## 8. 分阶段路线（S0–S7）

> 每阶段的**共同验收前置**：
> ```powershell
> & "G:\DeepSeek DSH\DSH Works\Project\PredatorSense\tools\dotnet-sdk\dotnet.exe" `
>     build <项目>.csproj -p:Platform=x64 -nodeReuse:false
> ```
> 判据：`0 Error`；截图落到 `docs\winui-<阶段>.png`；硬件写入必须「写前记录 → 写后读回 → 测完还原」。
> WPF 版**每阶段结束都要能 build**（红线：不能为了 WinUI 把在产版本搞挂）。

### S0 — 构建自愈（PRI + 入口 + 主题骨架）

| 项 | 内容 |
|---|---|
| **目标（可验证）** | 全新目录、一条 `dotnet build` 之后，直接双击 exe 能出窗口，不需要任何手工 `makepri` / 拷 DLL 步骤 |
| **改动文件** | `app\experiments\Predex.WinUI\Predex.WinUI.csproj`（改 PRI 三开关、加 `DISABLE_XAML_GENERATED_MAIN`）；新增 `Program.cs`；`App.xaml.cs`（改由 `Program` 启动、补 `--no-autohide`）；`Styles\Resources.xaml` → 拆 `Theme.xaml`（加 `ThemeDictionaries` Light/Dark）；`App.xaml`（`ms-appx:///Styles/Theme.xaml`） |
| **参考** | Dafeiyu `DshInstaller.csproj:16-22`、`Program.cs:104-149 / 271-294`、`Styles\Theme.xaml:12-79`、`App.xaml:6-14` |
| **验收命令** | ① `build` 0 error ② `Test-Path bin\...\Predex.pri` → `True` ③ 启动 exe 出窗口 ④ `Get-Content crash.log` 无新条目 |
| **判据** | 删除 `tools\pri-stub` 依赖后仍能构建（若做不到，则改用 `AfterBuild` 自动 `makepri`，并把该步骤写进 csproj，同样满足「零手工」） |
| **风险** | `EnableMsixTooling=true` 可能触发替身 DLL 缺任务 → 立刻回退：恢复三开关 false + `AfterBuild` `makepri`（`HANDOVER` 第 5 节命令已验证可用） |
| **回滚点** | 单 commit；只动 `app\experiments`，**不碰 WPF 项目** |

### S1 — 主窗口变成真壳（本阶段定生死）

| 项 | 内容 |
|---|---|
| **目标** | 单窗口壳可日常使用：托盘常驻、Predator 键唤出、模式切换写**真硬件**并读回校验；`NavigationView` 里 6 个菜单项都在，只有 Home 有内容 |
| **新建** | `Shell\ShellWindow.xaml(.cs)`（标题栏 + `NavigationView` + `Frame`）、`Shell\ShellWindow.Animation.cs`（搬现成）、`Shell\ShellServices.cs`（托盘/键盘钩子/HID/内存/退出）、`Pages\HomePage.xaml(.cs)`（照 `MainWindow.xaml` 三张卡片）、`Controls\ModeTile.xaml`、`Services\UserPromptService`（WinUI 版，S1 先给"确认"用） |
| **改动** | `MainWindow.xaml(.cs)` → 删除或改名为 `ShellWindow`；`App.xaml.cs` 改成 `new ShellWindow()`；`Program.cs` 里 `--no-autohide` 生效 |
| **必须补的洞**（现实验壳已知缺陷） | ① `Window_Deactivated` **未被订阅** → 失焦自动隐藏没生效（`MainWindow.xaml.cs:86` 只接了 `Activated`）② `AppWindow_Changed` 空实现（L193-202）③ `--no-autohide` 未实现 → 截图截不到 ④ `WS_EX_NOACTIVATE` 是否导致拿不到键盘焦点，**必须点击实测** |
| **验收命令** | ① `build` 0 error ② `pwsh -File docs\capture-ui.ps1 -Exe "<winui 输出>\Predex.exe"` → `docs\winui-s1-home.png` ③ 托盘右键 Quit 能真退出 ④ 隐藏 30s 后 `(Get-Process Predex).WorkingSet64` 记数 |
| **判据（硬件）** | 按 Predator 键 5 次分别切 5 模式；每次 `ViewModel.CurrentPowerMode` 读回与目标一致；测试后恢复用户原模式。若本机无 Acer WMI（Mock），需**明确标注"未在真机验证"**并在有硬件的机器上补 |
| **风险** | ① 单窗口 + 托盘 + 钩子的生命周期比 WPF 复杂 → 建议把三者移出窗口进 `ShellServices`，`Window.Close` 只做隐藏 ② 自包含 + `Bootstrap.Initialize` 双调 → 必崩（`0xC000027B`），S0 已解决 ③ 内存超目标 → 见 §7.5，先量再优化 |
| **回滚点** | 保留 WPF 版为默认启动入口；WinUI 版仍走独立 exe，随时可弃 |

### S2 — 设置页

| 项 | 内容 |
|---|---|
| **目标** | `SettingsPage` 功能与 WPF 版 1:1：语言、主题、开机启动、Acer 服务、电池限充、USB 充电、电源配置 |
| **新建** | `Pages\SettingsPage.xaml(.cs)`、`Controls\SettingDropdownRow` / `SettingSwitchRow` / `SettingSliderRow`（WinUI 版首批）、`Styles\Controls.xaml`（行控件样式） |
| **验收** | build 0 error；截图 `docs\winui-s2-settings.png`；**切语言后全壳文案跟着变且无 raw key**；切 Dark/Light 后主窗与设置页颜色都变（证明 `ThemeResource` 通） |
| **风险** | 行控件是后面 4 页的地基 → 本阶段**宁可慢**，把双向绑定/防抖/焦点/禁用态打磨好；`SettingSwitchRow` 若用 `ToggleSwitch`，视觉与原胶囊不同，需产品一次性拍板 |
| **回滚点** | 设置页只读崩溃也不影响 Home；可先隐藏菜单项上线 |

### S3 — 调校页

| 项 | 内容 |
|---|---|
| **目标** | `TuningPage` 完成 CPU 调度 / GPU 超频 / 风扇入口 / Advanced 折叠区；`Apply` 后读回校验 |
| **新建** | `Pages\TuningPage.xaml(.cs)`、`Controls\SettingValueRow`（`NumberBox` 版）、`Controls\RectangularSwitch`（`ToggleButton` 版）、`Controls\SectionCard` |
| **注意** | 882 行代码后置逻辑**原样搬**，只换事件签名与控件属性名；删掉 L109-115 的隐藏 `ComboBox` UIA hack |
| **验收** | build 0 error；截图 `docs\winui-s3-tuning.png`；Advanced 展开/折叠；4 组「滑杆拖动 → `NumberBox` 同步 → 松手不自动应用」；CPU 调度 Apply 后 `powercfg` 读回一致；GPU 时钟偏移写入后 NVAPI 读回一致（无 N 卡则标注 Mock） |
| **风险** | 一次改太多行控件易连锁失败 → 建议**先只迁布局与只读状态条**（无硬件写入），下一小步再接写入 |
| **回滚点** | 页级开关：`NavigationView` 菜单项可临时隐藏，不影响壳 |

### S4 — 风扇曲线页

| 项 | 内容 |
|---|---|
| **目标** | `FansPage` 完成：曲线图可拖点、8 点数据双向同步、Apply/Save 生效、CPU/GPU 双图切换 |
| **新建** | `Controls\FanCurveEditor`（**用 `Canvas` + `Polyline`/`Ellipse` 重画**，取代 `WpfFanCurveControl` 的 `OnRender`）、`Controls\SegmentedTabs`（取代 `LabeledTabSwitcher` + `RadioButton GroupName`） |
| **验收** | build 0 error；截图 `docs\winui-s4-fans.png`；拖点后 `Temp/Duty/RPM` 三个 `NumberBox` 跟着变；值域钳制（20–110°C / 0–100% / 5% 吸附）正确；Apply 后读回曲线 8 点完全一致；`Restore-AutoFan` 能还原 |
| **风险** | ① Canvas 拖拽命中测试要自己写（WinUI 无 `OnMouseLeftButtonDown`）② 自绘改成控件树后**视觉可能有 1–2px 偏差**，需截图对照 WPF 版 ③ 曲线写入是硬件动作 → 每次测试后必须还原 Auto |
| **回滚点** | 页面独立；曲线编辑失败时保留「精确输入」文本框作为兜底操作路径 |

### S5 — 灯效页（最大的一块）

| 项 | 内容 |
|---|---|
| **目标** | `LightingPage` 完成 4 大块：设备标签、键盘 4 区/软件效果、Logo、OEM 设备（含键盘图与单键编辑） |
| **新建** | `Controls\ColorWheel`（先试 `ColorPicker` Ring；视觉不可接受则 Canvas 重画）、`Controls\RgbKeyboardLayout`（Canvas + `Border`/`TextBlock`）、`Converters\HexColor`（`Color`↔`#RRGGBB` 双向） |
| **建议拆小步** | ① 只读：设备列表 + 亮度/超时 ② 静态 4 区取色 ③ Dynamic/Software 效果 ④ 预设主题 ⑤ OEM 键盘图 ⑥ 单键编辑 |
| **验收** | build 0 error；截图 `docs\winui-s5-lighting.png`（至少覆盖 静态区 / OEM 区 / 键盘图 三张）；取色后 Hex 框同步、写灯后读回颜色一致；`--rgb-test` 探针仍 PASS |
| **风险** | ① 70 个绑定 + 55 个 `DynamicResource`，一次性迁必然翻车 → 必须按上面 6 小步 ② `ColorPicker` 改变交互（多了浮出面板）→ 需产品确认 ③ 写错帧可能让 EC 啸叫（红线 8）→ 复用现有 `LightingApply` 队列，**不要**在 UI 层直接发帧 |
| **回滚点** | 每小步一个 commit；OEM 键盘图做不出来时，先用 LED 列表视图（现 `ShowIndividualLedGrid` 已有）顶上 |

### S6 — 指标/覆盖层页 + 提示层

| 项 | 内容 |
|---|---|
| **目标** | `OverlayPage` 完成全部覆盖层配置；`InfoWindow` 的三种状态（成功/进行中/失败）改为壳内 `InfoBar`；模式切换提示改 `InfoBar` 或 Composition 浮层 |
| **新建** | `Pages\OverlayPage.xaml(.cs)`、`Shell\InfoBarHost`（壳底部全局提示区） |
| **验收** | build 0 error；截图 `docs\winui-s6-overlay.png`；改 HUD 缩放/透明度后覆盖层实际变化；「Add Slot」能加行、能删行；`--info-demo` 等价开关能截到提示态 |
| **风险** | ① 覆盖层本身在 `PreySense.Overlay`（独立进程/窗口），本页只是配置 → 不要误改覆盖层渲染 ② 若坚持要独立浮窗，透明窗做不出来（§4 #10）→ 提前和产品对齐「改成壳内 `InfoBar`」 |
| **回滚点** | 页面独立；提示层失败可临时退回 `Debug.WriteLine` |

### S7 — 收尾：切默认入口 + 删垫片

| 项 | 内容 |
|---|---|
| **目标** | WinUI 版成为默认启动路径；WPF 版变为可选回退；`WpfShims`/`AppCompat`/`CustomModalDialogCompat`/`ModeToastCompat`/`StubViews` 全部删除 |
| **改动** | `src\Run-GUI.ps1`（指向新 exe）、`app\build-fork.ps1`（构建目标切换）、`app\Fork\PreySense.slnx`（加入新项目）；删除 5 个垫片文件；`PreySense.Frontend` 里 `IDialogService` 落地后删除 `CustomModalDialog.xaml(.cs)` 的调用点（或整个文件） |
| **验收** | ①  全仓 `grep -r "System.Windows" app\Predex.App` → **0 命中** ② `WpfShims.cs` 已删且 build 0 error ③ `PreySense.slnx` 全量 build + 单测通过 ④ 计划任务/启动项启动到托盘正常 ⑤ 隐藏 30s 内存达标（或记录实际值并给出结论） |
| **风险** | 切换默认入口是**用户可见**的变更 → 必须留「一键回退到 WPF exe」的说明与旧 dist 目录 |
| **回滚点** | 保留 `app\dist`（WPF 版）；`Run-GUI.ps1` 加 `-Legacy` 开关 |

### 阶段依赖与并行度

```
S0 ──▶ S1 ──┬─▶ S2 ──▶ S3 ──▶ S4 ──┐
             │                       ├─▶ S7
             ├─▶ S6 ─────────────────┤
             └─▶ S5（最长，可提早开工，但依赖 S2 的行控件）
```

| 可并行 | 说明 |
|---|---|
| S5 灯效 ∥ S3/S4 | 灯效页自成体系（不共享行控件形态），可由第二人并行；但**必须等 S2 的 `Styles\Controls.xaml`** 定稿 |
| S6 指标 ∥ S3/S4 | 指标页最简单，可作为「热身」在 S2 后立刻做 |

---

## 9. 不做什么与红线

### 9.1 沿用项目红线（`Agent.md` 第 3 节，逐条继承）

| # | 红线 | 迁移期补充说明 |
|---|---|---|
| 1 | 不装/卸任何内核驱动，不改 Secure Boot / 测试模式 / VBS | WinUI 迁移**完全不涉及**驱动层，任何「顺便试试驱动」的冲动都要拒绝 |
| 2 | 不改 `PreySense.Core\Hardware\`、`PreySense.Core\Rgb\*.cs` 的**协议常量、帧结构、cmd 号** | 迁移中若发现需要新增能力，只能**加接口**（`IHardwareService` 上加默认实现方法），不改已有常量 |
| 3 | 不停用/重配 `PSSvc` | 可以 `Start-Service`，**不要** `Stop`/`Set-Service` |
| 4 | 不向命名管道发未校验的帧 | 迁移时**不要**为了调试方便直连管道；一律走 `IHardwareService` |
| 5 | 不改/删 `preysense.json`、`%ProgramData%\PreySense`、计划任务名 `PreySense`、单实例 mutex 名 | **特别注意 S0**：WinUI `App.xaml.cs:53` 现在用的是 `PredexWinUI_Unique_Mutex_1234`，与 WPF 的 `PreySenseApp_Unique_System_Mutex_999` 不同 → 切换默认入口那一步要对齐，否则两版可同时启动 |
| 6 | 不把程序集名改回 `PreySense` | WinUI 项目 `AssemblyName=Predex` **保持**；`pack://application:,,,/Predex;component/...` 在 WinUI 里改成 `ms-appx:///`，但**程序集名不动** |
| 7 | 界面文案必须走 `UiText`，不在别处硬编码 | 迁移期最大诱惑是「WinUI 页面直接写英文」。**每加一行文案就加一个 `UiText` key**；灯效/调校页现有多处硬编码（"Fan Policy"/"Live RPM"/"Per-process fan curves…"）——迁移时**顺手补 key**，不要原样搬硬编码 |
| 8 | 不每秒循环重写风扇占空比 | WinUI 的 `DispatcherQueueTimer` 更容易写出高频 Tick → 风扇/灯光刷新频率**沿用现有队列节奏**，不得加快 |

### 9.2 迁移期新增红线

| # | 红线 | 理由 |
|---|---|---|
| 9 | **不删、不改 `PreySense.Frontend` 现有 XAML/代码**，直到 S7 且用户明确同意 | 它是唯一在产、可交付的版本；WinUI 版崩溃时用户还得能开 WPF 版 |
| 10 | **不改 `IHardwareService` 的方法签名** | 链接会两边同时编译；改签名=同时改两版。只能加带默认实现的新方法 |
| 11 | **不新增 NuGet 依赖**（尤其 Win2D / CommunityToolkit.WinUI） | 沿用 `Agent.md`「没有新增 NuGet 依赖（除非有充分理由并在报告里说明）」；Win2D 只在「Canvas 方案确实画不出」时再提 |
| 12 | **不在 UI 层直接 P/Invoke 硬件**（EC/WMI/NVAPI/powercfg） | 全部经 `IHardwareService`；WinUI 侧只允许 Win32 窗口/托盘/钩子类 P/Invoke |
| 13 | **不在 WinUI 项目里引用 `PresentationCore` / `PresentationFramework` / `System.Windows.Controls`** | 会让 XAML 编译器崩且难查 |
| 14 | **不加 `<UseWPF>true</UseWPF>`** | 会静默污染构建 |
| 15 | **`WpfShims` 这类同名伪装类型不得扩散** | 现有 5 个类型是技术债；S7 前清零，期间不得新增第 6 个 |

### 9.3 本次调研明确「不做」的事

| 不做 | 原因 |
|---|---|
| 不迁移 `ModeToastWindow` 的 WPF 实现 | 形态整体改为壳内 `InfoBar`，1:1 迁移没意义 |
| 不迁移 `InfoWindow` 的透明窗+阴影 | WinUI 无逐像素透明（§4 #10） |
| 不迁 `Views\ModeToastWindow` 之外的窗口 | 本次任务范围只含 8 个界面 + 2 个小窗（CustomModalDialog / InfoWindow）；如需扩范围要重新评估 |
| 不把 ViewModel 抽成独立类库 | S7 可选项，迁移期双线作战风险高 |
| 不重构 `PreySense.Core` 的任何并发/缓存逻辑 | 禁区 |
| 不做 NativeAOT / Trim | 与「可运行优先」冲突，且 WinUI + AOT 有额外工具链要求 |
| 不改产品信息（`Product`/`Company`/版本号来源） | 与 Dafeiyu 那条 `Directory.Build.props` 教训同源：不要在 csproj 里重写一遍 |

---

## 10. 附录

### 10.1 关键命令

```powershell
# 唯一可用 SDK
$dotnet = "G:\DeepSeek DSH\DSH Works\Project\PredatorSense\tools\dotnet-sdk\dotnet.exe"

# 构建 WinUI 实验壳（当前配置）
& $dotnet build app\experiments\Predex.WinUI\Predex.WinUI.csproj -p:Platform=x64 -nodeReuse:false
# 产物：app\experiments\Predex.WinUI\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\Predex.exe

# 构建 WPF 在产版（每阶段结束都要跑，确认没被带坏）
& $dotnet build app\Fork\PreySense.slnx -c Release
& $dotnet test  app\Fork\PreySense.Tests\PreySense.Tests.csproj -c Release

# 截图（复用现有脚本，-Exe 指 WinUI 产物）
pwsh -File docs\capture-ui.ps1 -Exe "<上面那个 Predex.exe>"

# 内存实测（隐藏到托盘 30s 后）
(Get-Process Predex).WorkingSet64 / 1MB

# 崩溃日志
Get-Content "<exe 同目录>\crash.log" -Tail 40

# 硬件还原（风扇）
pwsh -File src\Restore-AutoFan.ps1
```

### 10.2 每阶段交付模板

```markdown
## S<n> 交付
- 目标达成：<一句话 + 证据>
- 改动文件：<路径 + 一句话>
- 构建：<命令> → 0 Error / N Warning
- 截图：docs\winui-s<n>-*.png
- 硬件：写前原值 → 写后读回 → 是否还原（风扇/模式/灯效）
- 内存：隐藏 30s WorkingSet = ___ MB
- 遗留：<列点>
- 回滚：<怎么退回去>
```

### 10.3 风险登记表

| ID | 风险 | 概率 | 影响 | 缓解 |
|---|---|---|---|---|
| R1 | PRI 仍不生成，`EnableMsixTooling=true` 走不通 | 中 | 高（启动即崩） | 立刻回退 `AfterBuild` + `makepri` 自动化；`HANDOVER` 第 5 节命令已验证 |
| R2 | `WpfShims` 的假 `RenderOptions` 造成切 GPU 模式时真机崩溃 | 中 | 高（黑屏/丢显示） | S1 前先在真机上跑「切 Auto/Hybrid/dGPU 各一次」并录屏；必要时切模式前 `AppWindow.Hide()` |
| R3 | 自包含 + `Bootstrap.Initialize` 双调 → `0xC000027B` | 高（若照抄非自包含写法） | 高 | S0 照 Dafeiyu `SELF_CONTAINED_WINAPPSDK` 分支跳 bootstrap |
| R4 | 灯效页内存爆炸，隐藏后仍 >150MB | 高 | 中（违背产品定位） | 页不缓存 + 切走释放；实测不达标则考虑灯效页懒加载子块 |
| R5 | `ColorPicker` 视觉与原色轮差异过大，产品不接受 | 中 | 中（返工） | S5 先做 1 天 spike，截图给产品拍板再动正式代码 |
| R6 | `RgbKeyboardLayout` 键位几何在 WinUI 下还原不了 | 中 | 中（OEM 页降级） | 兜底：用现成的 LED 列表视图（`ShowIndividualLedGrid`） |
| R7 | 单窗口导航后「未保存草稿」丢失（原来切窗口状态还在） | 高 | 低-中 | 每页把草稿存进各自小 VM；或调校/灯效页开 `NavigationCacheMode=Required`（代价是内存） |
| R8 | 140+ 处 `DynamicResource` 转 `ThemeResource` 漏转，导致切主题后有硬编码色 | 高 | 低 | 机械化检查：`grep -c DynamicResource` 归零 + 逐页截图对比 Light/Dark |
| R9 | 两版 mutex 名不一致 → 双开抢硬件 | 中 | 高（EC 冲突） | S1 就把 WinUI 的 mutex 名改成 `PreySenseApp_Unique_System_Mutex_999`（红线 5） |
| R10 | `requireAdministrator` + WinUI 在无 Acer 硬件的机器上只能验证 Mock | 确定 | 中（验证不完整） | 报告里显式标「未真机验证」，并列出需真机补测的 5 项（Predator 键/托盘/失焦隐藏/模式切换/GPU 切换） |

### 10.4 本次调研的覆盖率与未验证项

| 项 | 状态 |
|---|---|
| 8 个界面逐页拆解 | **完成**（控件、结构、VM 依赖、映射全部基于实际文件） |
| 19 个自定义控件职责 | **完成** |
| `Predex.WinUI` 逐文件裁决 | **完成**（含 5 个垫片的债务评估） |
| Dafeiyu-Go 5 个主题（窗口/切页/标题栏/主题/提权/PRI） | **完成**，均带文件路径与行号 |
| `DataGrid` 使用情况 | **已确认为 0 处** |
| PRI 自动生成是否真的能用 `EnableMsixTooling=true` 复现 | **未验证**（无 Dafeiyu 的构建环境证据链；他们的 `.pri` 产物存在，但构建机器/参数无法完全复原）→ S0 第一件事就是实测 |
| `WM_NCHITTEST` 在 WinUI 3 不可用 | 采信 Dafeiyu 的实测注释（`WindowChrome.cs:202-205`），本项目未独立验证 |
| 真机硬件行为 | **完全未验证**（本机无 Acer WMI，走 Mock） |
| 隐藏后内存实测 | **未做**（`HANDOVER` 自认） |
| `ColorPicker` 与原色轮的视觉差异 | **未做 A/B** |

---

## 11. S0–S7 状态表（2026-11-13 S6 收尾时点，本机真机实测）

> 本表是 §8 路线执行到 S6 的实际状态。数字与证据都在
> `app\experiments\Predex.WinUI\docs\S6-REPORT.md` + `docs\s6-*.png` + `shell.log`。
> 红线核对：`app\Fork\PreySense.Core` / `PreySense.Frontend` / `PreySense.Tests` / `tools\**`
> 的源码在 S0–S6 期间**没有被改过**（历史例外：`StartupDiagnostics.cs` 的 `typeof(App)` → `typeof(StartupDiagnostics)`，见
> `app\experiments\Predex.WinUI\docs\HANDOVER.md` §7，早于 S0）。

| 阶段 | 状态 | 证据 | 遗留 |
|---|---|---|---|
| S0 构建自愈（PRI/入口） | ✅ 完成 | `EnableMsixTooling=true` 产出 `resources.pri`（本轮 1,409,752 B Debug / 1,397,232 B publish），无 `Predex.pri`；10/10 次启动出窗口（HANDOVER §9.8） | `Program.cs` 自写入口仍未做（当前不需要） |
| S1 单窗口壳 | ✅ 完成 | 320 DIP 固定宽、116/300 DIP 两态、托盘/热键/失焦隐藏/页面宿主（HANDOVER §10） | — |
| S2 设置页 | ✅ 完成 | `s2-settings.png` / `r3fix-settings.png`；语言/主题/开机启动/Acer 服务/电池/USB | 页内开关全是写系统状态，只读验证 |
| S3 调校页 | ✅ 完成 | `s3-tuning.png` / `r3fix-tuning*.png`；CPU 调度/GPU 超频/高级区 | 三个 Apply 未点（真机纪律） |
| S4 风扇页 | ✅ 完成 | `s4-*` / `r3fix-fans*.png`；「点了就写」+ 回滚 + 防重入，实测切自定义再切回 Auto | 失败回滚分支未实测 |
| S5 灯效页 | ✅ 完成 | `s5-lighting.png` / `r3fix-lighting*.png`；4 区/键盘图/ColorPicker | 键盘图与单键编辑未点过 |
| S6 指标页 + 提示层 + 内存 + 垫片 | ✅ 完成（本轮） | `s6-overlay.png`、`s6-dialog-confirm.png`、`s6-dialog-notify.png`、`s6-toast.png`、`s6-show-restored*.png`、`s6-publish-window.png`；隐藏 30 s 实测 18.7/19.0 MB（可见 216.6/216.9 MB）；`-t:Rebuild` 0/0；publish 89 文件可启动 | 覆盖层 HUD 本体未开（真机纪律）；GPU MUX 真实确认路径没走硬件触发；私有内存 ~97 MB 没降；`WpfShims`/`AppCompat` 因红线删不掉 |
| S7 切默认入口 + 删全部垫片 | ⛔ 未做 | — | 需要改 `src\Run-GUI.ps1`、`app\build-fork.ps1`、计划任务指向，并先解决 `WpfShims` 的退役（要动 `PreySense.Frontend`） |

### 11.1 S6 的实测结论（替代原附录里「隐藏后内存未做」那一行）

- 任务书基线「隐藏到托盘后 WorkingSet64 ≈ 244 MB」**本机复现不出来**。同一脚本实测：
  改动前隐藏 +1.5 s = **214.3 MB**（空闲回收 8 s 未跑），+30 s = **16.3–16.7 MB**；
  改动后 +1.5 s = **53.1–54.6 MB**（页面树已释放并立即 trim），+30 s = **18.0–19.0 MB**（3 次重复测量）。
  结论：25–35 MB 目标改动前就已达到；本轮收益是**释放提前到 2 s 内**，30 s 稳态没有变好。
- 私有内存改动前后都是 ~97–99 MB，是 WinUI/DWM 原生占用，清 XAML 树收不回；再往下需要销毁窗口/重启进程。

### 11.2 本轮新增的两个 WinUI 事实（写进迁移知识库）

1. `ContentDialog.DefaultButton = Primary` 时，`PrimaryButtonAsDefaultButton` 可视状态会把
   `PrimaryButton.Style` 覆盖成框架 `AccentButtonStyle` → 自定义危险按钮样式静默失效；改
   `DefaultButton = None` 才生效（`ContentDialog.PrimaryButtonStyle` 属性本身是存在的，已在
   `Microsoft.UI.Xaml.winmd` + `generic.xaml` 核对）。
2. `ContentDialog` 挂在 XamlRoot 的 popup 层，**不继承** `RootGrid.RequestedTheme`，必须显式设置
   `dialog.RequestedTheme`，否则可能和壳主题相反。
