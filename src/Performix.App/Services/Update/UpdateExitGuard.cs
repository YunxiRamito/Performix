// Fallback for the last step of an install: the replacement script waits for this process to
// disappear before it can swap Performix.exe.
//
// The normal path asks the shell to quit and never reaches the code after the await (the process
// is gone). But the quit request can fail to do anything:
//   • App.CurrentShell is null (the session outlived the shell window), or
//   • ShellWindow.QuitApplication returns early because _isExplicitExit is already set, or
//   • the UI thread is stuck in a shutdown path and never gets to close the window.
// In those cases the page would stay in ReadyToInstall (a busy state, so the check button is
// disabled) and the script would wait for a PID that never goes away: a deadlock with no way out
// for the user. So after a grace period the process ends itself and says why in update.log.
//
// Kept in its own file with an injected "still running" probe so the fallback itself is testable
// without killing the test host.
//
// THE PROBE IS NOT "the process exists" — that is always true inside this process, and using it
// would force the exit even when the graceful shutdown simply needs a few more seconds. The real
// question at the deadline is "has anything started closing, or is the UI thread stuck?". A
// shutdown that has begun (App.RequestExit ran, or QuitApplication set its flag) is left alone no
// matter how slow it is; a live shell window with no exit request is the hang this fallback exists
// for. <see cref="ShouldForceExit"/> is that decision, as a pure function; the App supplies the two
// facts through UpdateShutdownProbe.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Predex.WinUI.Services.Update
{
    internal static class UpdateExitGuard
    {
        /// <summary>How long the graceful exit gets before the process is forced down.</summary>
        public static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Waits <paramref name="grace"/>, then forces the exit when the process is still running.
        /// Returns true when it had to force. Never throws for a probe that reports "already gone".
        /// </summary>
        public static async Task<bool> ForceExitIfStillRunningAsync(TimeSpan grace, Func<bool> stillRunning,
            Action forceExit, CancellationToken cancellationToken = default)
        {
            if (stillRunning is null) throw new ArgumentNullException(nameof(stillRunning));
            if (forceExit is null) throw new ArgumentNullException(nameof(forceExit));

            if (grace > TimeSpan.Zero)
                await Task.Delay(grace, cancellationToken).ConfigureAwait(false);

            if (!stillRunning()) return false;

            forceExit();
            return true;
        }

        /// <summary>
        /// The install-path decision at the deadline: force only when the exit was never even
        /// requested and the shell is still there. Pure and unit-tested.
        /// </summary>
        /// <param name="exitRequested">
        /// App.RequestExit has run: a shutdown is under way, however slowly it finishes.
        /// </param>
        /// <param name="shellExitStarted">
        /// ShellWindow.QuitApplication set its explicit-exit flag; null when there is no shell at
        /// all (it outlived the window or the process has no shell), which is precisely the case
        /// where nothing will ever close it.
        /// </param>
        public static bool ShouldForceExit(bool exitRequested, bool? shellExitStarted)
        {
            if (exitRequested) return false;           // the graceful path ran; let it finish
            if (shellExitStarted is null) return true;  // no shell left to do the closing
            return !shellExitStarted.Value;             // shell alive but never asked to quit
        }

        /// <summary>
        /// Ends the process after the install request, so the waiting script can proceed. Logged
        /// first: after this call nothing else runs.
        /// </summary>
        public static void ForceProcessExit()
        {
            ShellLog.Write("update",
                $"process {Environment.ProcessId} is still alive {DefaultGrace.TotalSeconds:0} s after the " +
                "exit request; forcing it down so the replacement script is not left waiting");
            try
            {
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                ShellLog.Write("update", "forced exit failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
