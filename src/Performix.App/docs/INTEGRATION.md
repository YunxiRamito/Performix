# S2 集成报告：把四个真页面装进 WinUI 壳

日期：2026-11-13 04:35 左右（本机真机，Acer Predator PH317-55）
范围：`app\experiments\Predex.WinUI`（只改了本目录；`PreySense.Core` / `PreySense.Frontend` / `PreySense.Tests` / `tools\**` 一个字节没动）
目标页面：S2 Settings、S3 Tuning、S4 Fans、S5 Lighting（+ S1 已有 Home、S1 骨架 Overlay）

---

## 0. 一句话结论

四个页面已从 `Views\` 搬进 `Pages\`，壳的 `PageTypes` 全部指向真实页面，`-t:Rebuild` 干净构建 **0 warning / 0 error**，六页在真机上真实渲染并出图，折叠 116 ↔ 展开 280 DIP 正常，关闭=隐藏到托盘正常。
**这一轮还挖出了「XamlCompiler exit 1 且不打任何错误」的真正原因**（不是两套同名页打架，见 §3），四个页面作者写的 XAML/C# 里有 3 类非法成员；已按原意修好、没有删功能。

---

## 1. 页面搬迁与命名空间

| 源（`Views\`） | 目标（`Pages\`） | 类 / 命名空间变化 |
|---|---|---|
| `Views\SettingsPage.xaml/.cs` | `Pages\SettingsPage.xaml/.cs` | `Predex.WinUI.Views.SettingsPage` → `Predex.WinUI.Pages.SettingsPage` |
| `Views\TuningPage.xaml/.cs` | `Pages\TuningPage.xaml/.cs` | `Predex.WinUI.Views.TuningPage` → `Predex.WinUI.Pages.TuningPage` |
| `Views\FansPage.xaml/.cs` | `Pages\FansPage.xaml/.cs` | `Predex.WinUI.Views.FansPage` → `Predex.WinUI.Pages.FansPage` |
| `Views\LightingPage.xaml/.cs` | `Pages\LightingPage.xaml/.cs` | `Predex.WinUI.Views.LightingPage` → `Predex.WinUI.Pages.LightingPage` |

- 8 个文件 Move 过去覆盖了 S1 骨架；`Views\` 现在只剩 `CustomModalDialogCompat.cs` / `ModeToastCompat.cs`（命名空间 `PreySense.Frontend.Views`，是替身不是页面），**没有任何同名页残留**。
- 每个页面改了 `x:Class` + 文件里的 `namespace`；`LightingPage.xaml` 里给静态格式化类 `LightingFormat` 用的 `xmlns:views="using:Predex.WinUI.Views"` 改成 `xmlns:pages="using:Predex.WinUI.Pages"`，16 处 `views:LightingFormat.*` 跟着改。
- `Controls\FanCurveCanvas`、`Controls\KeyboardLayoutView` 的命名空间与文件位置**没动**。
- 搬完后页面行数：Settings 384/223、Tuning 857/480、Fans 229/444、Lighting 800/813（XAML/C#）。

---

## 2. 壳接线

### 2.1 `PageTypes` 映射（`Shell\ShellWindow.xaml.cs`）

| nav key | 类 | 标题 key | Vm 赋值 |
|---|---|---|---|
| `Home` | `Predex.WinUI.Pages.HomePage` | `Home` | 不需要（页面读 `App.CurrentShell.ViewModel`） |
| `Tuning` | `Predex.WinUI.Pages.TuningPage` | `Tuning` | ✅ 显式 |
| `Fans` | `Predex.WinUI.Pages.FansPage` | `Fans` | ✅ 显式 |
| `Lighting` | `Predex.WinUI.Pages.LightingPage` | `Lighting` | ✅ 显式 |
| `Overlay` | `Predex.WinUI.Pages.OverlayPage` | `Overlay` | 不需要（S1 骨架） |
| `Settings` | `Predex.WinUI.Pages.SettingsPage` | `Settings` | ✅ 显式 |

`PageTitleKeys` 未改。

### 2.2 页面契约（新增 `Pages\IPredexPage.cs`）

```csharp
public interface IPredexPage { MainViewModel? Vm { set; } }
```

- 四个真页面实现它（Settings/Tuning/Fans/Lighting），保持各自的 `Vm` setter 不动：Settings/Tuning 发 `PropertyChanged("Vm")`、Lighting 调 `Bindings.Update()` 做晚赋值全量重绑；Fans 是赋值在首次 `Loaded` 之前，自动属性够用。
- `ShellWindow.NavigateTo` 现在是：`Activator.CreateInstance` → `if (page is IPredexPage p) p.Vm = ViewModel` → 入 `PageHost`。日志会写 `created page Tuning (TuningPage, Vm=assigned)`。
- **新增兜底**：页面构造/赋 Vm 抛异常时，`try/catch` 把完整异常写进 `shell.log` 并留在当前页，壳不再跟着崩。这条就是定位 Settings 页崩溃的手段（§3.3）。

---

## 3. 构建：0 warning / 0 error（含 XamlCompiler 静默崩溃的真实根因）

### 3.1 现象与误判

- 原状：`XamlCompiler.exe` exit 1、**stdout/stderr 一个字都不打**、`output.json` 不落盘（或 `GeneratedCodeFiles: []`）。
- 交接文档猜「`Pages\` 与 `Views\` 两套同名页撞完整类名」——**实测不是**。把 8 个文件搬完、只留一套页之后仍然 exit 1。
- 真正结论（用 `input.json` 手工喂 XamlCompiler + 逐节点裁剪二分出来的）：**只要 XAML 里出现一个非法成员，这个版本的 XamlCompiler 就静默 exit 1**。探针验证：`<TextBlock FooBar="True"/>`、`<StackPanel IsEnabled="False"/>` 都是 exit 1；合法元素 exit 0。
- 途中试过 `-p:UseXamlCompilerExecutable=false` 想拿回诊断信息，便携 SDK 下托管任务直接 MSB4061 起不来（Type must be a type provided by the runtime），不可用。

### 3.2 找到并修好的 3 类非法成员（都按原意修，没删功能）

| # | 问题 | 位置 | 改法 |
|---|---|---|---|
| 1 | `IsEnabled` 写在 **Panel** 上（WinUI 只有 `Control` 有 IsEnabled） | Tuning 4 处（GPU 超频块、PL1/PL2/Tau、undervolt、GPU power limits）、Fans 1 处（EditorArea）、Lighting 1 处（颜色行） | 用 `ContentControl` 包住原面板，`IsEnabled` 放 ContentControl 上（禁用会向下继承）；共 7 个 ContentControl 包装（含 Fans `ModeRow`）。布局用 `Horizontal/VerticalContentAlignment="Stretch"` 保持原样 |
| 2 | `ColorPicker.IsColorSpectrumSliderVisible` 在 WinAppSDK 1.6 **不存在** | Lighting 5 处 | 删除该属性（WinUI 没有等价属性；其余 ColorPicker 属性都在） |
| 3 | `<x:Double>` 资源赋给 `ColumnDefinition.Width`（要 `GridLength`） | Settings 9 处 | 改为字面量 `Width="132"`，删掉 `SettingControlWidth` 资源并注释原因 |

另外的编译错误（C# 层面，不是静默崩溃）：

- `TuningPage.xaml.cs:359` `CpuScheduleValues.Default(...)` → `CpuScheduling.CpuScheduleValues.Default(...)`（该 record 是 `CpuScheduling` 的嵌套类型，不是顶层类型）。
- `FansPage.xaml.cs:346` `ModeRow.IsEnabled`：`ModeRow` 原来是 Grid（无 IsEnabled）→ 改成 ContentControl 后这行合法。

### 3.3 运行期才炸的一处（编译 0 error）

- Settings 页首次打开抛 `XamlParseException`：
  `Failed to assign to property 'ColumnDefinition.Width' because the type 'Windows.Foundation.Double' cannot be assigned to the type 'Microsoft.UI.Xaml.GridLength'` → 由 §3.2 #3 修复。壳的构造兜底把这行完整打进 `shell.log`，所以能在集成阶段定位。
- 修完复测：六页全部创建成功（见 §5 shell.log 证据）。

### 3.4 构建结果

```
& <sdk>\dotnet.exe build Predex.WinUI.csproj -p:Platform=x64 -nodeReuse:false -t:Rebuild
已成功生成。
    0 个警告
    0 个错误
