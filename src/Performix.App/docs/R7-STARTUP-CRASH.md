# R7 — 启动偶发崩溃 0xC000027B：根因、修复与复验

> 对象：`app\experiments\Predex.WinUI`（WinUI 3 版 Predex），Release 发布到 `app\dist-winui`。
> 本文记录 2026-11-13 R6 收尾时发现的「约 10 次启动崩 1 次」问题的定位与修复。
> 改动范围只有 `Predex.WinUI`；`app\Fork\**`、`PreySense.Tests`、`tools\**`、`PSSvc`、计划任务、
> `preysense.json` 结构均未改动。

## 1. 现象（与 R6-OSD.md §7 记录一致）

- WER：`Predex.exe` / `Microsoft.UI.Xaml.dll` / `0xC000027B`（stowed exception），`crash.log` 为空/不存在。
- 崩溃发生在浮层窗口创建之前；`shell.log` 最后一行总是
  `compat: Dispatcher.CurrentDispatcher off the UI thread … App.get_Dispatcher ← MainViewModel.OnPowerModeChanged ← ProductionHardwareService.SetPowerMode ← MainViewModel.RestoreSavedPowerMode`。
- 只在 `--trace` 启动时出现（`--trace` 才写 `shell.log`）。

## 2. 复现与抓落地证据

- 压测方式：`Start-Process Predex.exe --trace --crashdiag` → 等 6 s → 按 PID `Stop-Process -Force`，
  连续 40 次（Release 构建，与 dist 同一份输出）。
- **修复前：40 次里第 3 次崩**，症状与现场一致：`0xc000027b` / `Microsoft.UI.Xaml.dll` / 窗口未出
  / `crash.log` 不存在 / `shell.log` 以 compat 行为最后一行，且当次 `home: loaded` 行缺失。
- 打开 WER LocalDumps（临时，见 §7）后拿到 616 MB 全内存 dump；`cdb + !analyze / kv` 解出完整栈。

## 3. 真正的根因（dump 证据）

崩溃线程（UI 线程）的调用栈，自内向外：

```
KERNELBASE!RaiseFailFastException
combase!RoFailFastWithErrorContextInternal2          HRESULT = 0x8000FFFF (E_UNEXPECTED)
Microsoft_UI_Xaml!_CheckProcessRequirements+0x18e5
…（XAML 内部）
CoreMessagingXP!…（DispatcherQueue 消息派发）
user32!CallWindowProcW
…
combase!CoWaitForMultipleHandles+0xbb                ← CLR 在 STA 线程上的等待会抽消息
coreclr!…                                            （Monitor 等待基础设施）
System.Threading.Monitor.Enter_Slowpath
System.Threading.Monitor.Enter
Predex!Predex.WinUI.Services.ShellLog.Write          ← 就卡在这里
Predex!Predex.WinUI.Pages.HomePage.OnLoaded
Microsoft_WinUI!…RoutedEventHandler.Do_Abi_Invoke
Microsoft_UI_Xaml!…                                  （XAML 事件派发 / 消息循环）
```

机制（一句话）：**trace 日志的锁把 UI 线程堵在 XAML 事件里，等待时抽了消息，于是刚排进 UI 队列的
DispatcherQueue 回调重入 XAML，XAML 状态检查失败 → fail-fast。**

展开：

1. `ShellLog.Write` 原来是 `lock (Gate) + File.AppendAllText`（阻塞等待 + 同步写文件）。
2. 启动恢复流程里，工作线程写 compat 兜底行（就是最后那条日志），同时 UI 线程在
   `HomePage.OnLoaded` 里写 `home: loaded` → 两边抢同一把锁。
3. UI 线程在 `Monitor.Enter` 上等待；STA 线程的等待走 `CoWaitForMultipleHandles`，**会抽消息**。
4. 工作线程在写 compat 行的同一瞬间，`OnPowerModeChanged` 把 `Dispatcher.InvokeAsync(...)` 排进了
   UI 队列；UI 线程在抽消息时把它派发出来，于是在 `Loaded` 事件尚未返回时**重入 XAML**。
