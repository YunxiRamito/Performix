// The automatic update loop: wait out the startup burst, check, download, verify, then install
// once the machine is quiet — or report and try again on the next interval.
//
// THE ORDER IS THE FEATURE (docs\UPDATE-DESIGN.md §4.1, updated):
//   delay 45 s → check → newer? download → verify (size + SHA-256) → wait for a quiet moment
//   → announce the restart → hand over to scripts\invoke-update.ps1 (which waits for this PID
//   to exit, backs up, copies, restarts).
// The install is the only step that interrupts anything, so it is the only step that waits:
// no dialog open, no mode switch / tuning apply in flight, and 15 s of real input idle (which is
// what "the user is dragging a fan curve" looks like from the outside — see UpdateQuietWindow).
//
// EVERYTHING HERE IS BEST-EFFORT: a background check that fails must never pop an error. Failures
// go to shell.log / update.log and to the status card; the loop simply waits for the next
// interval. The one thing that is NOT silent is the install, which announces itself before it
// takes the app down.
//
// OFF MEANS OFFLINE: when the preference is off the loop never starts, and turning it off while a
// cycle is running stops the cycle (the quiet-window gate re-reads the preference every poll, and
// the status card is dismissed). Nothing else in the update path is reachable without the user
// pressing a button on the Update page.