```

输出 `bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\`：

- 95 个文件（其中 `shell.log` / `debug.log` 是运行验证留下的日志，非构建产物）
- `resources.pri` 1,398,376 B；**没有 `Predex.pri`**
- `Predex.exe` FileVersion `3.0.8.0`

---

## 4. 截图清单（`docs\`）

| 文件 | 内容 | 尺寸（物理 px @150%） | PrintWindow ink |
|---|---|---|---|
| `s1-home-collapsed.png` | 首页折叠态（116 DIP） | 480×174 | 36.8% |
| `s1-home-expanded-real.png` | 首页展开态（真内容） | 480×420 | 33.9% |
| `s2-settings.png` | 设置（真实内容：语言/主题/开机启动/Acer 服务） | 480×420 | 70.5% |
| `s3-tuning.png` | 调校（真实内容：状态条/性能模式/CPU 调度…） | 480×420 | 47.0% |
| `s4-fancurve.png` | 风扇曲线（真实内容：配置模式/控制模式/CPU 曲线图） | 480×420 | 38.0% |
| `s5-lighting.png` | 灯光（真实内容：设备检测/背光超时） | 480×420 | 53.0% |
| `s6-overlay.png` | 浮窗（**仍是 S1 骨架**，S6 未开始） | 480×420 | 19.6% |
| `s1-home-collapsed-screen.png` / `s1-home-expanded-screen.png` | 屏幕抓图（含 6px 桌面边） | 492×186 / 492×432 | — |
| `s4-fancurve-auto.png` / `s4-fancurve-custom-after-click.png` / `s4-fancurve-auto-restored.png` | 风扇模式「点了就写」实测（§6）：切到自定义后编辑器解锁、无错误行；切回自动 | 480×420 | — |
| `s1-after-tray-click.png` | 托盘唤回 | 480×420 | 33.1% |

`s1-home-expanded-real.png`、`s2..s6-*` 是本次新增的交付名；`s1-*.png` 是 `docs\s1-verify.ps1` 原样输出，保留作对照。

窗口枚举证据（`EnumWindows` by PID）：`pid=2260 hwnd=0x8E0996 class='WinUIDesktopWin32WindowClass' title='Predex'`；折叠 `480×174`、展开 `480×420`（150% DPI → 320×116 / 320×280 DIP）。
关闭按钮：`process alive=True window visible=False`；托盘 `WM_TRAY_CALLBACK` 后 `visible=True`；Predator 键 `VK 0xCC`：第 1 次 `visible=False`、第 2 次 `visible=True`。
六页导航日志：`created page Lighting/Overlay/Fans/Tuning/Settings`，四页 `Vm=assigned`。

---

## 5. 设计规范自查（`docs\UI-DESIGN-RULES.md`）与外部评审 5 条

### 5.1 自查结论

- 字号：五个真页面只用了 **11 / 12 / 14 / 16**（Home 11/12/16、Tuning 11/12/14/16、Fans 11/12、Lighting 11/12/14、Settings 12）。合规。
- 圆角：修掉 Lighting 的 4 个 22×22 色块 `CornerRadius="3"` → `4`；现在只剩 4 / 6（外加骨架页的 4）。合规。
- 间距：Fans 编辑器内层 `Spacing="10"` → `8`；Overlay 骨架 `ColumnSpacing="10"` → `8`。余下都是 4/6/8/12/16（Home 的 `10,0` 是规范 §6.2 明确给 AUTO chip 的内边距，保留）。
- 反模式：无“卡片套卡片”（Home 无卡片、Tuning/Lighting 是单层卡片）、无大标题+长句（长解释都收进 `?` 提示点 tooltip）、无整窗大圆角 Border、无半透明蒙层压材质。
- 可点元素：截图脚本点的全部元素都保留原 `AutomationId`（`OpenLighting/OpenOverlay/OpenFans/OpenTuning/OpenSettings/BtnBack/BtnExpand/BtnClose/CmbRefreshRate/BtnAutoRefresh` 等），一个没丢。

### 5.2 外部评审 5 条的处理

| # | 问题 | 处理 |
|---|---|---|
| 1 | CPU/GPU 都显示 4109 RPM，疑似绑了两次 | **不是 UI 重复绑定**：`HomePage.xaml.cs` 加了一行 `--trace` 证据 `home: telemetry cpu=80°C/4109 RPM gpu=55°C/N/A`（两个独立属性）；随后 `Restore-AutoFan.ps1` 直接从硬件读到 `fan=4109/4109 RPM` —— 两个风扇同速是硬件读数。未改代码（硬件层在红线外） |
| 2 | GPU 模式四个按钮没有选中态 | 样式已与性能模式完全同款（`#263B82F6` 底 + `#3B82F6` 描边，绑 `IsGpu*Active`），但**本机读不到 mux 状态**：日志 `home: loaded muxMode=-1 (N/A) showDdsTiles=True biosDiscrete=False`（NVIDIA DDS 不支持 → `ActiveGpuMuxMode` 恒 -1），所以四个都不亮。WPF 在产版同样。**未接上，见 §7** |
| 3 | 出现两个「自动」，含义不清 | 刷新率那颗 chip 改文案 **`Auto refresh` / 自动刷新**（新 key 加进 `WinUIStrings`），tooltip=`刷新率`；GPU 模式的「自动」没动 |
| 4 | 温度重复（标题栏 + 正文） | 展开区遥测行**删掉 CPU/GPU 温度**，只留风扇转速；温度只在标题栏出现（折叠态唯一出口，符合规范 §6.1） |
| 5 | 九个按钮图标彩虹色 | 5 个性能模式 + 4 个 GPU 模式图标统一：未选中 `#A8A8BC`、选中才 `#3B82F6`；选中底色/描边统一成同一个 accent；入口行图标本来就是单色，未动。强调色现在只表示“选中” |

