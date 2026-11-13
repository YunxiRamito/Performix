# Performix.Legacy.Wpf — WPF 旧界面，仅作回滚与参考

**这不是主程序。** WinUI 3 版（`src/Performix.App`，产物 `Performix.exe`）才是默认入口；
本工程是它的回滚路径与行为参照：

- 发布产物仍是 `dist\Predex.exe`（`AssemblyName` 保持 `Predex`，见下）。
- 只接受「保持可构建、可回滚、与 WinUI 行为对齐」的改动；新功能一律进 WinUI 版。
- 硬件逻辑不在这里：全部在 `Performix.Core`。

## 为什么程序集名还是 Predex

WPF 的资源字典用 `pack://application:,,,/Predex;component/...` 寻址
（`Views/CustomModalDialog.xaml` 等），窗口图标也引用 `appicon.ico` 资源。
改程序集名会连带改这些 Pack URI 与全部资源查找；回滚路径的价值在于「尽量少变」，
所以这一轮保留 `AssemblyName=Predex`，只把项目文件名/目录名/界面文案改成 Performix。

## 与主程序的关系

`src/Performix.App/Performix.App.csproj` 通过 `<Compile Include="..\Performix.Legacy.Wpf\ViewModels\*.cs" />`
**链接编译**这里的 ViewModel，并链接部分与 WPF 无关的 `Services/*.cs`
（`UiText`、`StartupTaskService`、`OverlayBridge`、`ProbeMode` 等）。
也就是说：这里的 `ViewModels/` 同时被两个 UI 共用 —— 改动要两边都考虑。

## 目录

- `Views/`：窗口组合与事件转发。Main、Lighting（`KeyboardWindow`）、Overlay（`MetricsWindow`）、
  Mode limits（`ModeSettingsWindow`）、Settings（`SettingsWindow`）。
- `Controls/`：可复用行与卡片。同一个交互第二次出现时再抽控件。
- `ViewModels/MainViewModel.*.cs`：状态与命令，按功能切分（PowerModes / Fans / Gpu / Lighting /
  Display / Session / Services / Theme / Tuning）。
- `Services/`：UI 侧适配（`ProductionHardwareService`、托盘、浮窗桥、开机任务、键盘钩子）。
- `Styles/`：语义令牌。视图里不要直接写主题颜色。

## UI 约定

1. 优先复用 `SettingSwitchRow` / `SettingDropdownRow` / `SettingSliderRow` / `SettingActionRow` /
   `SettingValueRow` / `SettingCard`，不要手写等价 Grid。
2. 主题相关画刷用 `DynamicResource`。
3. 滑杆/文本输入的硬件写入用 `UiDebouncer`，不要另造 `DispatcherTimer` 防抖。
4. 渲染路径与属性 getter 里不做硬件调用。
5. 任何 timer、hook、事件订阅、原生句柄服务都必须有显式关闭路径。
6. 模式提交与灯效提交互不重放（切模式不重写 RGB）。

## 构建与验证

```powershell
# 在仓库根目录
& $env:PERFORMIX_DOTNET build src\Performix.Legacy.Wpf\Performix.Legacy.Wpf.csproj -c Release
& $env:PERFORMIX_DOTNET test tests\Performix.Tests\Performix.Tests.csproj -c Release

# 或者一键发布到 dist\（同时产出 PerformixProbe.exe）
pwsh -File scripts\build-fork.ps1 -Test
```

> `PERFORMIX_DOTNET` 指向 .NET 10 SDK；没设时回退到 `<repo>\tools\dotnet-sdk\dotnet.exe`。
> 便携 SDK 不提交（818 MB），见根目录 README。