using Microsoft.UI.Dispatching;
using Predex.WinUI.Services;
using Predex.WinUI.Views;
using PreySense.Frontend.Services;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Predex.WinUI.Services.Update
{
    /// <summary>Owns the timer, the check → download → install chain and the quiet-window gate.</summary>
    internal static class UpdateAutoRunner
    {
        /// <summary>How often the gate re-checks whether the machine is quiet.</summary>
        private static readonly TimeSpan QuietPollInterval = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Upper bound on waiting for a quiet moment. 30 min covers "the user is in a long tuning
        /// session"; after that the card says so and the package stays verified, so the next
        /// interval (or the Update page's button) finishes the job instead of nagging.
        /// </summary>
        private static readonly TimeSpan QuietWaitLimit = TimeSpan.FromMinutes(30);

        /// <summary>
        /// How long the "restarting to install" card is shown before the app goes down. The
        /// install is not a modal prompt (the brief forbids one), but it must never be a surprise
        /// either: 6 s is enough to read the card and, if the user is actually mid-task, to move
        /// the mouse and cancel the wait.
        /// </summary>
        private static readonly TimeSpan AnnounceDwell = TimeSpan.FromSeconds(6);

        private static readonly object Gate = new();
        private static CancellationTokenSource? _cancellation;
        private static Task? _loop;
        private static bool _started;

        /// <summary>True once the loop owns the process (used by tests / the log line).</summary>
        internal static bool IsRunning
        {
            get { lock (Gate) return _started && _loop is { IsCompleted: false }; }
        }

        /// <summary>
        /// Starts the loop. Idempotent. <paramref name="commandLine"/> decides the verification
        /// opt-outs; a test feed disables the loop entirely (the acceptance scripts drive the
        /// update path themselves and must never race a background check).
        /// </summary>
        internal static void Start(string[]? commandLine)
        {
            lock (Gate)
            {
                if (_started) return;
                _started = true;
            }

            if (UpdateOptions.HasFlag(commandLine, UpdatePreferences.NoAutoSwitch))
            {
                ShellLog.Write("update", "auto-update: suppressed (--update-no-auto, this run only)");
                return;
            }

            if (!UpdatePreferences.AutoUpdate)
            {
                ShellLog.Write("update", "auto-update: off (preference) — the update path stays offline");
                return;
            }

            UpdateOptions parsed = UpdateCli.OptionsFromCommandLine(commandLine);
            if (!string.IsNullOrWhiteSpace(parsed.FeedOverride))
            {
                // --update-feed is verification only and already implies "never install"; running
                // the automatic loop on top of it would make the screenshot runs non-deterministic.
                ShellLog.Write("update", "auto-update: suppressed (--update-feed test feed)");
                return;
            }

            var cancellation = new CancellationTokenSource();
            lock (Gate) _cancellation = cancellation;

            UpdateSession.AutoInstallGate = ConfirmQuietInstallAsync;

            ShellLog.Write("update", $"auto-update: enabled ({UpdateAutoSchedule.Describe()}) — check, download, verify, install when idle");
            _loop = Task.Run(() => RunAsync(cancellation.Token));
        }

        /// <summary>Stops the loop (application quit path).</summary>
        internal static void Stop()
        {
            lock (Gate)
            {
                try { _cancellation?.Cancel(); } catch (ObjectDisposedException) { }
                _cancellation?.Dispose();
                _cancellation = null;
                _started = false;
            }
            UpdateSession.AutoInstallGate = null;
        }

        private static async Task RunAsync(CancellationToken token)
        {
            DateTimeOffset startedAt = DateTimeOffset.UtcNow;
            DateTimeOffset? lastCheck = null;

            while (!token.IsCancellationRequested)
            {
                TimeSpan wait = UpdateAutoSchedule.DelayUntil(
                    DateTimeOffset.UtcNow, startedAt, lastCheck, UpdatePreferences.AutoUpdate);

                ShellLog.Write("update",
                    $"auto-update: next check in {wait.TotalSeconds:0}s " +
                    $"(last={lastCheck?.ToLocalTime():HH:mm:ss} started={startedAt.ToLocalTime():HH:mm:ss})");

                try { await Task.Delay(wait, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }

                if (token.IsCancellationRequested) return;
                if (!UpdatePreferences.AutoUpdate)
                {
                    ShellLog.Write("update", "auto-update: switched off — stopping the loop");
                    return;
                }

                try
                {
                    await RunOneCycleAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    // Silent by design: a background check that failed is not the user's problem.
                    ShellLog.Write("update",
                        $"auto-update: cycle failed ({ex.GetType().Name}: {ex.Message}) — retrying on the next interval");
                }

                lastCheck = DateTimeOffset.UtcNow;
            }
        }

        /// <summary>Check → download → verify. The install happens inside the session, behind the gate.</summary>
        private static async Task RunOneCycleAsync(CancellationToken token)
        {
            UpdateStateMachine machine = UpdateSession.Machine;
            if (!machine.CanCheck)
            {
                ShellLog.Write("update",
                    $"auto-update: skipped, state={machine.State} (a manual check or a download owns the page)");
                return;
            }

            ShellLog.Write("update", "auto-update: checking");
            UpdateStatusPresenter.ShowChecking();
            // Silent: a failed background check is logged, never turned into a page failure.
            await UpdateSession.CheckSilentlyAsync().ConfigureAwait(false);

            if (token.IsCancellationRequested) return;

            machine = UpdateSession.Machine;
            if (machine.State != UpdatePageState.Available)
            {
                ShellLog.Write("update", $"auto-update: nothing to install (state={machine.State})");
                return;
            }

            if (!machine.CanDownload)
            {
                // The minVersion gate: this build cannot be upgraded in place. Say so and leave it
                // to the page — downloading a package the publisher says is not for us is exactly
                // what the gate exists to prevent.
                ShellLog.Write("update",
                    $"auto-update: {machine.CurrentVersionText} is below the manifest's minVersion " +
                    "— manual install required, not downloading");
                return;
            }

            ShellLog.Write("update",
                $"auto-update: downloading {machine.Manifest?.Version} ({machine.Asset?.Name})");
            await UpdateSession.DownloadAsync(autoInstall: true).ConfigureAwait(false);
        }

        /// <summary>
        /// The session's "may I install now?" gate. Returns false when the user is busy (the
        /// package stays verified and the page offers the button), true once the machine has been
        /// quiet long enough and the restart has been announced.
        /// </summary>
        private static async Task<bool> ConfirmQuietInstallAsync(CancellationToken token)
        {
            if (!UpdatePreferences.AutoUpdate)
            {
                ShellLog.Write("update", "auto-update: install declined (the switch is off)");
                return false;
            }

            Stopwatch waited = Stopwatch.StartNew();
            while (!token.IsCancellationRequested)
            {
                UpdateQuietInputs inputs = CaptureQuietInputs();
                UpdateQuietDecision decision = UpdateQuietWindow.Evaluate(inputs);

                if (decision.Quiet) break;

                if (waited.Elapsed > QuietWaitLimit)
                {
                    ShellLog.Write("update",
                        $"auto-update: install postponed after {waited.Elapsed.TotalMinutes:0} min ({decision.Reason}); " +
                        "the package stays verified and the Update page can finish it");
                    UpdateStatusPresenter.ShowWaitingForUser();
                    return false;
                }

                ShellLog.Write("update",
                    $"auto-update: waiting to install ({decision.Reason}, waited {waited.Elapsed.TotalSeconds:0}s)");
                try { await Task.Delay(QuietPollInterval, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return false; }
            }

            if (token.IsCancellationRequested) return false;

            // Announce, then go. The card is the only warning the user gets — there is no modal
            // dialog by design — so it names the version and stays up for AnnounceDwell.
            string version = UpdateSession.Machine.Manifest?.Version?.ToString() ?? "?";
            ShellLog.Write("update",
                $"auto-update: quiet for {UpdateQuietWindow.RequiredIdleSeconds:0}s — announcing the restart to install {version}");
            UpdateStatusPresenter.ShowRestarting(version);

            try { await Task.Delay(AnnounceDwell, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }

            if (!UpdatePreferences.AutoUpdate)
            {
                ShellLog.Write("update", "auto-update: install cancelled at the last moment (the switch is off)");
                return false;
            }

            // Last look: if the user started typing during the announcement, the install is not
            // worth interrupting. (Deliberately only the idle check — a dialog opened in the last
            // 6 s is also a fresh input event, so the idle rule already covers it.)
            UpdateQuietDecision final = UpdateQuietWindow.Evaluate(CaptureQuietInputs());
            if (!final.Quiet)
            {
                ShellLog.Write("update",
                    $"auto-update: install cancelled after the announcement ({final.Reason})");
                UpdateStatusPresenter.ShowWaitingForUser();
                return false;
            }

            return true;
        }

        /// <summary>Reads the three signals the quiet-window decision needs, right now.</summary>
        private static UpdateQuietInputs CaptureQuietInputs()
        {
            bool dialogOpen = false;
            bool shellBusy = false;

            try
            {
                Shell.ShellWindow? shell = App.CurrentShell;
                if (shell is not null)
                {
                    dialogOpen = shell.Prompts.IsDialogOpen;
                    shellBusy = shell.ViewModel.IsBusy;
                }
            }
            catch (Exception ex)
            {
                // A shell that cannot be read is not a reason to skip the wait: treat it as busy.
                ShellLog.Write("update", $"auto-update: quiet probe failed ({ex.GetType().Name}) → treating as busy");
                shellBusy = true;
            }

            return new UpdateQuietInputs(UpdatePreferences.AutoUpdate, dialogOpen, shellBusy, IdleSeconds());
        }

        /// <summary>Seconds since the last keyboard / mouse input on this session.</summary>
        internal static double IdleSeconds()
        {
            try
            {
                var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
                if (!GetLastInputInfo(ref info)) return 0;
                uint now = (uint)Environment.TickCount;
                uint idleMs = unchecked(now - info.dwTime);
                // A wrap or a bogus reading must not read as "idle for 49 days".
                return idleMs > int.MaxValue ? 0 : idleMs / 1000.0;
            }
            catch { return 0; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LastInputInfo
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LastInputInfo plii);
    }
}