### 5.3 顺带修的 i18n

`Services\LocalizationService.cs` 的 `WinUIStrings` 补了 **13 条 zh-CN**：Tuning 副标题、Fans 的 4 条（缺 key 清单）、Lighting 的 7 条（缺 key 清单）、`Auto refresh`。另外 Fans 的「配置模式」下拉原来显示英文 `Balanced`，现在走 `Loc.T` 显示「均衡」。策略不变：只有 zh-CN 有表，其余语言回退英文。

---

## 6. 用户追加需求：风扇模式「点了就写」

实现（`Pages\FansPage.xaml` / `.xaml.cs`）：

- 单选 `Checked` → `draft.ControlMode = mode` → `Vm.SaveFanProfile(_mode, draft)`（`Task.Run`）→ `await Vm.ApplyFanProfileAsync()`，不再等 Apply。
- **失败回滚**：`catch` 里把 `_profile.ControlMode`、`_drafts[_mode]` 和单选状态都拨回切换前，错误写进页面原有的 `ErrorText` 行。
- **防重入**：`_applying` 标志 + 整组单选包在 `ContentControl x:Name="ModeRow"` 里、切换期间 `IsEnabled=false`；`_syncingModeRadios` 防止程序化 `IsChecked` 回滚再次触发写 EC。
- `Apply` 按钮保留，语义变为“重新应用当前模式”（原 `ApplyAsync` 不动）。
- 每次成功/失败写一行 `shell.log`（`fans: controlMode A -> B applied/FAILED`）。

