// Maps the update state machine onto the bottom-right status card.
//
// One render path: the session raises Changed on every transition, this subscribes once and
// translates the machine into four lines of text + a bar. The page does the same job for its own
// card, from the same machine — there is no second copy of the state anywhere.
//
// WHAT IS SHOWN, in the order the automatic flow produces it:
//   Checking      "正在检查更新…"                      spinner
//   Available     "发现新版本 v…，开始下载"             (transient; the download follows at once)
//   Downloading   "正在下载更新…" + bytes/speed/percent  bar
//   Verifying     "正在校验更新包…"                      indeterminate bar
//   ReadyToInstall "更新已就绪，等您空闲时安装"          (the quiet-window gate is waiting)
//   UpToDate      "已是最新 v…"                          fades out after 8 s
//   Failed        "更新失败" + one sentence               fades out after 8 s, log has the detail
//
// The card is NOT topmost and never takes focus; a click opens the Update page (the detail view).
// All copy goes through LocalizationService, so zh-CN and English both come from the same table.

using Microsoft.UI.Dispatching;
using Predex.WinUI.Services;
using Predex.WinUI.Views;
using PreySense.Frontend.Services;
using System;

namespace Predex.WinUI.Services.Update
{
    /// <summary>Renders <see cref="UpdateStateMachine"/> into <see cref="UpdateInfoWindow"/>.</summary>
    internal static class UpdateStatusPresenter
    {
        private static bool _attached;

        /// <summary>Subscribes to the session and remembers how to open the detail page.</summary>
        internal static void Attach(DispatcherQueue queue, Action openDetails)
        {
            UpdateInfoWindow.Initialize(queue, openDetails);
            if (_attached) return;
            _attached = true;
            UpdateSession.Changed += (_, _) => RenderFromSession();
        }

        /// <summary>
        /// Re-reads the machine. Ignored while the automatic path is off: a manual check already
        /// has the page in front of the user, and a status card for it would be noise.
        /// </summary>
        internal static void RenderFromSession()
        {
            if (!UpdatePreferences.AutoUpdate) return;
            if (!UpdateAutoRunner.IsRunning) return;

            UpdateStateMachine machine = UpdateSession.Machine;
            switch (machine.State)
            {
                case UpdatePageState.Idle:
                    // Nothing to say yet (the loop has not fired, or it was cancelled).
                    UpdateInfoWindow.Dismiss();
                    break;

                case UpdatePageState.Checking:
                    Show(new UpdateInfoContent(
                        Title: Loc.T("Checking for updates…"),
                        Detail: LocalizationService.Format("Current version {0}", machine.CurrentVersionText),
                        Busy: true,
                        ProgressVisible: false,
                        ProgressIndeterminate: false,
                        ProgressPercent: 0,
                        ProgressText: null,
                        IsFailure: false,
                        AutoDismiss: false));
                    break;

                case UpdatePageState.Available:
                    Show(new UpdateInfoContent(
                        Title: LocalizationService.Format("Update {0} found",
                            "v" + (machine.Manifest?.Version?.ToString() ?? "?")),
                        Detail: Loc.T("Starting the download…"),
                        Busy: true,
                        ProgressVisible: false,
                        ProgressIndeterminate: false,
                        ProgressPercent: 0,
                        ProgressText: null,
                        IsFailure: false,
                        AutoDismiss: false));
                    break;

                case UpdatePageState.Downloading:
                    Show(Downloading(machine));
                    break;

                case UpdatePageState.Verifying:
                    Show(new UpdateInfoContent(
                        Title: Loc.T("Verifying the update package…"),
                        Detail: LocalizationService.Format("Update {0}",
                            "v" + (machine.Manifest?.Version?.ToString() ?? "?")),
                        Busy: true,
                        ProgressVisible: true,
                        ProgressIndeterminate: true,
                        ProgressPercent: 0,
                        ProgressText: null,
                        IsFailure: false,
                        AutoDismiss: false));
                    break;

                case UpdatePageState.ReadyToInstall:
                    ShowWaitingForUser();
                    break;

                case UpdatePageState.UpToDate:
                    Show(new UpdateInfoContent(
                        Title: Loc.T("Up to date"),
                        Detail: LocalizationService.Format("Performix {0} is the newest version.",
                            "v" + machine.CurrentVersionText),
                        Busy: false,
                        ProgressVisible: false,
                        ProgressIndeterminate: false,
                        ProgressPercent: 0,
                        ProgressText: null,
                        IsFailure: false,
                        AutoDismiss: true));
                    break;

                case UpdatePageState.Failed:
                    Show(new UpdateInfoContent(
                        Title: machine.IsNoRelease ? Loc.T("No release yet") : Loc.T("Update failed"),
                        Detail: DescribeFailure(machine),
                        Busy: false,
                        ProgressVisible: false,
                        ProgressIndeterminate: false,
                        ProgressPercent: 0,
                        ProgressText: null,
                        IsFailure: !machine.IsNoRelease,
                        AutoDismiss: true));
                    break;
            }
        }

