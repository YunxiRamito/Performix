# Predex.WinUI 交接日志

日期：2026年11月13日
状态：**可编译、可运行**（0 警告 0 错误；窗口正常渲染，截图见 `screenshot-main-window.png`）

## 1. 目标

用 WinUI 3 重做 PreySense/Predex 的界面：

- 闲时自动清理内存，常驻占用低。
- 按 Predator 键或单击托盘图标，窗口从任务栏上方以贝塞尔曲线滑出。
- 复用原 WPF 项目的 ViewModel，不复制代码。

## 2. 如何构建和运行

便携 SDK 在 `tools/dotnet-sdk/`，系统自带的 `dotnet` 没有 SDK。

```bash
cd app/Fork/Predex.WinUI
"../../../tools/dotnet-sdk/dotnet.exe" build Predex.WinUI.csproj -p:Platform=x64 -nodeReuse:false
```

输出：`bin/x64/Debug/net10.0-windows10.0.22621.0/win-x64/Predex.exe`

> ⚠️ 下面第 2–8 节是 S0 之前的历史记录。**手工补 `Predex.pri` 的做法已经废弃**：
> 现在构建直接产出 `resources.pri`，不再需要任何手工步骤。看 §9「S0 之后」为准。

崩溃日志：`Predex.exe` 同目录下的 `crash.log`（`App.xaml.cs` 里的 `LogCrash` 写入）。

## 3. 目录结构

| 路径 | 作用 |
|---|---|
| `App.xaml.cs` | 单实例、探测模式、跨进程唤起事件、崩溃日志 |
| `MainWindow.xaml(.cs)` | 主界面、托盘、键盘钩子、HID 监听、显示/隐藏 |
| `MainWindow.Animation.cs` | 贝塞尔滑入/滑出动画（Composition API） |
| `Compat/WpfShims.cs` | `Dispatcher`、`DispatcherTimer`、`DispatcherOperation<T>`、`Application`、`RenderOptions` 替身，转发到 `DispatcherQueue` |
| `Compat/AppCompat.cs` | 替身 `PreySense.Frontend.App`（`SuppressAutoHide`、`Current.Dispatcher`） |
| `Services/` | 主题、本地化、托盘、键盘钩子、内存回收、更新检查的 WinUI 版实现 |
| `Views/CustomModalDialogCompat.cs` | `CustomModalDialog` 替身，用 Win32 `MessageBoxW` 实现同步确认 |
| `Views/ModeToastCompat.cs` | `ModeToastWindow` 替身，目前只写 Debug 输出 |
| `Views/StubViews.cs` | 设置、键盘灯效、风扇曲线、模式调节四个子窗口的占位 |
| `docs/` | 本文档和截图 |

`Predex.WinUI.csproj` 通过 `<Compile Include=... Link=...>` 链接了 `PreySense.Frontend` 的 21 个 ViewModel 文件和 8 个服务文件。改这些文件会同时影响 WPF 版。

## 4. 本次做的关键决定

1. **链接而不复制 ViewModel**。代价是 ViewModel 里写死的 WPF 类型（`Dispatcher`、`App`、`CustomModalDialog`）要靠替身类型满足编译。优点是业务逻辑只有一份。
2. **`DispatcherOperation<T>.Task` 保持 `Task<Task>`**。链接代码写的是 `InvokeAsync(() => FooAsync()).Task.Unwrap()`，替身如果直接返回展平后的 `Task`，`Unwrap()` 会编译失败。
3. **`UpdateCheckService` 去掉了 `AppNotificationBuilder`**。该 API 需要清单注册，非打包应用不可用。现在发现新版本时直接用浏览器打开发布页。
4. **使用 Core 里的 `MockHardwareService`**。删掉了本项目重名的存根，避免命名空间歧义。
5. **非打包 + 自包含部署**（`WindowsPackageType=None`、`WindowsAppSDKSelfContained=true`、`RuntimeIdentifier=win-x64`）。目标机器不需要另装 Windows App Runtime。`Directory.Build.props` 默认定义 `PREYSENSE_NO_DRIVER`，内核驱动部分是空实现。