5. XAML 内部检查失败（E_UNEXPECTED）→ `RoFailFastWithErrorContext` → `0xC000027B`。

这也解释了为什么最后一行日志偏偏是 compat 行：它既是锁的持有者，也是把消息排进队列的那一方；
两者撞在同一毫秒级窗口里，所以是「偶发、约 1/10」。

## 4. 实测排除的方向（与上报时的猜测不同）

| 猜测 | 实测 | 结论 |
|---|---|---|
| `Dispatcher.InvokeAsync(async …)` 的 async-void 延续抛异常 | `--crash-test=asyncvoid`：C# 选的是 `Func<Task>` 重载，异常落在被丢弃的 Task 上，进程存活 | 不是这个 |
| 工作线程直接改 XAML 绑定属性（`DiagnosticMessage`/`ObservableProperty`） | `--crash-test=crossthread`：thread-guard 记录到跨线程 raise，但 XAML 没有 fail-fast，进程存活 | 不是这个 |
| `ProductionHardwareService.SetPowerMode` 在工作线程同步 Invoke handler | compat 兜底已经把回调 marshal 回 UI 队列，handler 本身在 UI 线程跑；它只是「最后一行的作者」 | 不是这个 |
| 任意原始 `DispatcherQueue` 回调抛异常 | `--crash-test=dispatcherthrow`：会崩，但模块是 `CoreMessagingXP.dll` 且**不会**触发 `Application.UnhandledException` | 与本次模块不符 |

所以 `OnPowerModeChanged` / `SetPowerMode` 的线程模型没有问题，**不需要**改 marshal 方式或推迟
`RestoreSavedPowerMode`；真正要修的是「日志把 UI 线程堵住」这一点。

## 5. 修复（WinUI 侧）

1. `Services\ShellLog.cs:50-66` — `lock` → `Monitor.TryEnter`（0 ms 超时）。
   拿不到锁就**丢这一行并计数**，绝不让调用线程等待。文件头写了线程规则与这次崩溃的来龙去脉。
   代价：极端竞争下会少一行 trace（可接受；trace 本来只是 opt-in 诊断）。
2. `Diagnostics\CrashDoctor.cs` — 新增的取证日志同样改为 `TryEnter` 丢记录，
   避免诊断代码自己引入同类阻塞、扰动要抓的崩溃。
3. 崩溃取证增强（App.xaml.cs / ShellWindow.xaml.cs，均为可关开关，正常启动无行为变化）：
   - `crash.log` 记录 `UnhandledException` / `AppDomain.UnhandledException` /
     `TaskScheduler.UnobservedTaskException`，带**线程 id、是否 UI 线程、启动后毫秒数、完整 `ToString()`**，
     并附 `shellLogDrops`（日志竞争丢行数）。
   - `--crashdiag`：`FirstChanceException` 去重、限量写 `crashdiag.log`（原生 fail-fast 也会留下
     「崩之前抛过什么」的记录；本次定位正是靠它确认崩前**没有**托管异常）。
   - `--crash-test=asyncvoid|dispatcherthrow|crossthread`：用于区分上面几类失败形态（§4 就是用它们测的）。
   - `--ignore-unhandled`：把 `Application.UnhandledException` 标成 Handled（WPF 版
     `DispatcherUnhandledException` 的等价物），默认**关**，不掩盖真问题。
   - `ShellWindow.AttachViewModelThreadGuard()`：VM 的 `PropertyChanged` 若在非 UI 线程触发就写
     `thread-guard` 行 + setter 栈。

## 6. 复验（修复后，dist-winui 发布版）

**A. 验收口径 20 次（`--trace`，正是会崩的配置）：20/20 出窗口、0 崩溃、0 WER、无 crash.log**

