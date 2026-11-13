// InfoWindowService — WinUI shell implementation (S6).
//
// The real WPF implementation (app\Fork\PreySense.Frontend\Services\InfoWindowService.cs)
// drives a WPF InfoWindow and therefore cannot be linked into this build. The linked
// ViewModels call it after fan writes / mode switches
// (MainViewModel.Fans.cs, MainViewModel.PowerModes.cs), so a type with that name and API is
// required — but it no longer has to be a stub: every outcome now surfaces as the shell's
// InfoBar toast (ShellWindow.ShowToast: slide in at the top, 3 s auto-dismiss).
//
// Mapping of the WPF InfoWindow states:
//   ShowSuccess → InfoBar Severity=Success      (was: green 3 s toast)
//   ShowFailure → InfoBar Severity=Error        (was: red, stays until dismissed)
//   ShowWarning → InfoBar Severity=Warning
//   ShowInformation / ShowWorking → Severity=Informational (3 s; the shell does not keep a
//   persistent "working" row — no operation in the WinUI shell reports progress that way yet)
//   SetProgress / Dismiss → shell.log only (no persistent window to update)
//
// Any thread may call it: ShowToast marshals to the dispatcher.

using Microsoft.UI.Xaml.Controls;

namespace PreySense.Frontend.Services
{
    /// <summary>Right-bottom information toasts, mapped onto the shell InfoBar.</summary>
    public static class InfoWindowService
    {
        public static void ShowSuccess(string title, string? detail = null)
            => Toast(InfoBarSeverity.Success, title, detail);

        public static void ShowFailure(string title, string? detail = null)
            => Toast(InfoBarSeverity.Error, title, detail);

        public static void ShowWarning(string title, string? detail = null)
            => Toast(InfoBarSeverity.Warning, title, detail);

        public static void ShowInformation(string title, string? detail = null)
            => Toast(InfoBarSeverity.Informational, title, detail);

        public static void ShowWorking(string title, string? detail = null, double? progress = null)
            => Toast(InfoBarSeverity.Informational, title,
                progress is null ? detail : $"{detail} ({progress:0}%)");

        public static void SetProgress(double percent, string? detail = null)
            => Predex.WinUI.Services.ShellLog.Write("info",
                $"progress {percent:0}%{(detail is null ? "" : $" — {detail}")} (InfoBar has no progress row)");

        public static void Dismiss()
            => Predex.WinUI.Services.ShellLog.Write("info", "dismiss requested (InfoBar auto-dismisses)");

        private static void Toast(InfoBarSeverity severity, string title, string? detail)
        {
            string localizedTitle = LocalizationService.Translate(title);
            string? localizedDetail = detail is null ? null : LocalizationService.Translate(detail);
            Predex.WinUI.Services.ShellLog.Write("info",
                $"{severity} '{localizedTitle}'{(localizedDetail is null ? "" : $" — {localizedDetail}")}");
            Predex.WinUI.App.CurrentShell?.ShowToast(severity, localizedTitle, localizedDetail);
        }
    }
}