## 5. 已知问题和遗留工作

### 已知问题 1：`Predex.pri` 需手工生成（阻塞项，优先处理）— **S0 已解决，见 §9.3**

便携 .NET SDK 缺少 Visual Studio 的 PRI 打包任务，所以构建不会生成 `Predex.pri`。非打包应用启动时找不到它，会抛
`Cannot locate resource from 'ms-appx:///Microsoft.UI.Xaml/Themes/themeresources.xaml'`。

当前的手工做法（命令在 Git Bash 里执行，`export MSYS_NO_PATHCONV=1` 避免 `/cf` 被转成路径）：

```bash
export MSYS_NO_PATHCONV=1
MP="$HOME/.nuget/packages/microsoft.windows.sdk.buildtools/10.0.22621.756/bin/10.0.22621.0/x64/makepri.exe"
OUT="<输出目录>"            # 上面 win-x64 目录
ST="<临时暂存目录>"
mkdir -p "$ST/Styles" "$ST/Assets"
cp "$OUT"/*.xbf "$ST/"; cp "$OUT"/Styles/*.xbf "$ST/Styles/"; cp -r "$OUT/Assets/." "$ST/Assets/"
cp "$OUT/Microsoft.UI.pri" "$OUT/Microsoft.UI.Xaml.Controls.pri" "$ST/"
"$MP" createconfig /cf priconfig.xml /dq en-US /o
"$MP" new /pr "$ST" /cf priconfig.xml /of "$OUT/Predex.pri" /o
```

注意：不能对整个输出目录直接 `makepri new`，会报 `PRI277 Conflicting values for resource 'Files/Microsoft.UI.Xaml/Assets/NoiseAsset_256X256_PNG.png'`。所以要用暂存目录，只放 `.xbf` 和两个框架 `.pri`。

**待办**：在 `Predex.WinUI.csproj` 里加一个 `AfterBuild` Target，用 `Exec` 自动完成上面的步骤，路径从 `$(NuGetPackageRoot)` 取。

### 已知问题 2：内存占用高于目标

窗口可见时工作集约 250–275 MB（私有内存约 130–160 MB），目标是隐藏到托盘后 25–35 MB。`Services/MemoryManager.cs` 的空闲回收（`GC.Collect` 加 `SetProcessWorkingSetSize`）已实现，但**没有实测过隐藏后的数值**。

**待办**：运行后点关闭按钮隐藏窗口，等待 `IdleTrimDelay` 之后用 `Get-Process Predex` 看 `WorkingSet64`。如果仍高，重点怀疑 `Microsoft.UI.Xaml` 本身加载的原生内存，必要时在隐藏时释放 `Content` 或销毁窗口、按需重建。

### 已知问题 3：子窗口是占位

`Views/StubViews.cs` 里设置、键盘灯效、风扇曲线、模式调节四个窗口只显示一行文字。主界面上对应按钮能打开它们，但没有实际功能。

### 已知问题 4：未验证的交互

以下功能编译通过，但**没有在真机上验证**，这台开发机没有 Acer WMI 硬件，走的是 Mock：

- Predator 键触发滑入、单击托盘图标触发滑入。
- 失焦自动隐藏（`Window_Deactivated` 已写，但 `Activated` 事件目前只接了 `OnWindowShown`，没有接到它）。
- 性能模式切换、GPU 模式切换（需要真硬件）。
- `WS_EX_NOACTIVATE` 会不会让窗口无法获得键盘焦点，需要实际点击测试。

### 已知问题 5：杂项

- `ModeToastWindow` 只写 `Debug.WriteLine`，没有真正的提示 UI。
- `CustomModalDialog` 替身用系统 MessageBox，外观与应用主题不一致。
- `HidDeviceArrivalWatcher` 只注册了通知，没有处理 `WM_DEVICECHANGE`。
- `MainWindow.AppWindow_Changed` 里是空实现的注释占位。

