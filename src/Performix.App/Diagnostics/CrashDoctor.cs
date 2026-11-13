// Startup-crash forensics for the WinUI shell.
//
// Why this exists: the shell has an intermittent cold-start crash
//     0xc000027b (STATUS_STOWED_EXCEPTION) / Microsoft.UI.Xaml.dll
// and crash.log stayed empty, so the WinUI exception hooks were not producing
// evidence. This class gives every hook the same, richer record (thread id, UI
// thread flag, uptime, full ToString) and adds opt-in diagnostics that do not
// change behaviour in a normal run:
//
//   --crashdiag        log first-chance managed exceptions to crashdiag.log
//                      (deduplicated, bounded). A native fail-fast takes the
//                      process down without running the managed unhandled
//                      handlers; first chance is the only managed point that
//                      always runs before the throw reaches native code.
//   --crash-test=<t>   deliberate self-test, used to characterise what the
//                      reported crash signature actually is:
//                        asyncvoid        throw after await in an async-void
//                                         UI-dispatcher callback (the pattern
//                                         the linked ViewModel uses via
//                                         Dispatcher.InvokeAsync(async ...))
//                        dispatcherthrow  throw inside a raw DispatcherQueue
//                                         callback
//                        crossthread      write a XAML-bound ViewModel property
//                                         from a worker thread
//
// The final one or two switches never write hardware; they exist for the crash
// investigation only and are inert without the command-line argument.

