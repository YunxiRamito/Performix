// Update page state machine, kept out of the page so it can be unit-tested and so the page
// stays a renderer.
//
// The eight states the brief asks for:
//
//   Idle            未检查       nothing happened yet
//   Checking        检查中       source groups being queried
//   UpToDate        已是最新     a complete manifest, but not newer than the running build
//   Available       有新版       a complete manifest with a newer version
//   Downloading     下载中       transfer in flight (cancelable)
//   Verifying       校验中       size + SHA-256 being checked
//   ReadyToInstall  准备安装     verified; the replacement script is (or will be) started
//   Failed          失败         check / download / verification / install failed, with a reason
//
// INVARIANTS (pinned by tests):
//   • Available requires a manifest, an asset that passed the size+sha gate and a newer version.
//   • Available is NOT the same as installable: when the manifest declares a minVersion above the
//     running build, the release cannot be applied in place. The machine still reports the newer
//     version (that is a fact), but CanDownload is false and RequiresManualInstall / the block
//     reason explain why, so no script ever copies that package over an incompatible install.
//   • Failed always carries a stage and a reason; the page never shows a bare "failed".
//   • An unreadable running version is Fail(Check) — never "up to date" (that would be a lie).
//   • A check may only start from Idle / UpToDate / Failed (CanCheck), and only the check that
//     owns the Checking state may apply its result: a late check must never overwrite a
//     download that started meanwhile, and never discard a verified ReadyToInstall package.
//   • Cancelling a download keeps the check result, so "下载并安装" is one click away again.
//   • A failed download / verify / install is retried only through TryRetry (Failed → Available);
//     BeginDownload refuses every state that is not downloadable, with or without source rotation.
//   • Progress is passed through untouched: no percentage is invented when the total is unknown.
//
// The machine is UI-agnostic on purpose: it holds numbers and state names, the page turns them
// into localized sentences (that is where Loc.T lives).

using System;

namespace Predex.WinUI.Services.Update
{
    public enum UpdatePageState
    {
        Idle = 0,
        Checking = 1,
        UpToDate = 2,
        Available = 3,
        Downloading = 4,
        Verifying = 5,
        ReadyToInstall = 6,
        Failed = 7,
    }

    public enum UpdateFailureStage
    {
        None = 0,
        Check = 1,
        Download = 2,
        Verify = 3,
        Install = 4,
    }

    public sealed class UpdateStateMachine
    {
        public UpdateStateMachine(string channel, string currentVersionText)
        {
            Channel = string.IsNullOrWhiteSpace(channel) ? UpdateOptions.DefaultChannel : channel.Trim().ToLowerInvariant();
            CurrentVersionText = currentVersionText ?? string.Empty;
            CurrentVersion = SemanticVersion.Parse(CurrentVersionText);
        }

        /// <summary>Channel this machine checks ("alpha"); never mixed with the manifest's own.</summary>
        public string Channel { get; }

        /// <summary>As reported by the build ("0.1.0-alpha.1"); shown on the page.</summary>
        public string CurrentVersionText { get; }

        /// <summary>Parsed running version; null when the assembly metadata is unreadable.</summary>
        public SemanticVersion? CurrentVersion { get; }

        public UpdatePageState State { get; private set; } = UpdatePageState.Idle;

        /// <summary>The last completed check (kept after a cancel so the version stays visible).</summary>
        public UpdateCheckResult? Check { get; private set; }

        public UpdateManifest? Manifest => Check?.Manifest;

        public UpdateAsset? Asset => Check?.Asset;

        public UpdateSource? Source => Check?.Source;

        /// <summary>True when the last check found a strictly newer version.</summary>
        public bool IsNewer { get; private set; }

        /// <summary>True when no source has published anything yet (not a failure in the scary sense).</summary>
        public bool IsNoRelease { get; private set; }

        public UpdateDownloadProgress Progress { get; private set; } = UpdateDownloadProgress.Empty;

        /// <summary>True when the last successful transfer had to change download address.</summary>
        public bool LastDownloadRotatedSource { get; private set; }