## 6. 对构建环境做的修改（在仓库之外的副作用）

为让便携 SDK 能构建 WinUI 3，向 SDK 目录写入了一个文件：

```
tools/dotnet-sdk/sdk/10.0.404/Microsoft/VisualStudio/v18.0/AppxPackage/Microsoft.Build.AppxPackage.dll
```

这是自制的替身，只实现了 Windows App SDK 构建链会调用的 8 个任务（去重、目录展开、默认语言、架构等），逻辑最小化。源码已存档在 `tools/pri-stub/`，重建方式：

```bash
cd tools/pri-stub
../dotnet-sdk/dotnet.exe build PriTasksStub.csproj -c Release -nodeReuse:false
../dotnet-sdk/dotnet.exe build-server shutdown     # 否则目标 DLL 被占用，复制失败
cp bin/Release/net10.0/Microsoft.Build.AppxPackage.dll ../dotnet-sdk/sdk/10.0.404/Microsoft/VisualStudio/v18.0/AppxPackage/
```

如果升级或替换了 `tools/dotnet-sdk`，这个 DLL 要重新放一次。`PriTasksStub.csproj` 引用的 `Microsoft.Build.Utilities.Core 17.11.4` 有一条已知的高严重性漏洞告警（NU1903）。它只在构建期加载、不进入产物，风险有限，但有条件时应升级版本。

`.csproj` 里相关设置：`AppxGeneratePriEnabled=false`、`AppxGeneratePrisForPortableLibrariesEnabled=false`、`EnableMsixTooling=false`。这些是为了绕开 PRI 打包任务，也是问题 1 出现的根源。

## 7. 工作区状态

- Git 工作区里 `app/Fork/Predex.WinUI/` 和 `tools/pri-stub/` 都是未跟踪文件，**没有提交任何东西**。
- 本次改动了一个共享文件：`app/Fork/PreySense.Frontend/Services/StartupDiagnostics.cs`（`typeof(App)` 改成 `typeof(StartupDiagnostics)`，因为 WinUI 版没有同名 `App`）。WPF 版编译不受影响，但改动尚未单独验证。
- `app/Fork/PreySense.slnx` 已加入 `Predex.WinUI` 项目。
- 调试用的 `Predex.exe` 可能仍在后台运行，用 `taskkill /F /IM Predex.exe` 结束。

## 8. 建议的下一步（按优先级）

1. 把 `Predex.pri` 生成接入 csproj 的 `AfterBuild`（问题 1）。
2. 实测隐藏后的内存，决定是否需要销毁重建窗口（问题 2）。
3. 在真机上验证 Predator 键、托盘点击、失焦隐藏（问题 4）。
4. 补全四个子窗口（问题 3）。
5. 提交前单独跑一遍 WPF 版 `PreySense.Frontend` 的构建，确认 `StartupDiagnostics.cs` 的改动没有副作用。

---

## 9. S0 之后（构建 / 启动 / 单实例 / 崩溃排查）

> 本节只描述 S0 收尾后的现状，不涉及 S1 及以后的壳/页面/主题工作。

### 9.1 怎么构建

```powershell
$sdk = "G:\DeepSeek DSH\DSH Works\Project\PredatorSense\tools\dotnet-sdk\dotnet.exe"
cd "G:\DeepSeek DSH\DSH Works\Project\PredatorSense\app\experiments\Predex.WinUI"
& $sdk build Predex.WinUI.csproj -p:Platform=x64 -nodeReuse:false      # 0 Warning / 0 Error
```