| 次 | 出窗口 | 结论 | shell.log 末行（摘要） |
|---|---|---|---|
| 1 | 是 | 正常 | home: telemetry cpu=73°C |
| 2 | 是 | 正常 | home: telemetry cpu=76°C |
| 3 | 是 | 正常 | home: telemetry cpu=74°C |
| 4 | 是 | 正常 | home: telemetry cpu=73°C |
| 5 | 是 | 正常 | home: telemetry cpu=70°C |
| 6 | 是 | 正常 | home: telemetry cpu=71°C |
| 7 | 是 | 正常 | home: telemetry cpu=71°C |
| 8 | 是 | 正常 | home: telemetry cpu=71°C |
| 9 | 是 | 正常 | home: telemetry cpu=72°C |
| 10 | 是 | 正常 | home: telemetry cpu=73°C |
| 11 | 是 | 正常 | home: telemetry cpu=70°C |
| 12 | 是 | 正常 | home: telemetry cpu=70°C |
| 13 | 是 | 正常 | home: telemetry cpu=69°C |
| 14 | 是 | 正常 | home: telemetry cpu=67°C |
| 15 | 是 | 正常 | home: telemetry cpu=72°C |
| 16 | 是 | 正常 | home: telemetry cpu=71°C |
| 17 | 是 | 正常 | home: telemetry cpu=70°C |
| 18 | 是 | 正常 | home: telemetry cpu=72°C |
| 19 | 是 | 正常 | home: telemetry cpu=72°C |
| 20 | 是 | 正常 | home: telemetry cpu=71°C |

**B. 加倍压测 20 次（`--trace --crashdiag`，修复前正是这个配置第 3 次崩）：20/20 出窗口、0 崩溃、0 WER。**

**C. 全量累计**：修复过程中先在等价构建上跑过 20（`--trace`）+ 40（`--trace --crashdiag`），
最终发布构建再跑 20 + 20，合计 **140 次启动 0 崩溃、0 WER、0 crash.log**；
其中最终 `app\dist-winui` 这版二进制本身跑了 **40 次 0 崩溃**。
`shell.log` 完整（`home: loaded` / `home: telemetry` 等行都在），窗口、托盘、热键初始化行与修复前一致。

## 7. 复跑方法

```powershell
# 构建 + 发布（与验收命令一致）
cd "G:\DeepSeek DSH\DSH Works\Project\PredatorSense\app\experiments\Predex.WinUI"
& "..\..\..\tools\dotnet-sdk\dotnet.exe" build Predex.WinUI.csproj -p:Platform=x64 -nodeReuse:false -t:Rebuild
pwsh -NoProfile -File "G:\DeepSeek DSH\DSH Works\Project\PredatorSense\app\build-winui.ps1"
```

压测脚本（本次用，位于仓库外，可重建）：每次 `Start-Process app\dist-winui\Predex.exe --trace` →
等 6 s → `Stop-Process -Id <pid> -Force`；统计 `$p.HasExited` / `MainWindowHandle` / WER 事件 / `crash.log`。

若还要抓 dump（本次定位用；WER 全局 `Disabled=1` 时 `LocalDumps` 子键仍然生效）：

```powershell
$k='HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\Predex.exe'
New-Item -Path $k -Force | Out-Null
New-ItemProperty $k -Name DumpFolder -PropertyType ExpandString -Value 'C:\Users\Public\predator-crash-lab\dumps' -Force
New-ItemProperty $k -Name DumpType   -PropertyType DWord -Value 2 -Force
New-ItemProperty $k -Name DumpCount  -PropertyType DWord -Value 2 -Force
# 用 WinDbg 自带的 cdb 分析（WindowsApps 目录不能直接执行，先复制出来）：
# cdb.exe -z <dump> -cf <脚本>.txt   # 脚本内：.reload / .ecxr / .exr -1 / kv / !analyze -v / ~*k
# 分析完删掉 $k
```

## 8. 遗留说明

- 本次根因与 `app\Fork` 无关，未改动 `app\Fork\**`。
- 其余启动期阻塞点（VM 内部 `lock`、硬件 pipe 调用）没有出现在失败栈里；如以后再遇到同签名崩溃，
  `crash.log` 现在会带线程与 `shellLogDrops`，`--crashdiag` 会留下崩溃前的首异常链，可直接比对。
- `--ignore-unhandled` 只是备用的 WPF 对齐开关，默认关闭：宁可让真异常可见，也不默认吞掉。