        public UpdateFailureStage FailureStage { get; private set; } = UpdateFailureStage.None;

        /// <summary>English source sentence for the failure (Loc.T translates the common ones).</summary>
        public string? FailureReason { get; private set; }

        /// <summary>Extra technical detail for the log / tooltip ("expected …, got …").</summary>
        public string? FailureDetail { get; private set; }

        // ── Derived flags the page binds to ──────────────────────────────────────────────

        public bool IsBusy => State is UpdatePageState.Checking or UpdatePageState.Downloading
            or UpdatePageState.Verifying or UpdatePageState.ReadyToInstall;

        public bool IsProgressVisible => State is UpdatePageState.Downloading or UpdatePageState.Verifying;

        public bool HasUpdate => State == UpdatePageState.Available;

        public bool HasFailure => State == UpdatePageState.Failed;

        /// <summary>
        /// "下载并安装" enabled only with a complete, newer, gated asset AND a manifest whose
        /// minVersion the running build satisfies (see <see cref="BelowMinimumVersion"/>).
        /// A too-old build would be replaced by a package it cannot migrate from, so the button
        /// stays inert and the user is pointed at a manual install instead.
        /// </summary>
        public bool CanDownload => State == UpdatePageState.Available && Asset is not null && IsNewer &&
                                   !BelowMinimumVersion;

        /// <summary>
        /// True when a failed download / verification / install can be started again: the check
        /// result and its gated asset survived the failure, so the machine only has to go back to
        /// Available. A failed CHECK has nothing to transfer; it needs a new check instead.
        /// </summary>
        public bool CanRetry => State == UpdatePageState.Failed &&
                                (FailureStage is UpdateFailureStage.Download
                                    or UpdateFailureStage.Verify
                                    or UpdateFailureStage.Install) &&
                                Check is not null && Asset is not null && IsNewer && !BelowMinimumVersion;

        /// <summary>The cancel button is only meaningful mid-transfer.</summary>
        public bool CanCancel => State == UpdatePageState.Downloading;

        /// <summary>Check button: idle / up-to-date / failed, and never while busy.</summary>
        public bool CanCheck => State is UpdatePageState.Idle or UpdatePageState.UpToDate or UpdatePageState.Failed;

        /// <summary>0..100 or -1 (unknown total) — passed through, never synthesized.</summary>
        public double Percent => Progress.Percent;

        public bool IsIndeterminate => State == UpdatePageState.Downloading && !Progress.IsTotalKnown;

        /// <summary>True when the manifest requires a newer build than the running one.</summary>
        public bool BelowMinimumVersion =>
            Manifest?.MinVersion is not null && CurrentVersion is not null &&
            SemanticVersion.Compare(CurrentVersion, Manifest.MinVersion) < 0;

        /// <summary>
        /// True when a newer release exists but this build is below its minVersion. That is the
        /// publisher saying "this package was not built to upgrade from your version": the
        /// in-place download/install path must stay closed (<see cref="CanDownload"/> is false)
        /// and the user has to install a full package manually.
        /// </summary>
        public bool RequiresManualInstall => IsNewer && BelowMinimumVersion;

        /// <summary>
        /// The version the user would have to reach first, or null when no gate applies.
        /// </summary>
        public SemanticVersion? RequiredMinimumVersion => RequiresManualInstall ? Manifest?.MinVersion : null;

        /// <summary>
        /// English sentence (one key, zh-CN row in LocalizationService) explaining the block;
        /// {0} is the version from <see cref="RequiredMinimumVersion"/>. Null when not blocked.
        /// </summary>
        public string? DownloadBlockReason
            => RequiredMinimumVersion is { } minimum
                ? string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    ManualInstallRequiredReason, minimum)
                : null;

        /// <summary>Source sentence for <see cref="DownloadBlockReason"/>; translate/format it.</summary>
        public const string ManualInstallRequiredReason =
            "This update needs version {0} or newer. Install the full package manually instead of updating in place.";