真机实测（`docs\s4-fans-mode-click.ps1`，UIA 选 `自定义` → 等 2.5s → 选回 `自动`）：

```
fans: controlMode Auto -> Custom applied for operating mode 1
fans: controlMode Custom -> Auto applied for operating mode 1
radio 自定义 state after click = True    radio 自动 state after click = False
radio 自动   state after switch back = True   radio 自定义 state after switch back = False
```

失败回滚分支**没有实测**（没法在不破坏硬件状态的前提下让 `ApplyFanProfileAsync` 抛错）。

---

## 7. 没接上 / 没验的清单（原样列，不美化）

1. **Overlay 页仍是 S1 骨架**（S6/task-6 才迁 `MetricsWindow`）；`s6-overlay.png` 拍到的就是占位内容。
2. **GPU 模式按钮选中态本机出不来**：`ActiveGpuMuxMode = -1`（NVIDIA DDS 不可用），壳日志已留证；需在支持 DDS 的机器复验。UI 侧的选中样式与绑定已经齐了。
3. **灯光页下半部分没验**：`KeyboardLayoutView` 键盘布局、4 个 `ColorPicker` 区块在页面里默认 `Collapsed`，截图/UIA 只走到「检测设备 + 背光超时」。作者警告的 `KeyboardLayoutView` 的 4 个 Brush DP 默认值 `new SolidColorBrush(...)`（E_INVALIDARG 风险）**页面构造时没触发**，但键盘布局的画笔更新路径一次都没点过。
4. **调校页三个 Apply 没点**：`ApplyCpuSchedule`（powercfg）、`ApplyGpuClock`（NVAPI 偏移）、`ApplyAdvancedTuning`（驱动缺失，本来就是 disabled）。
5. **风扇页 `Apply` / `Restore Defaults` 没点**：本轮只做了两次模式切换写入（已恢复 Auto），按钮本身没点。
6. **灯光页 `Apply` / `Reset` 没点**（会下发 EC）。
7. **设置页所有开关没动**：开机启动（计划任务）、Acer 服务（停服务）、电池限制、USB 充电、电源方案下拉 —— 全是写系统状态，只读不改。
8. **托盘图标真实鼠标点击没验**：脚本用 `PostMessage(WM_TRAY_CALLBACK, WM_LBUTTONUP)`；Predator 键是 `keybd_event(0xCC)` 真注入，已验证。
9. **`TuningPage.FanEditorRequested` 事件没有订阅者**：页面里同时直接 `App.CurrentShell?.NavigateTo("Fans")`，所以按钮能用；事件本身是死代码。
10. **隐藏 30s 后的内存没测**（S6 任务）。
11. **硬件层“两风扇同速”没深挖**（`PreySense.Core` 在红线外）；本次实测 `Restore-AutoFan` 的硬件读数也是 `4109/4109`，倾向真实读数，但若怀疑传感器映射，需要 Core 作者处理。
12. 非 en/zh-CN 语言仍回退英文（沿用 S1 策略，`WinUIStrings` 只有 zh-CN 行）。
13. 首页标题栏温度不带 CPU/GPU 标签（沿用 S1 契约，本次未改）。