        /// <summary>
        /// "正在检查更新…". Called by the runner before the check starts: a silent check that
        /// finds nothing never changes the machine state, so without this the card would have
        /// nothing to render while the request is in flight.
        /// </summary>
        internal static void ShowChecking()
        {
            UpdateStateMachine machine = UpdateSession.Machine;
            Show(new UpdateInfoContent(
                Title: Loc.T("Checking for updates…"),
                Detail: LocalizationService.Format("Current version {0}", machine.CurrentVersionText),
                Busy: true,
                ProgressVisible: false,
                ProgressIndeterminate: false,
                ProgressPercent: 0,
                ProgressText: null,
                IsFailure: false,
                AutoDismiss: false));
        }

        /// <summary>"更新已就绪，等您空闲时安装" — the gate is holding the install back.</summary>
        internal static void ShowWaitingForUser()
        {
            UpdateStateMachine machine = UpdateSession.Machine;
            string version = "v" + (machine.Manifest?.Version?.ToString() ?? "?");
            Show(new UpdateInfoContent(
                Title: LocalizationService.Format("Update {0} is ready", version),
                Detail: Loc.T("It will be installed and Performix restarted once you are not busy."),
                Busy: false,
                ProgressVisible: false,
                ProgressIndeterminate: false,
                ProgressPercent: 0,
                ProgressText: null,
                IsFailure: false,
                AutoDismiss: false));
        }

        /// <summary>"即将重启安装 v…" — the only warning before the app goes down.</summary>
        internal static void ShowRestarting(string version)
        {
            Show(new UpdateInfoContent(
                Title: LocalizationService.Format("Restarting to install {0}", "v" + version),
                Detail: Loc.T("Performix closes and reopens in a few seconds."),
                Busy: false,
                ProgressVisible: false,
                ProgressIndeterminate: false,
                ProgressPercent: 0,
                ProgressText: null,
                IsFailure: false,
                AutoDismiss: false));
        }

        /// <summary>Hides the card (feature switched off, or a manual action took over).</summary>
        internal static void Dismiss() => UpdateInfoWindow.Dismiss();

        private static UpdateInfoContent Downloading(UpdateStateMachine machine)
        {
            UpdateDownloadProgress progress = machine.Progress;
            string version = "v" + (machine.Manifest?.Version?.ToString() ?? "?");
            string received = UpdateText.FormatBytes(progress.ReceivedBytes);

            string progressText;
            string detail;
            if (progress.IsTotalKnown)
            {
                string total = UpdateText.FormatBytes(progress.TotalBytes);
                string speed = UpdateText.FormatSpeed(progress.BytesPerSecond);
                progressText = LocalizationService.Format("{0}%", Math.Round(progress.Percent).ToString("0"));
                detail = LocalizationService.Format("{0} / {1} · {2}", received, total, speed);
            }
            else
            {
                // Never invent a percentage: the server did not declare a length.
                progressText = string.Empty;
                detail = LocalizationService.Format("{0} downloaded (total size unknown)", received);
            }

            return new UpdateInfoContent(
                Title: LocalizationService.Format("Downloading update {0}", version),
                Detail: detail,
                Busy: true,
                ProgressVisible: true,
                ProgressIndeterminate: !progress.IsTotalKnown,
                ProgressPercent: progress.IsTotalKnown ? progress.Percent : 0,
                ProgressText: progressText,
                IsFailure: false,
                AutoDismiss: false);
        }

        /// <summary>One sentence, same wording policy as the page: say what to do next.</summary>
        private static string DescribeFailure(UpdateStateMachine machine)
        {
            if (machine.IsNoRelease)
                return Loc.T("The update feed has no release yet. It will be checked again later.");

            return machine.FailureStage switch
            {
                UpdateFailureStage.Check => Loc.T("The update check failed. It will be retried later."),
                UpdateFailureStage.Download => Loc.T("The download failed. It will be retried later."),
                UpdateFailureStage.Verify => Loc.T("The downloaded package did not pass verification, so it was discarded."),
                UpdateFailureStage.Install => Loc.T("The update could not be installed. Open the Update page for details."),
                _ => Loc.T("The update failed. It will be retried later."),
            };
        }

        private static void Show(UpdateInfoContent content) => UpdateInfoWindow.Present(content);
    }
}