        // ── Transitions ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Idle / UpToDate / Failed → Checking. Refused while a check, download, verification or
        /// install is in progress (see <see cref="CanCheck"/>): a second check would share this
        /// machine, overwrite a verified ReadyToInstall package with a stale answer and, when the
        /// feed is flaky, turn a "download failed" card into a "check failed" one.
        /// </summary>
        public bool BeginCheck()
        {
            if (!CanCheck) return false;
            State = UpdatePageState.Checking;
            FailureStage = UpdateFailureStage.None;
            FailureReason = null;
            FailureDetail = null;
            IsNoRelease = false;
            return true;
        }

        /// <summary>
        /// Applies a check result: newer → Available, complete-but-older → UpToDate.
        ///
        /// ONLY the check that owns the Checking state may apply. A result that arrives after the
        /// page has moved on (a download got going, or the package is already verified and ready)
        /// is stale and is dropped — otherwise a slow check started minutes ago would replace a
        /// verified package with "up to date", or overwrite a download failure with a check
        /// failure, and the install would never happen. Returns false when the result was stale.
        /// </summary>
        public bool ApplyCheck(UpdateCheckResult result)
        {
            if (result is null) throw new ArgumentNullException(nameof(result));
            if (State != UpdatePageState.Checking) return false;

            Check = result;
            FailureDetail = null;
            Progress = UpdateDownloadProgress.Empty;
            IsNoRelease = false;

            if (!result.HasRelease)
            {
                IsNewer = false;
                State = UpdatePageState.Failed;
                FailureStage = UpdateFailureStage.Check;
                FailureReason = result.FailureReason ?? "The update check failed.";
                IsNoRelease = result.NoRelease;
                return true;
            }

            if (CurrentVersion is null)
            {
                // Never claim "up to date" when we could not read what we are. That is the one
                // lie that hides every real update problem.
                IsNewer = false;
                State = UpdatePageState.Failed;
                FailureStage = UpdateFailureStage.Check;
                FailureReason = "The running version could not be read, so no update decision was made.";
                return true;
            }

            IsNewer = SemanticVersion.Compare(result.Manifest!.Version, CurrentVersion) > 0;
            State = IsNewer ? UpdatePageState.Available : UpdatePageState.UpToDate;
            FailureStage = UpdateFailureStage.None;
            FailureReason = null;
            return true;
        }

        /// <summary>
        /// Failed → Available, through the one entry both "重试" and "换源重试" use, so a retry
        /// can never start a transfer while the card still says Failed (that was the bug: the
        /// page showed no progress and no cancel button, yet the download ran and the installer
        /// could replace the program behind it). The check result and its gated asset are kept;
        /// only the failure text is cleared. Returns false when there is nothing to retry.
        /// </summary>
        public bool TryRetry()
        {
            if (!CanRetry) return false;
            State = UpdatePageState.Available;
            Progress = UpdateDownloadProgress.Empty;
            FailureStage = UpdateFailureStage.None;
            FailureReason = null;
            FailureDetail = null;
            return true;
        }

        /// <summary>
        /// The only way into Downloading. A failed card is migrated through <see cref="TryRetry"/>
        /// first — for the plain retry AND the rotated-source retry — and anything else that is
        /// not <see cref="CanDownload"/> is refused. Returns false when no transfer may start.
        /// </summary>
        public bool BeginDownload(bool rotatedSource = false)
        {
            if (State == UpdatePageState.Failed && !TryRetry()) return false;
            if (!CanDownload) return false;
            LastDownloadRotatedSource = rotatedSource;
            State = UpdatePageState.Downloading;
            Progress = UpdateDownloadProgress.Empty;
            FailureStage = UpdateFailureStage.None;
            FailureReason = null;
            FailureDetail = null;
            return true;
        }

        public void ApplyProgress(UpdateDownloadProgress progress)
        {
            if (State != UpdatePageState.Downloading) return;
            Progress = progress;
        }

        public void BeginVerify()
        {
            if (State != UpdatePageState.Downloading) return;
            State = UpdatePageState.Verifying;
            Progress = Progress with { BytesPerSecond = 0, Remaining = null };
        }

