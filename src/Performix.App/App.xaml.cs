using Microsoft.UI.Xaml;
using Predex.WinUI.Services;
using Predex.WinUI.Services.Update;
using PreySense.Frontend.Services;
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Predex.WinUI
{
    public partial class App : Application
    {
        // 单实例对象名（红线：名字不许改，WPF 版切换期要靠它对上）。
        private const string SingleInstanceMutexName = "PredexWinUI_Unique_Mutex_1234";
        private const string ShowEventName = "PredexWinUI_Show_Event";

        /// <summary>
        /// Opt-in (`--ignore-unhandled`): mark <see cref="Application.UnhandledException"/>
        /// handlable instead of letting XAML fail-fast (0xc000027b). This is the WinUI
        /// counterpart of the WPF shell's DispatcherUnhandledException policy
        /// (PreySense.Frontend\App.xaml.cs:35-60), where a runtime exception is logged and
        /// the app keeps running. Off by default so real failures stay visible.
        /// </summary>
        private static bool _keepAliveOnUnhandledException;

        private static Mutex? _singleInstanceMutex;
        private static EventWaitHandle? _showEvent;
        private static Thread? _showThread;
        private static volatile bool _exiting;
        private static bool _ownsMutex;
        private static AcerHotkeyWatcher? _acerHotkeys;

        /// <summary>
        /// Acer operation-mode bytes live in <see cref="TurboKeyToggle.TurboMode"/> /
        /// <see cref="TurboKeyToggle.BalancedMode"/> (MainViewModel.PowerModes: 0=Quiet,
        /// 1=Balanced, 4=Performance, 5=Turbo, 6=Eco). The ACPI hotkey drives the exact same
        /// command the Home page's Turbo tile issues (<c>SetModeCommand</c>); no separate
        /// hardware write path is opened here, and keyboard lighting is never touched — the
        /// Turbo LED is firmware behaviour that follows the operating mode.
        /// </summary>
        private const int SimulatedPressSettleMilliseconds = 6000;

        /// <summary>
        /// Set by <c>--no-autohide</c>: the window stays open when it loses focus, which is
        /// what the screenshot / UI-automation runs need (S0 never implemented the switch).
        /// </summary>
        internal static bool NoAutoHide { get; private set; }

        /// <summary>The single shell window (pages reach it through this, no DI container).</summary>
        internal static Shell.ShellWindow? CurrentShell { get; set; }

        private Shell.ShellWindow? _shell;

        private static void LogCrash(string where, object? error) => Diagnostics.CrashDoctor.Log(where, error);

        public App()
        {
            // First statement: install AppDomain / TaskScheduler / (opt-in) first-chance hooks
            // before any XAML resource, config or hardware object can throw.
            string[] startupArgs = Environment.GetCommandLineArgs();
            Diagnostics.CrashDoctor.Install(startupArgs);
            _keepAliveOnUnhandledException = startupArgs.Any(a =>
                a.Equals("--ignore-unhandled", StringComparison.OrdinalIgnoreCase));

            UnhandledException += (_, e) =>
            {
                LogCrash("App.UnhandledException",
                    $"message={e.Message} handledBefore={e.Handled}\n{e.Exception}");
                if (_keepAliveOnUnhandledException)
                {
                    // WPF parity: the shipped WPF shell always sets Handled and keeps running.
                    e.Handled = true;
                    LogCrash("App.UnhandledException", "marked handled (--ignore-unhandled): process stays alive");
                }
            };
            // AppDomain.UnhandledException / TaskScheduler.UnobservedTaskException are installed
            // by CrashDoctor.Install above (they carry terminating flag / SetObserved handling).
            try { InitializeComponent(); }
            catch (Exception ex) { LogCrash("App.InitializeComponent", ex); throw; }
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            // ── Probe mode (read-only hardware self-check) ──────────────────────
            if (PreySense.Frontend.Services.ProbeMode.IsProbeRequested(Environment.GetCommandLineArgs()))
            {
                int code;
                try { code = PreySense.Frontend.Services.ProbeMode.Run(Environment.GetCommandLineArgs()); }
                catch { code = 4; }
                Exit();
                Environment.Exit(code);
                return;
            }

            // ── Update check switch (--check-update) ─────────────────────────────
            // One headless check, printed and written to %LOCALAPPDATA%\Performix\logs\
            // update-check.log, then exit. Runs before the single-instance guard and before any
            // XAML window exists, so it also works while another Performix is running and can
            // never leave a window behind. "No release published yet" is exit code 0: the
            // repository having no release is a fact about the feed, not an error.
            string[] updateArgs = Environment.GetCommandLineArgs();
            if (UpdateCli.IsCheckUpdateRequested(updateArgs))
            {
                int exitCode;
                try { exitCode = UpdateCli.RunCheck(updateArgs); }
                catch { exitCode = 1; }
                Exit();
                Environment.Exit(exitCode);
                return;
            }

            // ── Single instance guard ────────────────────────────────────────────
            // 注意：不能在这一分支里调 Application.Exit()。
            // OnLaunched 运行时 XAML 根（Application.Current / CoreWindow 之类）还没建完，
            // 直接同步 Exit 会在 native 侧 fail-fast 成 0xC0000374 (ntdll heap corruption)：
            // 没有窗口、crash.log 为空、WER 只留一条 Application Error。
            // 正确做法：先把「显示窗口」信号发给已有实例，再让退出排到消息循环里执行。
            if (!TryClaimPrimaryInstance())
            {
                SignalExistingInstance();
                ExitWithoutTouchingXamlRoot();
                return;
            }

            // ── Compat shim: capture the UI thread's DispatcherQueue ────────────
            // Must happen here: OnLaunched is the earliest point that is guaranteed to run on
            // the WinUI UI thread and that precedes the shell / ViewModels. WPF's
            // App.Current.Dispatcher is a process-wide UI dispatcher, so a worker-thread
            // lookup must hand back this queue instead of throwing (see WpfShims.Dispatcher).
            if (!System.Windows.Threading.Dispatcher.CaptureUiQueue(
                    Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()))
            {
                LogCrash("App.CaptureUiQueue",
                    "DispatcherQueue.GetForCurrentThread() was null on the OnLaunched thread — " +
                    "worker-thread Dispatcher.CurrentDispatcher lookups have no fallback.");
            }

            // ── Compat shim: expose a fake Application.Current ──────────────────
            System.Windows.Application.SetCurrent();

            // ── Cross-process show event ─────────────────────────────────────────
            try
            {
                _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
                _showThread = new Thread(() =>
                {
                    while (!_exiting)
                    {
                        try
                        {
                            _showEvent.WaitOne();
                            if (_exiting) break;
                            _shell?.DispatcherQueue.TryEnqueue(() => _shell?.ShowAndActivate());
                        }
                        catch (ObjectDisposedException) { break; }
                        catch { break; }
                    }
                }) { IsBackground = true, Name = "Predex_ShowListener" };
                _showThread.Start();
            }
            catch { }

            LocalizationService.Initialize();
            _ = Task.Run(PreySense.Overlay.WinRing0Cleanup.RemoveInstalledDriver);

            // First run defaults to "Run on Startup" on (tray only, --minimized); a saved
            // answer always wins. Best effort — a failure never blocks startup.
            _ = Task.Run(PreySense.Frontend.Services.StartupTaskService.ResolveStartupPreference);

            string[] commandLine = Environment.GetCommandLineArgs();

            // ── Update session (check / download / verify / replace) ─────────────
            // Created before the shell so the Updates page can render the machine immediately.
            // A test feed (--update-feed) implies "do not install": verification runs against
            // fake packages and must never touch the real installation.
            UpdateOptions updateOptions = UpdateCli.OptionsFromCommandLine(commandLine);
            bool suppressInstall = UpdateCli.IsInstallSuppressed(commandLine) ||
                                   !string.IsNullOrWhiteSpace(updateOptions.FeedOverride);
            UpdateSession.Initialize(updateOptions, suppressInstall);

            // Verification opt-out (--update-no-auto): read before anything can start the loop.
            UpdatePreferences.ApplyCommandLine(commandLine);

            NoAutoHide = commandLine.Any(a => a.Equals("--no-autohide", StringComparison.OrdinalIgnoreCase));
            if (commandLine.Any(a => a.Equals("--trace", StringComparison.OrdinalIgnoreCase)))
                ShellLog.Enable();
            // Verification only: pretend "设置 → 辅助功能 → 视觉效果 → 动画效果" is off, so the
            // overlay's spec §7 degraded path can be exercised (and screenshotted) on demand.
            if (commandLine.Any(a => a.Equals("--no-anim", StringComparison.OrdinalIgnoreCase)))
                Views.ModeOsdWindow.ForceNoMotion = true;
            ShellLog.Write("app",
                $"start autohide={!NoAutoHide} trace={ShellLog.Enabled} noanim={Views.ModeOsdWindow.ForceNoMotion}");

            bool startHidden = commandLine.Any(a =>
                a.Equals("--minimized", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--hidden", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--tray", StringComparison.OrdinalIgnoreCase));

            try { _shell = new Shell.ShellWindow(); }
            catch (Exception ex) { LogCrash("new ShellWindow()", ex); throw; }

            if (!startHidden)
                _shell.ShowAndActivate();
            else
                _shell.InitTrayOnly();

            // ── "已更新到 x.y.z" notice ───────────────────────────────────────────
            // invoke-update.ps1 restarted us with --updated-from <old version>; that is the only
            // signal that this is the first run of a freshly swapped build.
            NotifyUpdatedLaunch(commandLine);

            // ── Acer ACPI-WMI hotkeys (Turbo / extreme mode key) ─────────────────
            // Started after the shell exists: the action must go through the shell's
            // ViewModel, and the takeover below needs a constructed MainViewModel.
            TakeOverViewModelModeKey();
            StartAcerHotkeyWatcher();

            // Verification only: replay the Turbo key N times through the real handler.
            StartTurboKeySimulation(commandLine);

            // Crash investigation only (--crash-test=...): deliberately throw,
            // off the UI thread, or in an async-void dispatcher callback. Inert otherwise.
            Diagnostics.CrashDoctor.MaybeRunSelfTest(commandLine, _shell);

            _ = UpdateCheckService.CheckForUpdatesAsync();

            // ── S6 UI demo hooks (verification only; never touch hardware) ────────
            //   --demo=notify   ContentDialogUserPromptService.NotifyAsync (GPU power-cycle text)
            //   --demo=confirm  ContentDialogUserPromptService.ConfirmAsync (GPU MUX text, cancel path)
            //   --demo=toast    shell InfoBar toast (ModeToastWindow replacement), repeating
            //   --demo=osd[:<mode>]  mode-switch OSD overlay (TOAST-ANIMATION-SPEC), repeating;
            //                   <mode> is an Acer byte (0/1/4/5/6) or Eco/Quiet/Balanced/
            //                   Performance/Turbo|Beast. Omit it to cycle all five.
            // The demo path calls the prompt/toast layer directly; it never invokes a ViewModel
            // command, so no EC / WMI / NVAPI write and no restart can be triggered.
            string? demo = commandLine
                .FirstOrDefault(a => a.StartsWith("--demo=", StringComparison.OrdinalIgnoreCase))
                is { } demoArg ? demoArg["--demo=".Length..] : null;
            if (!string.IsNullOrWhiteSpace(demo))
            {
                ShellLog.Write("app", $"ui demo requested: {demo}");
                _shell.RunUiDemo(demo);
            }

            StartUpdateAutoRun(UpdateCli.AutoMode(commandLine));

            // ── Automatic updates (on by default; the Update page has the switch) ────
            // The status card is attached before the loop so the very first "checking" state has
            // somewhere to go. A click on the card opens the Update page: that is the detail view
            // (the card itself is the quiet status surface, never a modal dialog).
            UpdateStatusPresenter.Attach(_shell.DispatcherQueue, () =>
            {
                Shell.ShellWindow? shell = CurrentShell;
                if (shell is null) return;
                shell.NavigateTo("Update");
                shell.ShowAndActivate();
            });
            UpdateAutoRunner.Start(commandLine);
        }

        /// <summary>
        /// True once <see cref="RequestExit"/> has run: a shutdown is under way. The install
        /// fallback (UpdateShutdownProbe) reads it to tell "slow close" from "stuck".
        /// </summary>
        internal static bool IsExitRequested => _exiting;

        /// <summary>
        /// One-off toast + log for the first launch after a successful self-update. The version
        /// hand-off comes from the replacement script (--updated-from), the wording from
        /// LocalizationService, and the shell InfoBar from InfoWindowService.
        /// </summary>
        private static void NotifyUpdatedLaunch(string[] commandLine)
        {
            string? previous = UpdateLaunchNotice.ParseUpdatedFrom(commandLine);
            if (previous is null) return;

            string now = UpdateCheckService.InformationalVersion;
            ShellLog.Write("update",
                $"started after an update: now {now}, was {previous} " +
                $"(restarted by invoke-update.ps1 with {UpdateLaunchNotice.UpdatedFromSwitch})");

            try
            {
                InfoWindowService.ShowInformation(
                    LocalizationService.Format(UpdateLaunchNotice.UpdatedTitleKey, now),
                    LocalizationService.Format(UpdateLaunchNotice.UpdatedFromKey, previous));
            }
            catch (Exception ex)
            {
                // A toast that cannot render must not cost the log line above.
                ShellLog.Write("update", $"updated notice failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Verification hook (never used by a normal run): <c>--update-auto=check|download</c>
        /// drives the update session once after startup so the screenshot script can photograph
        /// the real states without clicking. Installing is suppressed separately by
        /// <c>--update-no-install</c> or by using a test feed.
        /// </summary>
        private static void StartUpdateAutoRun(string? mode)
        {
            if (string.IsNullOrWhiteSpace(mode)) return;

            ShellLog.Write("update", $"verification auto-run requested: {mode}");
            _ = Task.Run(async () =>
            {
                try
                {
                    await UpdateSession.CheckAsync();
                    if (string.Equals(mode, "download", StringComparison.OrdinalIgnoreCase))
                        await UpdateSession.DownloadAsync();
                }
                catch (Exception ex)
                {
                    ShellLog.Write("update", $"auto-run failed: {ex.GetType().Name}: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Subscribes root\wmi:APGeEvent and routes the Turbo key through
        /// <see cref="TurboKeyToggle"/> (toggle Turbo ↔ previous mode). Everything is
        /// best-effort: a missing WMI class or a denied subscription is logged and the shell
        /// keeps running (docs\KEY-EVENT-SOURCES.md §3).
        /// </summary>
        private static void StartAcerHotkeyWatcher()
        {
            try
            {
                _acerHotkeys = new AcerHotkeyWatcher();
                _acerHotkeys.TurboKeyRequested += detail => OnTurboKeyPress(detail, simulated: false);
                _acerHotkeys.Start();
            }
            catch (Exception ex)
            {
                ShellLog.Write("acer hotkey", $"init failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// One Turbo-key press. Runs on the WMI callback thread (MTA), so the ViewModel is only
        /// touched after the dispatch to the UI thread; <see cref="TurboKeyToggle"/> then decides
        /// the direction from the live mode and serialises overlapping presses.
        /// </summary>
        private static void OnTurboKeyPress(byte[] detail, bool simulated)
        {
            string tag = simulated ? " [simulated]" : string.Empty;
            ShellLog.Write("acer hotkey",
                $"turbo key request{tag} (detail {BitConverter.ToString(detail)}) → toggle, direction from ActivePowerMode");
            Shell.ShellWindow? shell = CurrentShell;
            if (shell is null)
            {
                ShellLog.Write("acer hotkey", $"turbo key request{tag} dropped: shell not created yet");
                return;
            }
            shell.DispatcherQueue.TryEnqueue(() => _ = TurboKeyToggle.ToggleAsync(shell.ViewModel, tag));
        }

        /// <summary>
        /// Verification hook: <c>--turbo-key-sim[=N]</c> feeds N synthetic APGeEvent payloads
        /// (07 04 00 = game-key family / Turbo key) through the exact same handler the WMI
        /// watcher raises. A physical key press cannot be injected from code, so this is how the
        /// toggle is proven end-to-end on the real machine.
        /// Only honoured together with <c>--trace</c>: every line it writes is tagged
        /// "[simulated]", which must never appear in a support log without the trace opt-in.
        /// Timing knobs (ms, both optional): <c>--turbo-key-sim-delay=</c> before the first press
        /// (default 12000, room for a UIA harness to put the machine in a known mode) and
        /// <c>--turbo-key-sim-interval=</c> between presses (default 8000), which is also the
        /// settle window before each readback. <c>--turbo-key-sim-burst=</c> replaces the beat
        /// with a rapid-fire press spacing and a single readback at the end — that is the
        /// "user hammers the key" case, where the press serialisation has to show up in the log.
        /// </summary>
        private static void StartTurboKeySimulation(string[] commandLine)
        {
            string? simArg = commandLine.FirstOrDefault(a =>
                a.Equals("--turbo-key-sim", StringComparison.OrdinalIgnoreCase) ||
                a.StartsWith("--turbo-key-sim=", StringComparison.OrdinalIgnoreCase));
            if (simArg is null) return;

            if (!ShellLog.Enabled)
            {
                // Without --trace there is no log, so there is no way to label the press as
                // simulated — and a silent mode write is worse than no simulation at all.
                return;
            }

            int presses = 1;
            int separator = simArg.IndexOf('=');
            if (separator > 0 && (!int.TryParse(simArg[(separator + 1)..], out presses) || presses < 1 || presses > 10))
                presses = 1;
            int delay = ReadIntOption(commandLine, "--turbo-key-sim-delay=", 12000);
            int interval = ReadIntOption(commandLine, "--turbo-key-sim-interval=", 8000);
            int burst = ReadIntOption(commandLine, "--turbo-key-sim-burst=", -1);
            if (delay < 0) delay = 12000;
            if (interval < 0) interval = 8000;
            if (burst < -1) burst = -1;

            byte[] detail = { AcerHotkeyWatcher.CategoryGameKey, AcerHotkeyWatcher.TurboKeySubId, 0x00 };
            ShellLog.Write("turbo key",
                burst >= 0
                    ? $"SIMULATION armed: {presses} rapid press(es) of {BitConverter.ToString(detail)} in {delay} ms, {burst} ms apart (burst) — not a real key press [simulated]"
                    : $"SIMULATION armed: {presses} press(es) of {BitConverter.ToString(detail)} in {delay} ms, {interval} ms apart — not a real key press [simulated]");

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay);
                    for (int i = 1; i <= presses; i++)
                    {
                        if (_exiting) return;
                        ShellLog.Write("turbo key",
                            $"simulated press #{i}/{presses} ({BitConverter.ToString(detail)}) [simulated]");
                        OnTurboKeyPress(detail, simulated: true);

                        if (burst >= 0)
                        {
                            // Fire the next press while this one is still in flight.
                            if (i < presses) await Task.Delay(burst);
                        }
                        else
                        {
                            // TransitionDelay is 2 s plus the trailing apply; read back after that.
                            await Task.Delay(SimulatedPressSettleMilliseconds);
                            LogSimulatedModeReadback(i, presses);
                            if (i < presses) await Task.Delay(interval);
                        }
                    }

                    if (burst >= 0)
                    {
                        await Task.Delay(SimulatedPressSettleMilliseconds);
                        LogSimulatedModeReadback(presses, presses);
                    }
                    ShellLog.Write("turbo key", "SIMULATION finished [simulated]");
                }
                catch (Exception ex)
                {
                    ShellLog.Write("turbo key",
                        $"simulation failed: {ex.GetType().Name}: {ex.Message} [simulated]");
                }
            });
        }

        /// <summary>
        /// Reads the mode back through the OEM pipe (cmd 33 / GetGamingMiscSetting) after a
        /// simulated press — same readback the ViewModel trusts, so the log line is proof the
        /// toggle reached the EC and not just the UI.
        /// </summary>
        private static void LogSimulatedModeReadback(int press, int total)
        {
            Shell.ShellWindow? shell = CurrentShell;
            if (shell is null)
            {
                ShellLog.Write("turbo key", $"readback #{press}/{total}: shell gone [simulated]");
                return;
            }

            string mode;
            try
            {
                mode = shell.ViewModel.TryGetPowerMode(out byte live)
                    ? $"0x{live:X2}"
                    : "unavailable";
            }
            catch (Exception ex)
            {
                mode = $"read failed ({ex.GetType().Name})";
            }

            ShellLog.Write("turbo key",
                $"readback after simulated press #{press}/{total}: pipe mode={mode} " +
                $"(ActivePowerMode 0x{shell.ViewModel.ActivePowerMode:X2}) [simulated]");
        }

        private static int ReadIntOption(string[] commandLine, string prefix, int fallback)
        {
            string? raw = commandLine.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (raw is null) return fallback;
            return int.TryParse(raw[prefix.Length..], out int value) ? value : fallback;
        }

        /// <summary>
        /// Takeover: MainViewModel (linked from the WPF frontend) still starts its own
        /// TurboKeyWatcher from PreySense.Core, and that one treats <em>every</em> APGeEvent
        /// with detail[0]==7 as the mode key — on this firmware that includes the profile
        /// notify (7/1) and the custom key (7/2) — and it cycles the mode instead of
        /// selecting Turbo (KEY-EVENT-SOURCES §4: ModeKeyId 0x05 is wrong here, it is 0x04).
        /// Two consumers of one ACPI event would switch the mode twice per press, so the
        /// shell detaches only that handler. Its power-source subscription (detail[0]==8)
        /// stays attached, and nothing inside PreySense.Core / PreySense.Frontend is edited.
        /// </summary>
        private static void TakeOverViewModelModeKey()
        {
            try
            {
                PreySense.Frontend.ViewModels.MainViewModel? viewModel = CurrentShell?.ViewModel;
                if (viewModel is null)
                {
                    ShellLog.Write("acer hotkey", "takeover skipped: ViewModel not created");
                    return;
                }

                const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
                object? watcher = viewModel.GetType().GetField("_turboKeyWatcher", Flags)?.GetValue(viewModel);
                MethodInfo? onTurboKeyPressed = viewModel.GetType().GetMethod("OnTurboKeyPressed", Flags);
                if (watcher is null || onTurboKeyPressed is null)
                {
                    ShellLog.Write("acer hotkey",
                        "WARNING takeover failed (MainViewModel._turboKeyWatcher / OnTurboKeyPressed moved) — the old watcher still cycles the mode on the same key press");
                    return;
                }

                var handler = (EventHandler<PreySense.HotKeyEventArgs>)Delegate.CreateDelegate(
                    typeof(EventHandler<PreySense.HotKeyEventArgs>), viewModel, onTurboKeyPressed);
                watcher.GetType().GetEvent("TurboKeyPressed")?.RemoveEventHandler(watcher, handler);
                ShellLog.Write("acer hotkey",
                    "took over turbo key: detached MainViewModel.OnTurboKeyPressed (power-source handler kept)");
            }
            catch (Exception ex)
            {
                ShellLog.Write("acer hotkey", $"takeover failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// 判定本进程是否为主实例，并持有 mutex。
        ///
        /// 只信 <c>createdNew</c> 是不够的：上一个实例被强杀后，只要还有别的进程
        /// （子进程/被继承的句柄）持有同名内核对象，<c>createdNew</c> 就是 false，
        /// 但那个 mutex 其实已经**没有活着的所有者**（abandoned）。
        /// 所以再尝试 WaitOne(0)：
        ///  * 拿到（含 AbandonedMutexException）→ 我是主实例，继续；
        ///  * 拿不到 → 确实有活实例在跑，走次实例路径。
        /// </summary>
        private static bool TryClaimPrimaryInstance()
        {
            try
            {
                _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out bool createdNew);
                if (createdNew)
                {
                    _ownsMutex = true;
                    return true;
                }

                try
                {
                    if (_singleInstanceMutex.WaitOne(0))
                    {
                        _ownsMutex = true;
                        return true;
                    }
                }
                catch (AbandonedMutexException)
                {
                    // 上一个所有者已经死了，但内核对象还在：内核把所有权给了我们。
                    _ownsMutex = true;
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                // 拿不到 mutex 本体（权限/名字异常）时宁可当主实例起窗口，
                // 也不要让用户看到「双击没反应」。
                LogCrash("App.TryClaimPrimaryInstance", ex);
                return true;
            }
        }

        /// <summary>把「显示窗口」信号发给已经在跑的实例。</summary>
        private static void SignalExistingInstance()
        {
            try
            {
                using var ev = EventWaitHandle.OpenExisting(ShowEventName);
                ev.Set();
            }
            catch { }
        }

        /// <summary>
        /// 次实例的早退路径：不使用同步 Application.Exit()。
        /// 把退出投递到本线程的 DispatcherQueue，等 OnLaunched 返回、
        /// XAML 根完成初始化后再执行；DispatcherQueue 拿不到时直接结束进程。
        /// </summary>
        private void ExitWithoutTouchingXamlRoot()
        {
            try
            {
                var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                if (queue is not null &&
                    queue.TryEnqueue(() =>
                    {
                        try { Exit(); }
                        catch (Exception ex) { LogCrash("App.Exit(deferred)", ex); }
                        Environment.Exit(0);
                    }))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                LogCrash("App.ExitWithoutTouchingXamlRoot", ex);
            }

            Environment.Exit(0);
        }

        internal static void RequestExit()
        {
            _exiting = true;
            // Stop the automatic update loop before the shell goes away: a check or download
            // that is still unwinding must not outlive the process' UI (and the status window
            // must not be left behind as a stray HWND).
            try { UpdateAutoRunner.Stop(); } catch { }
            try { Views.UpdateInfoWindow.Shutdown(); } catch { }
            _acerHotkeys?.Dispose();
            _acerHotkeys = null;
            try { _showEvent?.Set(); } catch { }
            _showEvent?.Dispose();
            if (_ownsMutex) { try { _singleInstanceMutex?.ReleaseMutex(); } catch { } }
            _singleInstanceMutex?.Dispose();
            Current.Exit();
        }
    }
}