using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Predex.WinUI.Diagnostics
{
    internal static class CrashDoctor
    {
        private const int MaxFirstChanceEntries = 400;
        private const int MaxStackLines = 16;

        private static readonly object Gate = new();
        private static readonly Stopwatch Up = Stopwatch.StartNew();
        private static readonly HashSet<string> SeenFirstChance = new(StringComparer.Ordinal);

        private static int _uiThreadId;
        private static bool _firstChanceEnabled;
        private static int _firstChanceUnique;
        private static int _firstChanceTotal;
        private static int _droppedRecords;

        internal static string CrashLogPath => Path.Combine(AppContext.BaseDirectory, "crash.log");
        internal static string FirstChanceLogPath => Path.Combine(AppContext.BaseDirectory, "crashdiag.log");

        /// <summary>
        /// Installs the process-wide hooks. Must be called as the first statement of the
        /// App constructor, i.e. on the UI thread, before XAML resources or the shell exist.
        /// </summary>
        internal static void Install(string[] commandLine)
        {
            _uiThreadId = Environment.CurrentManagedThreadId;
            try
            {
                _firstChanceEnabled = commandLine.Any(a =>
                    a.Equals("--crashdiag", StringComparison.OrdinalIgnoreCase));
                if (_firstChanceEnabled)
                    File.WriteAllText(FirstChanceLogPath,
                        $"# Predex first-chance trace {DateTime.Now:yyyy-MM-dd HH:mm:ss} pid={Environment.ProcessId} uiThread={_uiThreadId}\n");

                AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                    Log("AppDomain.UnhandledException",
                        $"terminating={e.IsTerminating}\n{e.ExceptionObject}");

                TaskScheduler.UnobservedTaskException += (_, e) =>
                {
                    Log("TaskScheduler.UnobservedTaskException", e.Exception);
                    // Keeping the process alive here matches what .NET 4.5+ already does:
                    // an unobserved Task exception is not a fatal condition by itself.
                    e.SetObserved();
                };

                if (_firstChanceEnabled)
                    AppDomain.CurrentDomain.FirstChanceException += (_, e) => OnFirstChance(e.Exception);
            }
            catch (Exception ex)
            {
                // Diagnostics must never be the reason a launch fails.
                try { File.AppendAllText(CrashLogPath, $"CrashDoctor.Install failed: {ex}\n\n"); } catch { }
            }
        }

        /// <summary>
        /// One crash/exception record. <paramref name="error"/> should be an Exception when
        /// possible: its ToString() carries the full stack.
        /// </summary>
        internal static void Log(string where, object? error)
        {
            try
            {
                Append(CrashLogPath, $"{Header(where)}{error}\n\n");
            }
            catch { }
        }

        private static void OnFirstChance(Exception ex)
        {
            try
            {
                Interlocked.Increment(ref _firstChanceTotal);
                string key = $"{ex.GetType().FullName}|{ex.Message}";
                // Never WAIT for the dedupe set either: this handler runs on whatever thread
                // threw, including the UI thread inside a XAML event (see Append below).
                bool unique;
                if (!Monitor.TryEnter(Gate))
                {
                    Interlocked.Increment(ref _droppedRecords);
                    return;
                }
                try { unique = SeenFirstChance.Add(key); }
                finally { Monitor.Exit(Gate); }
                if (!unique) return;

                int n = Interlocked.Increment(ref _firstChanceUnique);
                if (n > MaxFirstChanceEntries) return;

                string stack;
                try
                {
                    var lines = new StackTrace(1, fNeedFileInfo: false).ToString()
                        .Split('\n')
                        .Select(l => l.TrimEnd())
                        .Where(l => l.Length > 0)
                        .Take(MaxStackLines);
                    stack = string.Join("\n", lines);
                }
                catch (Exception stackError) { stack = $"<stack unavailable: {stackError.GetType().Name}>"; }

                Append(FirstChanceLogPath,
                    $"{Header($"first-chance #{n}")}{ex.GetType().FullName}: {ex.Message}\n{stack}\n\n");
            }
            catch { }
        }

        private static string Header(string where)
        {
            bool ui = DispatcherQueue.GetForCurrentThread() is not null;
            string name = Thread.CurrentThread.Name is { Length: > 0 } n ? $" \"{n}\"" : string.Empty;
            return $"[{DateTime.Now:HH:mm:ss.fff} +{Up.ElapsedMilliseconds} ms] {where} " +
                   $"thread={Environment.CurrentManagedThreadId}{name} uiThread={ui} " +
                   $"(uiThreadId={_uiThreadId}) shellLogDrops={Services.ShellLog.ContendedDrops}\n";
        }

        private static void Append(string path, string text)
        {
            // Same rule as ShellLog: never WAIT for a lock on any thread (a block on the UI
            // thread pumps COM messages and lets XAML re-enter an event it is still
            // dispatching). Dropping one diagnostic record beats perturbing the crash we
            // are trying to catch.
            if (!Monitor.TryEnter(Gate))
            {
                Interlocked.Increment(ref _droppedRecords);
                return;
            }
            try { File.AppendAllText(path, text); }
            catch { }
            finally { Monitor.Exit(Gate); }
        }

        // ── Deliberate failure self-tests (--crash-test=...) ────────────────────
        /// <summary>
        /// Runs one crash-shape experiment from the command line. Called once the shell
        /// exists; without the switch it returns immediately.
        /// </summary>
        internal static void MaybeRunSelfTest(string[] commandLine, Shell.ShellWindow shell)
        {
            string? test = commandLine
                .FirstOrDefault(a => a.StartsWith("--crash-test=", StringComparison.OrdinalIgnoreCase))
                is { } arg ? arg["--crash-test=".Length..].ToLowerInvariant() : null;
            if (string.IsNullOrWhiteSpace(test)) return;

            Services.ShellLog.Write("crash-test", $"armed: {test}");
            switch (test)
            {
                case "asyncvoid":
                    // The linked WPF ViewModel dispatches exactly like this:
                    //   Dispatcher.InvokeAsync(async () => { ... })
                    // which under WPF becomes an async-void callback. The shim wraps the
                    // synchronous part in try/catch, so a throw AFTER the first await escapes it.
                    _ = System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
                    {
                        await Task.Delay(150);
                        throw new InvalidOperationException(
                            "CrashDoctor self-test: throw after await inside an async-void dispatcher callback");
                    });
                    break;

                case "dispatcherthrow":
                    shell.DispatcherQueue.TryEnqueue(() =>
                        throw new InvalidOperationException(
                            "CrashDoctor self-test: throw inside a raw DispatcherQueue callback"));
                    break;

                case "crossthread":
                    _ = Task.Run(() =>
                    {
                        Thread.Sleep(500);
                        Services.ShellLog.Write("crash-test",
                            "writing ViewModel.CpuTemperatureText (bound in HomePage) from a worker thread");
                        shell.ViewModel.CpuTemperatureText = "DIAG °C";
                        Services.ShellLog.Write("crash-test", "cross-thread write returned");
                    });
                    break;

                default:
                    Services.ShellLog.Write("crash-test", $"unknown test '{test}'");
                    break;
            }
        }
    }
}
