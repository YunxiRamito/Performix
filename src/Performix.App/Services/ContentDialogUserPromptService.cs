// WinUI 3 implementation of PreySense.Frontend.Services.IUserPromptService.
//
// The ViewModel's dialog seam (docs\WINUI3-MIGRATION.md §7.4) used to be satisfied by
// Views\CustomModalDialogCompat.cs, which called the Win32 MessageBoxW — a light-themed,
// answer-less box that ignored the app theme and blocked the dispatcher. This is the real
// implementation: a WinUI ContentDialog hosted on the shell's XamlRoot.
//
// Shape notes (the interface was written with this in mind):
//  • ConfirmAsync / NotifyAsync are the shapes the linked ViewModels call. They are fully
//    async: ContentDialog.ShowAsync() is async-only and blocking the UI thread on it would
//    deadlock the dialog.
//  • Confirm / Notify (the synchronous pair) cannot be implemented on a WinUI dispatcher.
//    They are kept for interface compatibility, log a line and answer "no"/do nothing. The
//    WPF build is what uses them (WpfUserPromptService forwards async → sync).
//  • Only one ContentDialog may be open per XamlRoot at a time (WinUI throws
//    "Only a single ContentDialog can be open at any time"), so calls are serialized by a
//    flag and a second concurrent prompt is reported in shell.log instead of crashing.
//
// Buttons: a confirmation has PrimaryButtonText + CloseButtonText (确认 / 取消); a
// notification has only the primary button. The primary button uses the app's
// PrimaryButtonStyle (Styles\Resources.xaml) and switches to DangerPrimaryButtonStyle for
// destructive prompts (GPU MUX switch / restart), which is what the WPF CustomModalDialog
// conveyed with its accent confirm button.

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PreySense.Frontend.Services;
using System;
using System.Threading.Tasks;

namespace Predex.WinUI.Services
{
    public sealed class ContentDialogUserPromptService : IUserPromptService
    {
        private readonly Func<XamlRoot?> _xamlRoot;
        private readonly Func<ElementTheme> _requestedTheme;
        private readonly DispatcherQueue _queue;
        private bool _dialogOpen;

        public ContentDialogUserPromptService(Func<XamlRoot?> xamlRoot, Func<ElementTheme> requestedTheme, DispatcherQueue queue)
        {
            _xamlRoot = xamlRoot;
            _requestedTheme = requestedTheme;
            _queue = queue;
        }

        /// <summary>True while a prompt is on screen (used by the shell to skip auto-hide).</summary>
        public bool IsDialogOpen => _dialogOpen;

        // ── Sync pair: not representable on the WinUI UI thread ─────────────────
        public bool Confirm(string title, string subtitle, string message, string confirmText, string cancelText = "Cancel")
        {
            ShellLog.Write("prompt", $"Confirm('{title}') ignored: WinUI prompts are async (use ConfirmAsync)");
            return false;
        }

        public void Notify(string title, string subtitle, string message, string confirmText)
            => ShellLog.Write("prompt", $"Notify('{title}') ignored: WinUI prompts are async (use NotifyAsync)");

        // ── Async pair: real ContentDialog ──────────────────────────────────────
        public Task<bool> ConfirmAsync(string title, string subtitle, string message, string confirmText, string cancelText = "Cancel")
            => ShowAsync(title, subtitle, message, confirmText, cancelText, showCancel: true);

        public Task NotifyAsync(string title, string subtitle, string message, string confirmText)
            => ShowAsync(title, subtitle, message, confirmText, "Cancel", showCancel: false);

        private async Task<bool> ShowAsync(string title, string subtitle, string message,
            string confirmText, string cancelText, bool showCancel)
        {
            if (!_queue.HasThreadAccess)
            {
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!_queue.TryEnqueue(async () =>
                    {
                        try { completion.TrySetResult(await ShowOnUiThreadAsync(title, subtitle, message, confirmText, cancelText, showCancel)); }
                        catch (Exception ex) { completion.TrySetException(ex); }
                    }))
                {
                    ShellLog.Write("prompt", $"could not marshal prompt '{title}' to the UI thread");
                    return false;
                }
                return await completion.Task;
            }

            return await ShowOnUiThreadAsync(title, subtitle, message, confirmText, cancelText, showCancel);
        }

