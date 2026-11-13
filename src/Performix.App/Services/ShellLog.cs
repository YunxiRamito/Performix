// Opt-in trace log for the WinUI shell (`Predex.exe --trace`).
//
// S1 verification needs evidence that is not visible in a screenshot: whether the tray
// icon was actually registered, whether the low-level keyboard hook installed, which page
// the shell navigated to, and when auto-hide fired. Everything goes to shell.log next to
// the exe. Without --trace nothing is written (no I/O on a normal run).
//
// THREAD RULE (intermittent cold-start crash fix, 2026-11-13): every Write() call from any
// thread must be able to return without WAITING for the file gate. A blocking
// Monitor.Enter on the UI thread waits through CoWaitForMultipleHandles, i.e. it pumps COM
// messages; a dispatcher callback already queued at that moment then runs re-entrantly
// inside the XAML event handler that is still on the stack, and XAML fails fast with
// E_UNEXPECTED → 0xC000027B (faulting module Microsoft.UI.Xaml.dll, empty crash.log).
// Reproduced with a full dump: HomePage.OnLoaded → ShellLog.Write → Monitor.Enter (waited
// on the log gate held by the worker thread that logs the compat Dispatcher fallback) →
// CoWaitForMultipleHandles → nested XAML dispatch → fail fast.
// So: TryEnter with a zero timeout and drop the line instead of stalling the caller.
// A trace line is worth losing; never let logging block the UI thread.

using System;
using System.IO;
using System.Threading;

namespace Predex.WinUI.Services
{
    internal static class ShellLog
    {
        private static readonly object Gate = new();
        private static bool _enabled;
        private static int _contendedDrops;

        public static bool Enabled => _enabled;

        /// <summary>Lines dropped because another thread held the file gate.</summary>
        internal static int ContendedDrops => _contendedDrops;

        private static string LogPath => Path.Combine(AppContext.BaseDirectory, "shell.log");

        public static void Enable()
        {
            _enabled = true;
            try
            {
                File.WriteAllText(LogPath,
                    $"# Predex shell trace {DateTime.Now:yyyy-MM-dd HH:mm:ss} pid={Environment.ProcessId}\n");
            }
            catch { /* logging must never break startup */ }
        }

        public static void Write(string message)
        {
            if (!_enabled) return;
            // Zero-timeout TryEnter: never let a trace line block the calling thread
            // (see the thread rule above). Losing a line under contention is fine.
            if (!Monitor.TryEnter(Gate))
            {
                Interlocked.Increment(ref _contendedDrops);
                return;
            }
            try
            {
                File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
            }
            catch { }
            finally { Monitor.Exit(Gate); }
        }

        public static void Write(string area, string message) => Write($"{area}: {message}");
    }
}