---

## 8. 收尾：`Restore-AutoFan.ps1` 输出

```
SetGamingFanBehavior 0x00410009 (Auto) -> True
当前: temp CPU=69C GPU=50C  fan=4109/4109 RPM  mode=1
```

（在风扇模式「点了就写」实测之后执行的，EC 已回到 Auto。）

---

## 9. 本次改动文件

| 文件 | 改动 |
|---|---|
| `Shell\ShellWindow.xaml.cs` | 页面构造后显式赋 `Vm`（`IPredexPage`）；页面构造失败兜底 + 完整异常写 `shell.log` |
| `Pages\IPredexPage.cs` | 新增（页面↔壳的 Vm 契约） |
| `Pages\SettingsPage.xaml(.cs)` | 从 Views 搬入 + 命名空间/x:Class；`GridLength` 修复 |
| `Pages\TuningPage.xaml(.cs)` | 从 Views 搬入 + 命名空间；4 处 `IsEnabled` 改 `ContentControl`；`CpuScheduling.CpuScheduleValues` 修复；副标题翻译 |
| `Pages\FansPage.xaml(.cs)` | 从 Views 搬入 + 命名空间；`EditorArea`/`ModeRow` 改 `ContentControl`；模式「点了就写」+ 回滚 + 防重入 + 日志；配置模式下拉本地化；Spacing 10→8 |
| `Pages\LightingPage.xaml(.cs)` | 从 Views 搬入 + 命名空间；`pages:` xmlns；删 `IsColorSpectrumSliderVisible`；颜色行 `IsEnabled` 改 `ContentControl`；色块圆角 3→4 |
| `Pages\HomePage.xaml(.cs)` | 外部评审 5 条（单色图标/统一选中态/去重复温度/自动刷新文案）；`--trace` 遥测与 mux 状态证据 |
| `Pages\OverlayPage.xaml` | 间距刻度 10→8（仍是骨架） |
| `Services\LocalizationService.cs` | `WinUIStrings` 加 13 条 zh-CN |
| `docs\s4-fans-mode-click.ps1` | 新增：风扇模式「点了就写」真机验证脚本 |
| `docs\INTEGRATION.md` | 本文件 |

未动：`Predex.WinUI.csproj`、`App.xaml(.cs)`、`Styles\Resources.xaml`、`Controls\**`、`Services\` 其他文件、WPF/Core/tools 全部。

---

## 10. S6 之后的更正（2026-11-13）

本文是 S2 集成时点的报告。S6 收尾后以下两条已过时：

- §4/§7.1「Overlay 页仍是 S1 骨架、`s6-overlay.png` 拍的是占位内容」→ Overlay 已是真页面
  （指标槽位/缩放/透明度/位置/主题/Show FPS），见 `docs\S6-REPORT.md` §1 与新的 `s6-overlay.png`。
- 提示层不再是 Debug/MessageBox：`ContentDialogUserPromptService` + 壳内 InfoBar toast，
  见 `docs\S6-REPORT.md` §2。

另外 S6 起「隐藏到托盘时页面实例保留」的行为已改：隐藏会销毁整棵页面树、唤起时重建，
详见 `docs\SHELL-CONTRACT.md` §2 与 `docs\S6-REPORT.md` §3。