产物：`bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\`（干净构建 93 个文件）

- 必须有 `resources.pri`（约 1.35 MB）——**不再有** `Predex.pri`
- `Predex.exe` 的 FileVersion = `3.0.8.0`（与 `app\Fork\Directory.Build.props` 对齐）
- 编译宏含 `PREYSENSE_NO_DRIVER`（同 `Directory.Build.props` 语义）

发布（输出目录同样自带 `resources.pri`，可整目录拷到别的机器）：

```powershell
& $sdk publish Predex.WinUI.csproj -p:Platform=x64 -nodeReuse:false
# → ...\win-x64\publish\Predex.exe（91 个文件）
```

### 9.2 PRI 由谁产出（原第 6 节 / 第 5 节的说法已作废）

| 属性 | 现在的值 |
|---|---|
| `EnableMsixTooling` | `true` |
| `AppxGeneratePriEnabled` / `AppxGeneratePrisForPortableLibrariesEnabled` | 不设置（默认 true） |
| 手写的 `GeneratePredexPri` Target（makepri + 暂存目录） | **已删除** |
| `tools\pri-stub` 那个自制 `Microsoft.Build.AppxPackage.dll` | **构建链不再依赖**（文件原样保留，没删） |

结论：便携 SDK（没有 VS、没有 Visual Studio 的 AppxPackage 任务）也能靠 Windows App SDK
自带的 build targets 干净产出 `resources.pri`。蓝图 §6.6 的判断成立。

### 9.3 启动崩溃 0xC0000374 的真实根因（原「已知问题 1」的后续）

原判断「非首个实例里调 `Application.Exit()` 导致崩溃」只对了一半。用 WinDbg 打开
`%LOCALAPPDATA%\CrashDumps\Predex.exe.*.dmp` 看到的真实栈是：

```
MRM!MrmFreeResource
Microsoft_Windows_ApplicationModel_Resources!ResourceMap::GetValueImpl
Microsoft_UI_Xaml!ModernResourceProvider::TryGetResourceCandidate / TryGetLocalResource
Microsoft_UI_Xaml!CApplication::LoadComponent
Microsoft_UI_Xaml!DXamlCore::EnsureCoreApplicationInitialized
Microsoft_UI_Xaml!FrameworkApplication::StartDesktop
Microsoft_UI_Xaml!Application.Start
Predex!Predex.WinUI.Program.Main
```

即：**任何一次启动**（首实例也一样）都可能在 `Application.Start` 里加载 `App.xaml`
时踩坏堆。crash.log 为空是因为进程在托管代码跑到 `OnLaunched` 之前就已经 native
fail-fast，`App.UnhandledException` 根本来不及写。

触发条件：手写的 `Predex.pri` 把 `Microsoft.UI.pri` / `Microsoft.UI.Xaml.Controls.pri`
的内容一起索引进同一个 PRI（makepri 还按 `<autoResourcePackage qualifier="Language"/>`
拆出 79 个 `Predex.language-*.pri`），MRM 解析候选资源时会在
`MrmFreeResource` 上踩坏堆。实测崩溃率约 50%（6 次启动 3 次崩），与「刚强杀完再启动
更容易崩」的现象一致——但**不是**单实例分支引起的。

改法：改用 `EnableMsixTooling=true` 让 SDK 产出 `resources.pri`（内容正确、不重复索引
框架 PRI），崩溃率归零。注意**升级后必须删掉输出目录里残留的 `Predex.pri`**，
MRM 会优先加载 `<exe名>.pri`，留着它等于没换。

### 9.4 单实例行为（`App.xaml.cs`）

- mutex 名 `PredexWinUI_Unique_Mutex_1234`，唤出事件名 `PredexWinUI_Show_Event`（红线：名字没改）
- 判定不再只看 `createdNew`：如果名字已存在，再 `WaitOne(0)`；能拿到（含
  `AbandonedMutexException`）说明上一个实例已经被强杀、只剩内核对象，本进程接管当主实例。
  这样「强杀后立刻重启」不会误判成次实例而静默退出。
- 次实例路径：先把唤出事件 `Set()`，然后**不再同步调用 `Application.Exit()`**——
  改为把退出投递到本线程的 `DispatcherQueue`（保证 XAML 根已初始化），投递不了才
  `Environment.Exit(0)`。原来的同步 `Exit()` 在 `OnLaunched` 里运行属于未定义时机。
- 期望行为：第二个 exe 启动后 1 秒内自己退出（exit code 0，无 WER 记录），
  已有实例的窗口被唤到前台。

### 9.5 怎么启动验证（不点按钮、不碰硬件）

```powershell
$out = "...\app\experiments\Predex.WinUI\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64"
Start-Process "$out\Predex.exe" -WorkingDirectory $out
Start-Sleep 3
# 窗口出现：class = WinUIDesktopWin32WindowClass，标题 Predex（420×490，任务栏右上方）
# 截图：pwsh -File docs\capture-ui.ps1 -Exe "$out\Predex.exe"
```

收尾（任何时候跑完硬件相关操作都要跑）：

```powershell
pwsh -NoProfile -File "G:\DeepSeek DSH\DSH Works\Project\PredatorSense\src\Restore-AutoFan.ps1"
```

### 9.6 崩溃怎么查

1. `crash.log`（exe 同目录）：只覆盖托管异常。**空的 crash.log + 没有窗口 = native fail-fast**。
2. `%LOCALAPPDATA%\CrashDumps\Predex.exe.*.dmp`：WER 自动留的 minidump。
3. 有 WinDbg（本机装在 MSIX 包里），可以直接：

   ```powershell
   Copy-Item "C:\Program Files\WindowsApps\Microsoft.WinDbg_*\amd64\*" $env:TEMP\cdb -Recurse
   & "$env:TEMP\cdb\cdb.exe" -z "$env:LOCALAPPDATA\CrashDumps\Predex.exe.<pid>.dmp" -c ".ecxr; k 30; q"
   ```

   （`C:\Program Files\WindowsApps\...\cdb.exe` 直接执行会「拒绝访问」，要先拷出来。）
4. `Get-WinEvent -FilterHashtable @{LogName='Application';Id=1000}` 看 `0xC0000374` / `0xC000027B`。
   - `0xC0000374`（ntdll heap corruption）→ 先看 PRI 是不是旧的/手写的（见 9.3）
   - `0xC000027B`（XAML/激活失败，常配 `Cannot locate resource from 'ms-appx:///...'`）→ 缺 `.pri` 或 `.xbf`

### 9.7 还剩什么没做（仅 S0 范围）

- `Program.cs` + `DISABLE_XAML_GENERATED_MAIN`（自写入口）**没做**。现在仍用 XAML 生成的
  `Main`；S0 不再需要它是为了 PRI 崩溃，但蓝图的「自包含 + 不双调 Bootstrap」那条仍建议后续补。
- `--no-autohide` 启动开关仍没实现（截图靠先启动再抓）。
- `Styles\Resources.xaml` 还没拆出 `ThemeDictionaries`（Light/Dark 两套同名 key）。
- `tools\pri-stub` 目录还在（构建链已不依赖）。哪天确认没人回退，可以整目录删掉。
- 真机项（Predator 键、托盘、失焦隐藏、模式切换）仍是 Mock 验证，见第 5 节已知问题 4。

### 9.8 S0 验收记录（2026-11-13 03:32–03:36，本机真机）

**A. 10 轮「启动 → 确认窗口出现 → 强杀 → 立刻再启动」**（每轮之间静置 20s，杀进程到下一次启动间隔 0.26–0.27s）

| 轮次 | 重新启动时刻 | 出窗口耗时 | 结果 | 备注 |
|---|---|---|---|---|
| 0 | 03:32:33.778 | 1.14s | OK | window 'Predex' class=WinUIDesktopWin32WindowClass |
| 1 | 03:32:55.368 | 1.01s | OK | killed_pid=11320 relaunch_delay=0.27s window 'Predex' class=WinUIDesktopWin32WindowClass |
| 2 | 03:33:17.171 | 1.13s | OK | killed_pid=6140 relaunch_delay=0.26s window 'Predex' class=WinUIDesktopWin32WindowClass |
| 3 | 03:33:39.003 | 1.01s | OK | killed_pid=21688 relaunch_delay=0.26s window 'Predex' class=WinUIDesktopWin32WindowClass |
| 4 | 03:34:00.757 | 1.16s | OK | killed_pid=7516 relaunch_delay=0.26s window 'Predex' class=WinUIDesktopWin32WindowClass |
| 5 | 03:34:22.684 | 1.26s | OK | killed_pid=7552 relaunch_delay=0.26s window 'Predex' class=WinUIDesktopWin32WindowClass |
| 6 | 03:34:44.684 | 1.13s | OK | killed_pid=1444 relaunch_delay=0.27s window 'Predex' class=WinUIDesktopWin32WindowClass |
| 7 | 03:35:06.510 | 1.13s | OK | killed_pid=29624 relaunch_delay=0.26s window 'Predex' class=WinUIDesktopWin32WindowClass |
| 8 | 03:35:28.366 | 1.05s | OK | killed_pid=8060 relaunch_delay=0.26s window 'Predex' class=WinUIDesktopWin32WindowClass |
| 9 | 03:35:50.157 | 1.13s | OK | killed_pid=34916 relaunch_delay=0.26s window 'Predex' class=WinUIDesktopWin32WindowClass |
| 10 | 03:36:12.035 | 1.27s | OK | killed_pid=24492 relaunch_delay=0.26s window 'Predex' class=WinUIDesktopWin32WindowClass |

结论：**10/10 出窗口**，无 0xC0000374、无新增 WER dump。

**B. 次实例（不依赖 Application.Exit() 的早退路径）**

| 项 | 观测值 |
|---|---|
| 主实例 A | 存活，窗口 `Predex` / `WinUIDesktopWin32WindowClass` |
| 次实例 B（A 存活时启动） | **0.35s 后自行退出，exit code 0x00000000** |
| A 是否被影响 | 否（B 退出后 A 仍存活，并收到唤出信号） |
| 新增崩溃 dump | 0 |

**C. 构建 / 发布**

- `build` → 0 Warning / 0 Error，输出 93 个文件，含 `resources.pri`（1,347,064 B）
- `publish` → 0 Error，`...\win-x64\publish\` 91 个文件，含 `resources.pri`；从 publish 目录直接启动 2/2 正常
- `Predex.exe` FileVersion = `3.0.8.0`，ProductVersion = `3.0.8`

---

## 10. S1 记录：单窗口壳（2026-11-13 04:0x，本机真机）

### 10.1 结构（旧的 MainWindow 已删除）

| 路径 | 作用 |
|---|---|
| `Shell\ShellWindow.xaml(.cs)` | 唯一窗口：40 DIP 自定义标题栏 + 页面宿主 + 托盘/热键/自动隐藏生命周期 |
| `Shell\ShellWindow.Animation.cs` | 显示/隐藏/换页动效 + `CubicBezierEase`（与窗口尺寸动画共用） |
| `Pages\HomePage.xaml(.cs)` | 首页：5 档性能模式 + 展开区的 GPU/刷新率/入口；VSM 两态 |
| `Pages\{Settings,Tuning,Fans,Lighting,Overlay}Page` | S1 占位骨架（真页面在 S2–S5 落地，见 `docs\SHELL-CONTRACT.md`） |
| `Services\ShellServices.cs` | 托盘 / 键盘钩子 / HID / 内存回收的宿主，不随窗口可见性销毁 |
| `Services\ShellUiState.cs` | 折叠态持久化：`%LOCALAPPDATA%\Predex\shell-ui.json`（**不写 preysense.json**） |
| `Services\ShellLog.cs` | `--trace` 开关的 shell.log（托盘/钩子/导航/隐藏原因） |
| `Glyphs.cs` / `Loc.cs` | Segoe Fluent 码点表（逐张渲染核对）/ XAML 文案入口 |

- 320 DIP 固定宽；折叠 116 / 展开 280 DIP；展开 220 ms、收起 200 ms（逐帧 `MoveAndResize`，锚右下）。
- 导航不再用 NavigationView（320 宽放不下）：首页入口行 5 个按钮切 5 个页面，非首页时标题栏换成「返回 + 页面名」。

### 10.2 验收实测（`docs\s1-verify.ps1`，快照构建产物）

| 项 | 结果 |
|---|---|
| clean build | 0 warning / 0 error（`-t:Rebuild`，输出含 `resources.pri`，无 `Predex.pri`） |
| 窗口枚举 | `EnumWindows` 按 PID：class=`WinUIDesktopWin32WindowClass`、title=`Predex` |
| 渲染 | `PrintWindow(PW_RENDERFULLCONTENT)` ink 19–37%（非空白），另存屏幕抓图对照 |
| 折叠/展开 | 480×174 / 480×420 物理像素 = 320×116 / 320×280 DIP @150% |
| 六页 | UIA 点击 `OpenLighting/Overlay/Fans/Tuning/Settings` + `BtnBack`，全部切页成功 |
| 关闭 | `BtnClose` → 窗口 `visible=False`、进程存活 |
| 托盘 | 向窗口发 `WM_TRAY_CALLBACK`(=WM_USER+2048) + `WM_LBUTTONUP` → 窗口重新可见 |
| 热键 | 注入 VK `0xCC`：第 1 次隐藏、第 2 次显示（低级钩子生效） |
| 遥测 | 真机：`76°C / 55°C`、`CPU 4109 RPM / GPU 4054 RPM`、`165 Hz`、`Intel(R) UHD Graphics` |
| 描边 | 窗口外圈 2 px 采样全为 `#202020` 同色（无白线）；`WS_POPUP` + `DWMWA_COLOR_NONE` |

