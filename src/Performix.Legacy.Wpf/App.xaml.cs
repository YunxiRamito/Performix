using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace PreySense.Frontend
{
    public partial class App : Application
    {
        public static bool SuppressAutoHide { get; set; }

        /// <summary>UI-test switch (<c>--no-autohide</c>): keep windows visible for screenshots.</summary>
        public static bool NoAutoHide { get; private set; }

        private static void ApplyAutomationSwitches(string[] args)
        {
            if (args.Any(arg => arg.Equals("--no-autohide", StringComparison.OrdinalIgnoreCase)))
                NoAutoHide = true;
        }
        private static Mutex? _singleInstanceMutex;
        private static EventWaitHandle? _showAppEvent;
        private static Thread? _showAppThread;
        private static volatile bool _exiting;
        private static bool _ownsSingleInstanceMutex;

        private static volatile bool _applicationReady;
        private static int _runtimeFailureCount;

        public App()
        {
            // Install before InitializeComponent/OnStartup can load resources or configuration.
            DispatcherUnhandledException += (_, args) =>
            {
                if (!_applicationReady)
                {
                    // Start-up failures leave the app in an unusable state: report and exit.
                    args.Handled = true;
                    ShowStartupFailure(args.Exception);
                    Shutdown(1);
                    return;
                }

                // Runtime failures: keep the app alive, log, and surface it in the info window.
                // Three failures inside one session means state is unsafe — exit cleanly instead.
                args.Handled = true;
                PreySense.AppLogger.Error($"[Unhandled] {args.Exception}");
                int count = Interlocked.Increment(ref _runtimeFailureCount);
                if (count <= 3)
                {
                    try { Services.InfoWindowService.ShowFailure("程序遇到一个异常", args.Exception.Message); }
                    catch { /* the info window itself must never crash the handler */ }
                }
                else
                {
                    Shutdown(1);
                }
            };
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                Services.StartupDiagnostics.CreateReport(args.ExceptionObject as Exception
                    ?? new Exception(args.ExceptionObject?.ToString() ?? "Unknown application failure."));
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            // Read-only sensor self-check: never creates a window, hardware service or tray icon.
            if (Services.ProbeMode.IsProbeRequested(e.Args))
            {
                int exitCode;
                try { exitCode = Services.ProbeMode.Run(e.Args); }
                catch (Exception ex)
                {
                    try { Console.WriteLine($"probe failed: {ex}"); } catch { }
                    exitCode = 4;
                }
                Shutdown(exitCode);
                return;
            }

            try { StartApplication(e); }
            catch (Exception ex)
            {
                ShowStartupFailure(ex);
                Shutdown(1);
            }
        }

        private bool _failureReported;
        private void ShowStartupFailure(Exception exception)
        {
            if (_failureReported) return;
            _failureReported = true;
            string report = Services.StartupDiagnostics.CreateReport(exception);
            try { MessageBox.Show(report, "Performix could not start", MessageBoxButton.OK, MessageBoxImage.Error); }
            catch { /* Logging must still work when Windows cannot display a dialog. */ }
        }

        private void StartApplication(StartupEventArgs e)
        {
            base.OnStartup(e);
            ApplyAutomationSwitches(e.Args);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Deactivated += (_, _) =>
            {
                if (MainWindow is MainWindow mainWindow) mainWindow.QueueAutoHide();
            };

            // Single instance Mutex check
            _singleInstanceMutex = new Mutex(true, "PreySenseApp_Unique_System_Mutex_999", out bool createdNew);
            _ownsSingleInstanceMutex = createdNew;
            if (!createdNew)
            {
                try
                {
                    using var ev = EventWaitHandle.OpenExisting("PreySense_Show_App_Event");
                    ev.Set();
                }
                catch { }

                Shutdown(0);
                return;
            }

            Services.LocalizationService.Initialize();
            _ = Task.Run(PreySense.Overlay.WinRing0Cleanup.RemoveInstalledDriver);

            // Start cross-process show event listener
            try
            {
                _showAppEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "PreySense_Show_App_Event");
                _showAppThread = new Thread(() =>
                {
                    while (!_exiting)
                    {
                        try
                        {
                            _showAppEvent.WaitOne();
                            if (_exiting || Dispatcher.HasShutdownStarted) break;
                            Dispatcher.BeginInvoke(() =>
                            {
                                if (MainWindow is MainWindow mw)
                                {
                                    mw.ShowAndRestore();
                                }
                            });
                        }
                        catch (ObjectDisposedException) { break; }
                        catch (ThreadAbortException) { break; }
                        catch (Exception ex)
                        {
                            AppLogger.Error($"Show-app listener stopped: {ex}");
                            break;
                        }
                    }
                })
                {
                    IsBackground = true,
                    Name = "PreySense_ShowAppListener"
                };
                _showAppThread.Start();
            }
            catch { }

            bool startMinimized = e.Args.Any(arg =>
                arg.Equals("--minimized", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-minimized", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-hidden", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--hidden", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("/minimized", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("/hidden", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--tray", StringComparison.OrdinalIgnoreCase));

            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            // Hidden launches must initialize without waiting for WPF Loaded.
            ((ViewModels.MainViewModel)mainWindow.DataContext).NotifyApplicationReady();
            Services.OverlayBridge.StartIfNeeded();

            _ = Services.UpdateCheckService.CheckForUpdatesAsync();

            if (startMinimized)
            {
                // Ensure HWND/tray exist without a taskbar button; stay tray-only until opened.
                mainWindow.ShowInTaskbar = false;
                mainWindow.WindowState = WindowState.Normal;
                mainWindow.Hide();
            }
            else
            {
                mainWindow.ShowAndRestore();
            }

            // From here on the shell is usable: runtime exceptions are recoverable, not fatal.
            _applicationReady = true;

            // UI-test hook: show the bottom-right info window with a sample result.
            if (e.Args.Any(arg => arg.Equals("--info-demo", StringComparison.OrdinalIgnoreCase)))
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    Services.InfoWindowService.ShowSuccess("风扇设置已应用", "将跟随当前性能模式生效");
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);

                // 3.5 秒后换成「进行中」态，方便一次截图看到两种形态
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    Services.InfoWindowService.ShowWorking("正在切换性能模式…", "固件读回校验中", progress: 40);
                };
                timer.Start();
            }

            // UI-test hook: open the Tuning window immediately (used for screenshot acceptance).
            if (e.Args.Any(arg => arg.Equals("--tuning", StringComparison.OrdinalIgnoreCase) ||
                                  arg.Equals("--settings", StringComparison.OrdinalIgnoreCase)))
            {
                Window toolWindow = e.Args.Any(arg => arg.Equals("--settings", StringComparison.OrdinalIgnoreCase))
                    ? new Views.SettingsWindow { DataContext = mainWindow.DataContext, Owner = mainWindow }
                    : new Views.ModeSettingsWindow { DataContext = mainWindow.DataContext, Owner = mainWindow };
                toolWindow.Show();
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _exiting = true;
            try { _showAppEvent?.Set(); } catch (ObjectDisposedException) { }
            try
            {
                if (MainWindow?.DataContext is ViewModels.MainViewModel vm)
                    vm.PersistSessionSettings();
                Views.ModeToastWindow.Shutdown();
            }
            catch (Exception ex) { AppLogger.Error($"Application exit cleanup failed: {ex}"); }
            finally
            {
                _showAppEvent?.Dispose();
                if (_ownsSingleInstanceMutex) _singleInstanceMutex?.ReleaseMutex();
                _singleInstanceMutex?.Dispose();
            }

            base.OnExit(e);
        }
    }
}