        private async Task<bool> ShowOnUiThreadAsync(string title, string subtitle, string message,
            string confirmText, string cancelText, bool showCancel)
        {
            XamlRoot? root = _xamlRoot();
            if (root is null)
            {
                // The window has not finished loading (or is already closing). Answering "no"
                // is the safe direction for a confirmation and harmless for a notification.
                ShellLog.Write("prompt", $"'{title}' dropped: shell XamlRoot not available yet");
                return false;
            }

            if (_dialogOpen)
            {
                ShellLog.Write("prompt", $"'{title}' dropped: another ContentDialog is already open");
                return false;
            }

            _dialogOpen = true;
            ContentDialog? dialog = null;
            try
            {
                dialog = new ContentDialog
                {
                    XamlRoot = root,
                    // The dialog is hosted in the XamlRoot popup layer, i.e. outside the shell's
                    // RootGrid subtree, so it does not inherit RootGrid.RequestedTheme. Without
                    // this the dialog could render in the opposite theme of the app.
                    RequestedTheme = _requestedTheme(),
                    Title = title,
                    PrimaryButtonText = string.IsNullOrWhiteSpace(confirmText) ? "OK" : confirmText,
                    // Deliberately not ContentDialogButton.Primary: WinUI's
                    // "PrimaryAsDefaultButton" visual state replaces PrimaryButton.Style with
                    // AccentButtonStyle, which would override the DangerPrimaryButtonStyle below.
                    // Consequence: Enter does not auto-confirm; Esc still closes (returns None).
                    DefaultButton = ContentDialogButton.None,
                    Content = BuildBody(subtitle, message),
                };
                if (showCancel)
                    dialog.CloseButtonText = string.IsNullOrWhiteSpace(cancelText) ? "Cancel" : cancelText;

                // Destructive styling only for a confirmation: a notification's single "OK"
                // button stays accent-coloured (red is reserved for the decision to proceed).
                bool dangerous = showCancel && IsDestructive(title, subtitle);
                if (ResolveStyle(dangerous ? "DangerPrimaryButtonStyle" : "PrimaryButtonStyle") is { } primary)
                    dialog.PrimaryButtonStyle = primary;

                ShellLog.Write("prompt",
                    $"show '{title}' buttons={(showCancel ? "confirm+cancel" : "ok")} danger={dangerous}");

                ContentDialogResult result = await dialog.ShowAsync();
                bool confirmed = result == ContentDialogResult.Primary;
                ShellLog.Write("prompt", $"closed '{title}' result={result} confirmed={confirmed}");
                return confirmed;
            }
            catch (Exception ex)
            {
                ShellLog.Write("prompt", $"'{title}' FAILED: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
            finally
            {
                _dialogOpen = false;
                // Drop the visual tree reference early: a closed dialog keeps its content
                // alive through the popup host until the next layout pass otherwise.
                if (dialog is not null) dialog.Content = null;
            }
        }

        private static StackPanel BuildBody(string subtitle, string message)
        {
            var body = new StackPanel { Spacing = 6, MaxWidth = 320 };
            if (!string.IsNullOrWhiteSpace(subtitle))
            {
                body.Children.Add(new TextBlock
                {
                    Text = subtitle.Trim().TrimStart('•').Trim(),
                    FontSize = 11,
                    Foreground = ResolveBrush("TextSecondaryBrush"),
                    TextWrapping = TextWrapping.Wrap,
                });
            }
            body.Children.Add(new TextBlock
            {
                Text = message,
                FontSize = 12,
                Foreground = ResolveBrush("TextPrimaryBrush"),
                TextWrapping = TextWrapping.Wrap,
            });
            return body;
        }

        /// <summary>
        /// Destructive prompts: the only confirmation the WinUI shell can raise today is the
        /// GPU MUX / restart one (MainViewModel.Gpu.cs → ConfirmGpuRestartAsync). The strings
        /// that reach this service are the same ones the WPF dialog shows, so the check keys
        /// off them rather than adding a parameter the interface does not have.
        /// </summary>
        private static bool IsDestructive(string title, string subtitle)
        {
            string text = $"{title} {subtitle}";
            return text.Contains("MUX", StringComparison.OrdinalIgnoreCase)
                || text.Contains("restart", StringComparison.OrdinalIgnoreCase)
                || text.Contains("power cycle", StringComparison.OrdinalIgnoreCase);
        }

        private static Style? ResolveStyle(string key)
            => Application.Current?.Resources.TryGetValue(key, out object? value) == true
                ? value as Style
                : null;

        private static Microsoft.UI.Xaml.Media.Brush ResolveBrush(string key)
            => Application.Current?.Resources.TryGetValue(key, out object? value) == true &&
               value is Microsoft.UI.Xaml.Media.Brush brush
                ? brush
                : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }
}