### 10.3 S1 欠账

- 截图是在**快照构建**（临时目录，5 个占位页）里跑的：证明的是**壳行为**；S2–S5 的真页面内容未在本轮验证。
- `SHELL-CONTRACT.md` 的 `PageTypes` 映射仍指向 S1 占位页；集成方需改到真页面。
- 拖拽（`WM_NCLBUTTONDOWN`）与最大化按钮只做了代码路径，未做鼠标/点击实测。
- 隐藏 30 s 后内存未测（`MemoryManager` 逻辑未改）。
- 折叠态标题栏只放温度；风扇转速在展开态的遥测行（受 116 DIP 高度预算限制）。
- 非 en/zh 语言下 S1 新增的 8 个 key 回落到英文（`WinUIStrings` 只补了 zh-CN）。
- `ShellInfoBar` 仍空置（ModeToast 仍写 Debug），S6 再接。

---

## 11. S6 记录：Overlay 真页面 + 提示层 + 隐藏释放 UI 树 + 垫片清理（2026-11-13 05:25–05:45）

详细报告与全部实测数字：`docs\S6-REPORT.md`。这里只留结论和坑。

### 11.1 做了什么

| 项 | 结果 |
|---|---|
| `Pages\OverlayPage` | 从 S1 骨架变成真页面：指标槽位（CPU 10 / GPU 13 项、曲线开关、增删）、HUD 缩放 50–200 %、背景/文字不透明度、水平/垂直位置、主题 Dark/White、Show FPS；全部经 Core `OverlayConfig`（和 WPF `MetricsWindow` 同一入口），刷新间隔做成只读「1 秒（固定）」——Core 里没有这个字段 |
| 提示层 | 新 `Services\ContentDialogUserPromptService.cs` 实现 WPF 的 `IUserPromptService`（确认=确认+取消、通知=单按钮），在 `ShellWindow` 里 `ViewModel.UserPrompts = ...`；危险确认用红色 `DangerPrimaryButtonStyle`。`Views\CustomModalDialogCompat.cs`（Win32 MessageBox）已删 |
| `ModeToastWindow` | 不再只写 Debug：壳内 `InfoBar` 顶部滑入 + **3 秒自动消失**，`InfoWindowService`（风扇/模式失败提示）也复用同一条；不开第二窗口（内存考虑） |
| 内存 | 隐藏时清空 `PageHost` + 丢全部页面实例 + 立即 `GC.Collect`/`SetProcessWorkingSetSize`；唤起时按上次 nav key 重建页面并按页面内容量定高（二级页重建实测仍是 520 DIP） |
| 垫片 | 删 `CustomModalDialogCompat.cs`；`WpfShims`/`AppCompat`/`InfoWindowServiceCompat`/`ModeToastCompat` 因链接的 WPF 文件仍引用，**不能删**（删=改 Frontend，红线），后两者已改成真功能 |

