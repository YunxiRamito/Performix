// Idle-time memory trimmer.
// When the popup has been hidden for IdleTrimDelay, aggressively collects
// garbage and releases the working set back to the OS.
// This keeps Predex's RAM footprint near ~25–35 MB while in the tray.

using Microsoft.UI.Dispatching;
using System;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace Predex.WinUI.Services
{
    public sealed class MemoryManager : IDisposable
    {
        // How long after the window hides before we trim memory.
        private static readonly TimeSpan IdleTrimDelay = TimeSpan.FromSeconds(8);

        [DllImport("kernel32.dll")]
        private static extern bool SetProcessWorkingSetSize(
            IntPtr hProcess, nint minWorkingSet, nint maxWorkingSet);

        private readonly DispatcherQueueTimer _timer;
        private DateTime _hiddenAt = DateTime.MinValue;
        private bool _pendingTrim;
        private bool _disposed;

        public MemoryManager(DispatcherQueue queue)
        {
            _timer = queue.CreateTimer();
            _timer.Interval = TimeSpan.FromSeconds(4);
            _timer.IsRepeating = true;
            _timer.Tick += OnTimerTick;
        }

        // Call when the popup window becomes hidden.
        public void OnWindowHidden()
        {
            _hiddenAt = DateTime.UtcNow;
            _pendingTrim = true;
            _timer.Start();
        }

        // Call when the popup window becomes visible.
        public void OnWindowShown()
        {
            _pendingTrim = false;
            _timer.Stop();
        }

        private void OnTimerTick(DispatcherQueueTimer _, object __)
        {
            if (!_pendingTrim) { _timer.Stop(); return; }
            if (DateTime.UtcNow - _hiddenAt < IdleTrimDelay) return;

            _pendingTrim = false;
            _timer.Stop();
            TrimNow();
        }

        // Can be called manually from tests or diagnostics.
        public static void TrimNow()
        {
            // Full generational GC pass — compaction + LOH compaction.
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

            // Release unused pages back to the OS — same trick used by Chrome/Edge.
            SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, -1, -1);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Stop();
        }
    }
}
