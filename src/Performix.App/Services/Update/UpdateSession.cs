// Process-wide update session: owns the one UpdateClient and the one UpdateStateMachine, runs
// the check/download in the background and lets any page (re)attach by subscribing to Changed.
//
// WHY A SESSION AND NOT PAGE STATE: the shell destroys the whole page tree when the window
// hides to the tray (S6-REPORT §3), and it hides automatically when it loses focus. An update
// download owned by the page would therefore die the moment the user clicked anything else.
// The session outlives the page; the page re-renders the current machine state on Loaded.
//
// Threading: the work runs on the thread pool; Changed is raised on whatever thread finished
// the step, and the page marshals it to its DispatcherQueue. No UI types are touched here.

using PreySense.Frontend.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Predex.WinUI.Services.Update
{
    /// <summary>Minimal IProgress that forwards to an action (Progress&lt;T&gt; needs a sync context).</summary>
    internal sealed class ActionProgress<T> : IProgress<T>
    {
        private readonly Action<T> _action;

        public ActionProgress(Action<T> action) => _action = action;

        public void Report(T value)
        {
            try { _action(value); } catch { /* progress reporting must never break a transfer */ }
        }
    }

    internal static class UpdateSession
    {
        private static readonly object Gate = new();
        private static UpdateClient? _client;
        private static UpdateStateMachine? _machine;
        private static CancellationTokenSource? _cancellation;
        private static bool _downloadRunning;
        private static bool _installSuppressed;
        private static bool _initialized;

        /// <summary>Raised after any state change; handlers must marshal to the UI thread.</summary>
        public static event EventHandler? Changed;

        public static bool IsInitialized => _initialized;

        public static UpdateStateMachine Machine
        {
            get
            {
                lock (Gate)
                {
                    _machine ??= new UpdateStateMachine(UpdateOptions.DefaultChannel, UpdateCheckService.InformationalVersion);
                    return _machine;
                }
            }
        }

        public static UpdateOptions Options { get; private set; } = new();

        /// <summary>
        /// Builds the client and the machine. Called once from App.OnLaunched; safe to call
        /// again with the same arguments (the verification script relaunches the exe per run).
        /// </summary>
        public static void Initialize(UpdateOptions options, bool suppressInstall)
        {
            if (options is null) throw new ArgumentNullException(nameof(options));

            UpdateOptions effective = new()
            {
                Channel = options.Channel,
                FeedOverride = options.FeedOverride,
                ProductVersion = string.IsNullOrWhiteSpace(options.ProductVersion)
                    ? UpdateCheckService.InformationalVersion
                    : options.ProductVersion,
                GroupBudget = options.GroupBudget,
                TotalBudget = options.TotalBudget,
                StallTimeout = options.StallTimeout,
                MaxRetries = options.MaxRetries,
                TempRoot = options.TempRoot,
            };

            lock (Gate)
            {
                _client?.Dispose();
                _client = new UpdateClient(effective);
                _machine = new UpdateStateMachine(effective.Channel, UpdateCheckService.InformationalVersion);
                Options = effective;
                _installSuppressed = suppressInstall;
                _initialized = true;
            }

            ShellLog.Write("update",
                $"session ready ({effective}) installSuppressed={suppressInstall} temp={effective.ResolveTempRoot()}");
        }

        /// <summary>
        /// Runs one full check. Silent when the session is not initialized.
        ///
        /// ONE CHECK AT A TIME: BeginCheck runs inside the session gate, so two callers can never
        /// both start a check (they would share one CancellationTokenSource and the later answer
        /// would overwrite the newer one). BeginCheck also refuses Checking / Downloading /
        /// Verifying / ReadyToInstall, so a check cannot discard a verified package.
        /// </summary>
        public static async Task CheckAsync()
        {
            UpdateClient? client;
            UpdateStateMachine machine;
            CancellationTokenSource cancellation;
            lock (Gate)
            {
                if (_client is null) { ShellLog.Write("update", "check skipped: session not initialized"); return; }
                client = _client;
                machine = Machine;
                if (!machine.BeginCheck())
                {
                    ShellLog.Write("update",
                        $"check skipped: state={machine.State} stage={machine.FailureStage} " +
                        "(a check needs Idle, UpToDate or Failed)");
                    return;
                }
                cancellation = ReplaceCancellationLocked();
            }

            RaiseChanged();

            try
            {
                UpdateCheckResult result = await client.CheckAsync(machine.Channel, cancellation.Token).ConfigureAwait(false);

                if (machine.State != UpdatePageState.Checking)
                {
                    // The page moved on while this check was in flight (download, verification or
                    // ReadyToInstall). Its answer is stale and must not touch the machine.
                    ShellLog.Write("update",
                        $"check result discarded: state={machine.State} (the result no longer owns the page)");
                    return;
                }

                machine.ApplyCheck(result);

                ShellLog.Write("update",
                    $"check: hasRelease={result.HasRelease} noRelease={result.NoRelease} " +
                    $"version={result.Manifest?.Version} source={result.Source?.Id} " +
                    $"newer={machine.IsNewer} state={machine.State} " +
                    $"manualInstallRequired={machine.RequiresManualInstall} " +
                    $"{result.ElapsedMilliseconds}ms [{result.DescribeAttempts()}]");

                if (machine.RequiresManualInstall)
                {
                    ShellLog.Write("update",
                        $"minVersion gate: running {machine.CurrentVersionText} is below the manifest's " +
                        $"{machine.Manifest?.MinVersion}; the in-place download is refused. " +
                        machine.DownloadBlockReason);
                }

                foreach (UpdateAssetRejection rejection in result.RejectedAssets)
                    ShellLog.Write("update", $"asset rejected: {rejection.Name}: {rejection.Reason}");
            }
            catch (OperationCanceledException)
            {
                if (machine.State == UpdatePageState.Checking)
                    machine.Fail(UpdateFailureStage.Check, "The update check was cancelled.");
                ShellLog.Write("update", "check cancelled");
            }
            catch (Exception ex)
            {
                if (machine.State == UpdatePageState.Checking)
                    machine.Fail(UpdateFailureStage.Check, "The update check failed.", ex.GetType().Name + ": " + ex.Message);
                ShellLog.Write("update", $"check failed: {ex}");
            }
            finally
            {
                RaiseChanged();
            }
        }

        /// <summary>
        /// "重试" on a failed card: a failed check re-runs the check, a failed download /
        /// verification / install goes back to Available (through the state machine) and starts
        /// the transfer again. The page never decides this for itself.
        /// </summary>
        public static Task RetryAsync()
        {
            UpdateStateMachine machine = Machine;
            return machine.State == UpdatePageState.Failed && machine.CanRetry
                ? DownloadAsync()
                : CheckAsync();
        }

        /// <summary>
        /// Downloads, verifies, then starts the replacement script (unless the test feed
        /// suppressed installing). <paramref name="rotateSource"/> = the "换源重试" button.
        ///
        /// ONE TRANSFER AT A TIME: the rotation button must never start a second download next to
        /// the one that is still unwinding — two writers on the same .part file corrupt it, and
        /// replacing the CancellationTokenSource would leave the old transfer uncancellable. A
        /// rotation requested while an operation is running cancels that operation (the existing
        /// CTS, kept as the only one) and returns; the next press starts the new attempt.
        ///
        /// STATE GATE: every entry, rotated source included, passes the machine's one download
        /// entry (BeginDownload) inside the session gate, so a check can never be started between
        /// the gate and the transfer. A failed card is first migrated Failed → Available by that
        /// entry; a state that cannot download (Idle, Checking, a failed check, the minVersion
        /// gate) is logged and refused instead of silently transferring in the background. The
        /// rotation path therefore really is in Downloading — the cancel button works there.
        /// </summary>
        public static async Task DownloadAsync(bool rotateSource = false)
        {
            UpdateClient client;
            UpdateStateMachine machine;
            UpdateAsset? asset;
            UpdateSource? source;
            CancellationTokenSource cancellation;

            lock (Gate)
            {
                if (_client is null) { ShellLog.Write("update", "download skipped: session not initialized"); return; }
                client = _client;
                machine = Machine;
                asset = machine.Asset;
                source = machine.Source;

                if (_downloadRunning)
                {
                    if (!rotateSource)
                    {
                        ShellLog.Write("update", "download skipped: another download is already running");
                        return;
                    }

                    ShellLog.Write("update",
                        "source rotation requested while an operation is running; cancelling it first " +
                        "instead of starting a second concurrent download");
                    try { _cancellation?.Cancel(); } catch (ObjectDisposedException) { }
                    return;
                }

                if (!machine.BeginDownload(rotateSource))
                {
                    ShellLog.Write("update",
                        $"download skipped: state={machine.State} stage={machine.FailureStage} " +
                        $"rotated={rotateSource} (nothing downloadable on this card)");
                    return;
                }

                asset ??= machine.Asset;
                _downloadRunning = true;
                cancellation = ReplaceCancellationLocked();
            }

            RaiseChanged();

            try
            {
                await RunDownloadAsync(client, machine, asset, source, cancellation, rotateSource).ConfigureAwait(false);
            }
            finally
            {
                lock (Gate) _downloadRunning = false;
            }
        }

        private static async Task RunDownloadAsync(UpdateClient client, UpdateStateMachine machine,
            UpdateAsset? asset, UpdateSource? source, CancellationTokenSource cancellation, bool rotateSource)
        {
            if (asset is null)
            {
                machine.Fail(UpdateFailureStage.Download, "The update manifest carries no downloadable asset.");
                RaiseChanged();
                return;
            }
            RaiseChanged();

            var progress = new ActionProgress<UpdateDownloadProgress>(sample =>
            {
                machine.ApplyProgress(sample);
                RaiseChanged();
            });

            UpdateDownloadResult download;
            try
            {
                ShellLog.Write("update",
                    $"download start: {asset.Name} {asset.SizeBytes} bytes from {source?.Id ?? "?"} (rotated={rotateSource})");
                download = await client.DownloadAsync(asset, source, progress, cancellation.Token, rotateSource)
                    .ConfigureAwait(false);
                ShellLog.Write("update", $"download complete: {download.Bytes} bytes via {download.Url}");
            }
            catch (OperationCanceledException)
            {
                machine.CancelDownload();
                ShellLog.Write("update", "download cancelled (partial file kept for a later resume)");
                RaiseChanged();
                return;
            }
            catch (Exception ex)
            {
                machine.Fail(UpdateFailureStage.Download, ex.Message);
                ShellLog.Write("update", $"download failed: {ex.GetType().Name}: {ex.Message}");
                RaiseChanged();
                return;
            }
            machine.BeginVerify();
            RaiseChanged();

            try
            {
                UpdateVerifyOutcome outcome = await UpdateClient.VerifyAsync(download.FilePath, asset, cancellation.Token)
                    .ConfigureAwait(false);

                if (!outcome.Ok)
                {
                    // A package the verifier refused (size or SHA-256) is not resumable: drop it
                    // and its .part/.part.meta siblings so the next attempt starts at byte 0.
                    UpdateClient.DiscardDownload(download.FilePath);
                    machine.Fail(UpdateFailureStage.Verify, outcome.Reason ?? "Verification failed.", outcome.Detail);
                    ShellLog.Write("update", $"verification failed: {outcome.Reason} ({outcome.Detail})");
                    RaiseChanged();
                    return;
                }

                machine.MarkReadyToInstall();
                RaiseChanged();
                ShellLog.Write("update", $"verified: sha256={outcome.Sha256} file={download.FilePath}");
            }
            catch (OperationCanceledException)
            {
                machine.Fail(UpdateFailureStage.Verify, "Verification was cancelled.");
                RaiseChanged();
                return;
            }
            catch (Exception ex)
            {
                machine.Fail(UpdateFailureStage.Verify, "Verification failed: " + ex.Message);
                ShellLog.Write("update", $"verification threw: {ex}");
                RaiseChanged();
                return;
            }

            if (_installSuppressed)
            {
                // Verification runs (and the four screenshots are taken) against fake packages:
                // never touch the real installation from a test feed.
                ShellLog.Write("update", "install suppressed (test feed): stopping at ReadyToInstall");
                return;
            }

            UpdateInstallStart start = UpdateInstaller.Start(download.FilePath, asset, machine);
            if (!start.Ok)
            {
                machine.Fail(UpdateFailureStage.Install, start.Reason ?? "The replacement script could not be started.",
                    start.Detail);
                ShellLog.Write("update", $"install failed: {start.Reason} ({start.Detail})");
                RaiseChanged();
                return;
            }

            ShellLog.Write("update", $"replacement script started (pid {Environment.ProcessId}); exiting for the swap");
            App.CurrentShell?.QuitApplication();

            // The script is waiting for this PID to disappear. QuitApplication can fail to end the
            // process (no shell window, or _isExplicitExit already set): without this fallback the
            // page would sit in ReadyToInstall forever and the script would wait for a ghost.
            // Reaching the line after the await at all means the graceful exit did not finish in
            // time, but "did not finish YET" is not the same as "stuck": the probe distinguishes a
            // shutdown that is merely slow from an exit request that never ran (see
            // UpdateExitGuard.ShouldForceExit / UpdateShutdownProbe.IsStuck).
            await UpdateExitGuard.ForceExitIfStillRunningAsync(
                UpdateExitGuard.DefaultGrace,
                UpdateShutdownProbe.IsStuck,
                UpdateExitGuard.ForceProcessExit,
                CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>Cancels the running operation (the download); the check result is kept.</summary>
        public static void Cancel()
        {
            lock (Gate)
            {
                try { _cancellation?.Cancel(); } catch (ObjectDisposedException) { }
            }
            ShellLog.Write("update", "cancel requested");
        }

        private static CancellationTokenSource ReplaceCancellationLocked()
        {
            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();
            return _cancellation;
        }

        private static void RaiseChanged()
        {
            try { Changed?.Invoke(null, EventArgs.Empty); } catch { /* a renderer must not break the update */ }
        }
    }
}
