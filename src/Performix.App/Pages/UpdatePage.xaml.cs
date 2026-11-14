// Update — settings sub-page code-behind (split out of "Updates and About").
//
// The update card is unchanged: the state machine lives in Services\Update\UpdateSession (not
// here) because the shell destroys the page tree when the window hides to the tray while an
// update download may still be running (S6-REPORT §3). This page attaches/detaches its Changed
// subscription and re-renders; it never owns the transfer and never invents progress:
//
//   • the bar is indeterminate, the percent label hides and only received bytes are shown when
//     the transfer did not declare Content-Length (UpdateDownloadProgress.TotalBytes < 0),
//   • "up to date" is only shown when a complete manifest and a comparable running version
//     actually produced that answer (the machine reports Failed when the version is unreadable),
//   • every state change re-reads the machine — there are no local copies of the state that
//     could drift after a hide/show cycle.
//
// THEMING: no brush is read or set by string key. Styles come from Page.Resources and
// Styles\Resources.xaml, and every foreground is a {ThemeResource} that re-resolves on a
// light/dark switch. The failure colour switch is therefore a *style* switch, not a
// SolidColorBrush lookup.
//
// FAILURE COPY: the card says one sentence (what happened + which button to press). The
// technical detail — the 64-hex SHA-256 pair — is shortened to 8 hex chars in the tooltip and
// written in full to shell.log and to %LOCALAPPDATA%\Performix\logs\update.log.
//
// Vm is part of the shell page contract (SHELL-CONTRACT §3) even though this page reads no
// ViewModel value: every page the shell hands a ViewModel to exposes it, and a page that needs
// one later must not have to change the shell registry.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Predex.WinUI.Services;
using Predex.WinUI.Services.Update;
using PreySense.Frontend.Services;
using PreySense.Frontend.ViewModels;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Predex.WinUI.Pages
{
    /// <summary>
    /// Update: the real update surface (check / download / verify / install).
    /// </summary>
    public sealed partial class UpdatePage : Page, IPredexPage
    {
        /// <summary>"expected &lt;64 hex&gt;, got &lt;64 hex&gt;" — the SHA-256 mismatch detail.</summary>
        private static readonly Regex ShaDetail = new(
            @"expected\s+([0-9a-fA-F]{64})\s*,\s*got\s+([0-9a-fA-F]{64})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly Microsoft.UI.Dispatching.DispatcherQueue? _queue;
        private string? _loggedFailure;

        /// <summary>
        /// True while <see cref="RenderUpdateState"/> writes the switch: the Toggled event fires
        /// for a programmatic IsOn change too, and a render must never write the preference back
        /// (that would turn a read into a write on every session change).
        /// </summary>
        private bool _renderingAutoUpdate;

        public UpdatePage()
        {
            InitializeComponent();
            _queue = DispatcherQueue;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        /// <summary>Shell page contract (SHELL-CONTRACT §3). The page needs no property from it.</summary>
        public MainViewModel? Vm { get; set; }

        /// <summary>The folder the {@code --check-update} switch and this page log to.</summary>
        private static string UpdateLogFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Performix", "logs");

        // ── Lifetime ─────────────────────────────────────────────────────────────
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            LocalizationService.LanguageChanged -= OnLanguageChanged;
            LocalizationService.LanguageChanged += OnLanguageChanged;

            UpdateSession.Changed -= OnSessionChanged;
            UpdateSession.Changed += OnSessionChanged;

            RenderUpdateState();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            LocalizationService.LanguageChanged -= OnLanguageChanged;
            UpdateSession.Changed -= OnSessionChanged;
        }

        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            Bindings.Update();
            RenderUpdateState();
        }

        /// <summary>Session events arrive on a worker thread; render on the UI thread.</summary>
        private void OnSessionChanged(object? sender, EventArgs e)
            => _queue?.TryEnqueue(RenderUpdateState);

        // ── Actions ──────────────────────────────────────────────────────────────
        private async void UpdatePrimary_Click(object sender, RoutedEventArgs e)
        {
            UpdateStateMachine machine = UpdateSession.Machine;
            try
            {
                switch (machine.State)
                {
                    case UpdatePageState.Idle:
                    case UpdatePageState.UpToDate:
                        await UpdateSession.CheckAsync();
                        break;
                    case UpdatePageState.Available:
                        await UpdateSession.DownloadAsync();
                        break;
                    case UpdatePageState.Failed:
                        // The session decides: a failed check re-checks, a failed download /
                        // verification / install goes back to Available through the machine
                        // first. The page must not pick a transfer from a failed card.
                        await UpdateSession.RetryAsync();
                        break;
                    default:
                        // Busy states disable the button; nothing to do.
                        break;
                }
            }
            catch (Exception ex)
            {
                ShellLog.Write("update", $"page action failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private async void UpdateSecondary_Click(object sender, RoutedEventArgs e)
        {
            UpdateStateMachine machine = UpdateSession.Machine;
            try
            {
                switch (machine.State)
                {
                    case UpdatePageState.Downloading:
                        UpdateSession.Cancel();
                        break;
                    case UpdatePageState.Failed when machine.FailureStage is UpdateFailureStage.Download
                        or UpdateFailureStage.Verify:
                        // "换源重试": start from the next download address.
                        await UpdateSession.DownloadAsync(rotateSource: true);
                        break;
                }
            }
            catch (Exception ex)
            {
                ShellLog.Write("update", $"page secondary action failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// The "Automatic updates" switch. Writing it persists to preysense.json; turning it off
        /// also stops the running cycle (the runner's gate re-reads the preference and the status
        /// card is dismissed), so "off" really means "offline from this moment".
        /// </summary>
        private void AutoUpdate_Toggled(object sender, RoutedEventArgs e)
        {
            if (_renderingAutoUpdate) return;

            bool wanted = ChkAutoUpdate.IsOn;
            bool stored = UpdatePreferences.SetAutoUpdate(wanted);

            // A failed write leaves the old answer: show what the file says, not the click.
            if (stored != wanted) SetAutoUpdateSwitch(stored);

            if (!stored)
            {
                UpdateStatusPresenter.Dismiss();
                ShellLog.Write("update", "automatic updates turned off: the update path is offline again");
            }
            else
            {
                ShellLog.Write("update", "automatic updates turned on: the next check follows the schedule");
            }
        }

        /// <summary>Writes the switch without re-entering the handler.</summary>
        private void SetAutoUpdateSwitch(bool on)
        {
            _renderingAutoUpdate = true;
            try { ChkAutoUpdate.IsOn = on; }
            finally { _renderingAutoUpdate = false; }
        }

        /// <summary>Failure-only shortcut: the folder that holds update-check.log / update.log.</summary>
        private void OpenUpdateLogs_Click(object sender, RoutedEventArgs e)        {
            try
            {
                Directory.CreateDirectory(UpdateLogFolder);
                Process.Start(new ProcessStartInfo
                {
                    FileName = UpdateLogFolder,
                    UseShellExecute = true,
                    Verb = "open",
                });
                ShellLog.Write("update", $"opened update logs folder {UpdateLogFolder}");
            }
            catch (Exception ex)
            {
                ShellLog.Write("update", $"open update logs folder failed ({UpdateLogFolder}): " +
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        // ── State → UI ───────────────────────────────────────────────────────────
        /// <summary>
        /// Re-reads the session machine and writes the whole card. Called on load, on every
        /// session change and after a language switch, so there is exactly one render path.
        /// </summary>
        private void RenderUpdateState()
        {
            if (TxtUpdateState is null || TxtUpdateVersion is null) return;

            UpdateStateMachine machine = UpdateSession.Machine;

            TxtUpdateState.Text = Loc.T(StateTextKey(machine));
            // A real fault gets the danger colour; "no release yet" stays a normal state.
            TxtUpdateState.Style = StyleOrDefault(
                machine.HasFailure && !machine.IsNoRelease ? "MetaStateDangerStyle" : "MetaStateStyle");

            TxtUpdateVersion.Text = machine.CurrentVersionText;
            TxtUpdateChannel.Text = machine.Channel;

            // The stored answer, never the in-memory one: the switch shows the file.
            SetAutoUpdateSwitch(UpdatePreferences.PersistedAutoUpdate);

            // UX review item 9: a dash in the "Update source" / "publish time" rows could mean
            // "not checked", "no data" or "not supported". Before the first check both rows say
            // "Not checked" — the same word the status line at the top right uses. Once a check
            // HAS run, a value that is still missing stays a dash: it is genuinely unknown, and
            // an unknown value must look unknown (UI-DESIGN-RULES §5).
            string missing = machine.State == UpdatePageState.Idle ? Loc.T("Not checked") : "—";

            TxtUpdateSource.Text = machine.Source is null ? missing : Loc.T(machine.Source.Label);
            ToolTipService.SetToolTip(TxtUpdateSource,
                machine.Source is null ? null : machine.Check?.DescribeAttempts());

            TxtUpdatePublished.Text = machine.Manifest is null
                ? missing
                : machine.Manifest.PublishedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

            RenderTarget(machine);
            RenderNotes(machine);
            RenderProgress(machine);
            RenderFailure(machine);
            RenderBlockReason(machine);
            RenderActions(machine);

            // The card grows and shrinks with the state (notes, progress block, failure sentence).
            // The shell measured this page once on navigation, so ask it to re-fit after the
            // layout pass that applies the visibilities above: otherwise the primary button of
            // the "update available" state can sit below the fold of an idle-sized window.
            DispatcherQueue?.TryEnqueue(() => App.CurrentShell?.RefitCurrentPageHeight());
        }

        private static string StateTextKey(UpdateStateMachine machine) => machine.State switch
        {
            UpdatePageState.Checking => "Checking…",
            UpdatePageState.UpToDate => "Up to date",
            // A newer release that this build may not install in place is not "available" in the
            // sense the button implies: the minVersion gate closed CanDownload, so say so.
            UpdatePageState.Available when machine.RequiresManualInstall => "Needs manual install",
            UpdatePageState.Available => "Update available",
            UpdatePageState.Downloading => "Downloading…",
            UpdatePageState.Verifying => "Verifying…",
            UpdatePageState.ReadyToInstall => "Ready to install",
            UpdatePageState.Failed when machine.IsNoRelease => "No release yet",
            UpdatePageState.Failed => "Update failed",
            _ => "Not checked",
        };

        /// <summary>"新版本 v0.1.0-alpha.2 · 34.5 MB" — the target and what it costs.</summary>
        private void RenderTarget(UpdateStateMachine machine)
        {
            UpdateManifest? manifest = machine.Manifest;
            bool show = manifest is not null && machine.IsNewer;
            TxtUpdateTarget.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show)
            {
                TxtUpdateTarget.Text = string.Empty;
                ToolTipService.SetToolTip(TxtUpdateTarget, null);
                return;
            }

            string version = "v" + manifest!.Version;
            UpdateAsset? asset = machine.Asset;
            TxtUpdateTarget.Text = asset is null
                ? LocalizationService.Format("New version {0}", version)
                : LocalizationService.Format("New version {0} · {1}", version,
                    UpdateText.FormatBytes(asset.SizeBytes));
            ToolTipService.SetToolTip(TxtUpdateTarget, asset?.Name);
        }

        private void RenderNotes(UpdateStateMachine machine)
        {
            string notes = machine.Manifest?.Notes?.Trim() ?? string.Empty;
            bool hasManifestState = machine.State is UpdatePageState.Available or UpdatePageState.UpToDate
                or UpdatePageState.Downloading or UpdatePageState.Verifying or UpdatePageState.ReadyToInstall;
            bool show = hasManifestState && notes.Length > 0;

            TxtUpdateNotesHeader.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            NotesScroll.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            TxtUpdateNotes.Text = show ? notes : string.Empty;
        }

        private void RenderProgress(UpdateStateMachine machine)
        {
            bool show = machine.IsProgressVisible;
            PnlUpdateProgress.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show)
            {
                PrgUpdate.IsIndeterminate = false;
                PrgUpdate.Value = 0;
                TxtUpdatePercent.Visibility = Visibility.Collapsed;
                TxtUpdatePercent.Text = string.Empty;
                return;
            }

            if (machine.State == UpdatePageState.Verifying)
            {
                // Hashing a package has no meaningful percentage here; an indeterminate bar plus
                // one sentence is the honest presentation.
                PrgUpdate.IsIndeterminate = true;
                TxtUpdatePercent.Visibility = Visibility.Collapsed;
                TxtUpdateProgress.Text = Loc.T("Verifying the update package…");
                return;
            }

            UpdateDownloadProgress progress = machine.Progress;
            PrgUpdate.IsIndeterminate = !progress.IsTotalKnown;
            PrgUpdate.Value = progress.IsTotalKnown ? progress.Percent : 0;

            // Percent is the non-colour channel of the bar, so it is shown only when the machine
            // actually knows the total. It is never derived from the manifest size.
            TxtUpdatePercent.Visibility = progress.IsTotalKnown ? Visibility.Visible : Visibility.Collapsed;
            TxtUpdatePercent.Text = progress.IsTotalKnown
                ? LocalizationService.Format("{0}%",
                    Math.Round(progress.Percent).ToString("0", CultureInfo.CurrentCulture))
                : string.Empty;

            TxtUpdateProgress.Text = DescribeProgress(progress);
        }

        /// <summary>
        /// "12.4 MB / 86.2 MB · 3.1 MB/s · 00:24 left", or "12.4 MB downloaded (total size
        /// unknown)" when the server never declared a length — the indeterminate case.
        /// </summary>
        private static string DescribeProgress(UpdateDownloadProgress progress)
        {
            string received = UpdateText.FormatBytes(progress.ReceivedBytes);
            if (!progress.IsTotalKnown)
                return LocalizationService.Format("{0} downloaded (total size unknown)", received);

            string total = UpdateText.FormatBytes(progress.TotalBytes);
            string speed = UpdateText.FormatSpeed(progress.BytesPerSecond);
            string remaining = UpdateText.FormatRemaining(progress.Remaining);

            return remaining.Length == 0
                ? LocalizationService.Format("{0} / {1} · {2}", received, total, speed)
                : LocalizationService.Format("{0} / {1} · {2} · {3} left", received, total, speed, remaining);
        }

        private void RenderFailure(UpdateStateMachine machine)
        {
            BtnUpdateLogs.Visibility = machine.HasFailure ? Visibility.Visible : Visibility.Collapsed;

            if (!machine.HasFailure)
            {
                TxtUpdateFailure.Visibility = Visibility.Collapsed;
                TxtUpdateFailure.Text = string.Empty;
                ToolTipService.SetToolTip(TxtUpdateFailure, null);
                return;
            }

            TxtUpdateFailure.Style = StyleOrDefault(machine.IsNoRelease ? "FailureMutedStyle" : "FailureDangerStyle");
            TxtUpdateFailure.Text = DescribeFailure(machine);

            // The card never prints 128 hex characters. The tooltip carries the first 8 of each
            // hash, the log carries both in full.
            ToolTipService.SetToolTip(TxtUpdateFailure, DescribeFailureDetail(machine.FailureDetail));
            LogFailureOnce(machine);
            TxtUpdateFailure.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// One sentence: what happened, and which button the user should press.
        /// </summary>
        private static string DescribeFailure(UpdateStateMachine machine)
        {
            if (machine.IsNoRelease)
            {
                return Loc.T("The update feed has no release yet. Click Retry to check again, " +
                             "or open the logs folder to see each attempt.");
            }

            return machine.FailureStage switch
            {
                UpdateFailureStage.Check =>
                    Loc.T("The update check failed. Click Retry to check again."),
                UpdateFailureStage.Download =>
                    Loc.T("The download failed. Click Switch source and retry to use another address, " +
                          "or open the logs folder for details."),
                UpdateFailureStage.Verify => Loc.T(VerifyFailureKey(machine.FailureReason)),
                UpdateFailureStage.Install =>
                    Loc.T("The update could not be installed. Click Retry, or open the logs folder for details."),
                _ => Loc.T("The update failed. Click Retry to try again."),
            };
        }

        /// <summary>Maps the state machine's English reason to the sentence the card shows.</summary>
        private static string VerifyFailureKey(string? reason) => reason switch
        {
            "Verification failed: the SHA-256 does not match the manifest." =>
                "The package SHA-256 does not match the manifest, so the file was deleted. " +
                "Click Switch source and retry to download it again.",
            "Verification failed: the file size does not match the manifest." =>
                "The package size does not match the manifest, so the file was deleted. " +
                "Click Switch source and retry to download it again.",
            "Verification failed: the file could not be read." =>
                "The package could not be read, so verification did not finish. " +
                "Click Switch source and retry to download it again.",
            "The downloaded update package is missing." =>
                "The downloaded package is gone. Click Switch source and retry to download it again.",
            "Verification was cancelled." =>
                "Verification was cancelled. Click Retry to download the package again.",
            _ => "The package could not be verified. Click Switch source and retry to download it again.",
        };

        /// <summary>"SHA-256 · expected 9f2c0b8e… · got 4520f3f0…" — 8 chars, never 64.</summary>
        private static string? DescribeFailureDetail(string? detail)
        {
            if (string.IsNullOrWhiteSpace(detail)) return null;

            Match match = ShaDetail.Match(detail);
            return match.Success
                ? LocalizationService.Format("SHA-256 · expected {0}… · got {1}…",
                    match.Groups[1].Value[..8], match.Groups[2].Value[..8])
                : null;
        }

        /// <summary>Writes the full technical detail once per distinct failure, not once per render.</summary>
        private void LogFailureOnce(UpdateStateMachine machine)
        {
            if (machine.FailureDetail is not { Length: > 0 } detail) return;

            string key = machine.FailureStage + "|" + detail;
            if (string.Equals(key, _loggedFailure, StringComparison.Ordinal)) return;
            _loggedFailure = key;

            ShellLog.Write("update",
                $"page failure ({machine.FailureStage}): {machine.FailureReason} | full detail: {detail}");

            try
            {
                Directory.CreateDirectory(UpdateLogFolder);
                File.AppendAllText(
                    Path.Combine(UpdateLogFolder, "update.log"),
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {machine.FailureStage}: " +
                    $"{machine.FailureReason} | {detail}{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                ShellLog.Write("update", $"update.log append failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// The minVersion gate sentence. Shown only while the machine reports
        /// <see cref="UpdateStateMachine.RequiresManualInstall"/>; the template is the machine's
        /// own key, so the copy stays in one place.
        /// </summary>
        private void RenderBlockReason(UpdateStateMachine machine)
        {
            string? reason = machine.RequiresManualInstall
                ? LocalizationService.Format(
                    UpdateStateMachine.ManualInstallRequiredReason, machine.RequiredMinimumVersion)
                : null;

            TxtUpdateBlockReason.Visibility = reason is null ? Visibility.Collapsed : Visibility.Visible;
            TxtUpdateBlockReason.Text = reason ?? string.Empty;
        }

        private void RenderActions(UpdateStateMachine machine)
        {
            string? primaryText = null;
            bool primaryEnabled = false;
            string? secondaryKey = null;

            switch (machine.State)
            {
                case UpdatePageState.Idle:
                    primaryText = Loc.T("Check for updates");
                    primaryEnabled = true;
                    break;
                case UpdatePageState.Checking:
                    primaryText = Loc.T("Checking…");
                    break;
                case UpdatePageState.UpToDate:
                    primaryText = Loc.T("Check again");
                    primaryEnabled = true;
                    break;
                case UpdatePageState.Available:
                    // Name the target: "下载并安装 v0.1.0-alpha.2", not a bare "download".
                    // Below the manifest's minVersion the same label stays visible but inert:
                    // the machine's CanDownload is false, and TxtUpdateBlockReason explains why.
                    primaryText = machine.Manifest is null
                        ? Loc.T("Download and install")
                        : LocalizationService.Format("Download and install {0}",
                            "v" + machine.Manifest.Version);
                    primaryEnabled = machine.CanDownload;
                    break;
                case UpdatePageState.Downloading:
                    secondaryKey = "Cancel";
                    break;
                case UpdatePageState.Verifying:
                    primaryText = Loc.T("Verifying…");
                    break;
                case UpdatePageState.ReadyToInstall:
                    primaryText = Loc.T("Preparing to install…");
                    break;
                case UpdatePageState.Failed:
                    primaryText = Loc.T("Retry");
                    primaryEnabled = true;
                    if (machine.FailureStage is UpdateFailureStage.Download or UpdateFailureStage.Verify)
                        secondaryKey = "Switch source and retry";
                    break;
            }

            bool showPrimary = primaryText is not null;
            BtnUpdatePrimary.Visibility = showPrimary ? Visibility.Visible : Visibility.Collapsed;
            if (showPrimary)
            {
                BtnUpdatePrimary.Content = primaryText;
                BtnUpdatePrimary.IsEnabled = primaryEnabled;
                AutomationProperties.SetName(BtnUpdatePrimary, primaryText!);
                // An inert button still has to say why: the reason line above is the visible
                // answer, the same sentence rides along as tooltip / UIA help text.
                string? blockReason = machine.RequiresManualInstall ? TxtUpdateBlockReason.Text : null;
                ToolTipService.SetToolTip(BtnUpdatePrimary, blockReason);
                AutomationProperties.SetHelpText(BtnUpdatePrimary, blockReason ?? string.Empty);
            }

            bool showSecondary = secondaryKey is not null;
            BtnUpdateSecondary.Visibility = showSecondary ? Visibility.Visible : Visibility.Collapsed;
            if (showSecondary)
            {
                BtnUpdateSecondary.Content = Loc.T(secondaryKey!);
                AutomationProperties.SetName(BtnUpdateSecondary, Loc.T(secondaryKey!));
            }
        }

        private Style? StyleOrDefault(string key)
        {
            try
            {
                if (Resources.TryGetValue(key, out object? value) && value is Style style)
                    return style;
            }
            catch
            {
                // A style lookup must not break a render pass.
            }
            return null;
        }
    }
}