### 11.2 两个新踩的坑（给后续阶段）

1. **ContentDialog 的 `DefaultButton=Primary` 会覆盖 `PrimaryButtonStyle`**：WinUI 的
   `PrimaryButtonAsDefaultButton` 可视状态把 `PrimaryButton.Style` 强制改成框架 `AccentButtonStyle`，
   自定义按钮样式（红色危险按钮）静默失效，长按钮文字还会被截断。改 `DefaultButton=None` 后生效。
2. **ContentDialog 在 XamlRoot 的 popup 层，不继承 `RootGrid.RequestedTheme`**：不显式设
   `dialog.RequestedTheme` 就可能跟壳主题不一致。

另：`Slider` 在 WinUI 没有 WPF 的 `IsSnapToTickEnabled`（吸附要在 `ValueChanged` 里做）；
`ElementCompositionPreview.SetIsTranslationEnabled` 后再动 `Translation` 才不会覆盖布局偏移。

### 11.3 内存实测（`docs\s6-memory.ps1`，隐藏 30 s）

| 采样点 | 改动前 | 改动后 |
|---|---|---|
| 隐藏 +1.5 s | 214.3 MB | 53.1–54.6 MB |
| 隐藏 +30 s（验收点） | 16.3–16.7 MB | 18.0–19.0 MB |
| 隐藏 +30 s 私有 | 97.5–97.6 MB | 96.4–98.7 MB |