        public void MarkReadyToInstall()
        {
            if (State != UpdatePageState.Verifying) return;
            State = UpdatePageState.ReadyToInstall;
            Progress = Progress with { BytesPerSecond = 0, Remaining = TimeSpan.Zero };
        }

        public void Fail(UpdateFailureStage stage, string reason, string? detail = null)
        {
            State = UpdatePageState.Failed;
            FailureStage = stage;
            FailureReason = reason;
            FailureDetail = detail;
            Progress = UpdateDownloadProgress.Empty;
        }

        /// <summary>
        /// Cancel: drops back to the version card (the check result is kept), so the user can
        /// press "下载并安装" again without re-checking.
        /// </summary>
        public void CancelDownload()
        {
            if (State != UpdatePageState.Downloading) return;
            Progress = UpdateDownloadProgress.Empty;
            FailureStage = UpdateFailureStage.None;
            FailureReason = null;
            FailureDetail = null;
            State = IsNewer && Asset is not null ? UpdatePageState.Available : UpdatePageState.Idle;
        }

        /// <summary>Back to 未检查 (used by the verification preview switch).</summary>
        public void Reset()
        {
            State = UpdatePageState.Idle;
            Check = null;
            IsNewer = false;
            IsNoRelease = false;
            Progress = UpdateDownloadProgress.Empty;
            FailureStage = UpdateFailureStage.None;
            FailureReason = null;
            FailureDetail = null;
        }

        public override string ToString()
            => $"{State} channel={Channel} current={CurrentVersionText} newer={IsNewer} " +
               (FailureReason is null ? string.Empty : $"reason={FailureReason}");
    }

    /// <summary>Number formatting shared by the page and the console switch (unit-tested).</summary>
    public static class UpdateText
    {
        private static readonly string[] Units = { "B", "KB", "MB", "GB" };

        /// <summary>"0 B", "820 KB", "12.4 MB", "1.02 GB" — 1024-based, one decimal above KB.</summary>
        public static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";

            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < Units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            // The displayed value rounds independently of the unit choice above: 1023.95 KB
            // prints "1024 KB" and 1023.7 MB prints "1024 MB". Carry one unit up instead of
            // showing a number that is not a valid value in its own unit. The rounding here
            // mirrors the precision FormatValue will use (no decimal from 100 up, one below).
            while (unit < Units.Length - 1 &&
                   RoundForDisplay(value) >= 1024)
            {
                value /= 1024;
                unit++;
            }

            return FormatValue(value, unit);
        }

        /// <summary>The number the format below will actually print (AwayFromZero, as users read it).</summary>
        private static double RoundForDisplay(double value)
            => value >= 100
                ? Math.Round(value, 0, MidpointRounding.AwayFromZero)
                : Math.Round(value, 1, MidpointRounding.AwayFromZero);

        /// <summary>One unit's rendering: whole bytes, whole KB above 100, one decimal below.</summary>
        private static string FormatValue(double value, int unit)
            => unit == 0
                ? value.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " B"
                : value.ToString(value >= 100 ? "0" : "0.0", System.Globalization.CultureInfo.InvariantCulture) +
                  " " + Units[unit];

        /// <summary>"3.1 MB/s"; zero stays "0 B/s" instead of an empty string.</summary>
        public static string FormatSpeed(double bytesPerSecond)
            => bytesPerSecond <= 0 ? "0 B/s" : FormatBytes((long)Math.Round(bytesPerSecond)) + "/s";

        /// <summary>"00:32" / "2:05" (mm:ss), or empty when the remaining time is unknown.</summary>
        public static string FormatRemaining(TimeSpan? remaining)
        {
            if (remaining is not TimeSpan value || value < TimeSpan.Zero) return string.Empty;
            int totalSeconds = (int)Math.Min(value.TotalSeconds, 99 * 60 + 59);
            return (totalSeconds / 60).ToString("00", System.Globalization.CultureInfo.InvariantCulture) + ":" +
                   (totalSeconds % 60).ToString("00", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