任务书里的「隐藏后 244 MB」本机复现不出来；能对上的是「隐藏了但 8 s 空闲回收还没跑」那一刻（214 MB）。
改动前 +30 s 就已经低于 25–35 MB 目标，本轮真正的收益是**隐藏后 2 s 内就释放**（214 → 53 MB），
30 s 稳态没有变好（差异 ~2 MB，在重复测量波动内）。私有内存 ~97 MB 是 WinUI/DWM 原生占用，清页面树收不回。

### 11.4 构建 / 发布

```
build -t:Rebuild  → 0 warning / 0 error（24.96 s），93 个文件，resources.pri 1,409,752 B，无 Predex.pri
publish -c Release → 90 文件，resources.pri 1,397,232 B；从 publish 启动：pid 14264，
                    窗口 class WinUIDesktopWin32WindowClass / title Predex / 480×450 px / PrintWindow ink 33.2%
Restore-AutoFan   → SetGamingFanBehavior 0x00410009 (Auto) -> True；CPU=76C GPU=52C fan=4054/4054 mode=1
```

### 11.5 还没做（详见 S6-REPORT §8）

覆盖层 HUD 本体与「改缩放后覆盖层真的变」没验（要开独立 OSD + 写 preysense.json，真机纪律禁止）；
GPU MUX 确认只做了同文案的 `--demo=confirm`（真实触发会写 EC/NVAPI 并要求重启）；WPF 在产版本轮没有
重新构建（也没改它的源码）；S7（切默认入口）不在本轮范围。
