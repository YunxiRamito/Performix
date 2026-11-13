using System;
using System.Windows;
using PreySense.Frontend.Views;

namespace PreySense.Frontend.Services
{
    /// <summary>
    /// WPF implementation of <see cref="IUserPromptService"/>. It still uses the existing
    /// <see cref="CustomModalDialog"/> with the same owner, centering, button layout and modal
    /// (<c>ShowDialog</c>) semantics, so the shipped UI is unchanged; only the construction site
    /// moved out of the ViewModel.
    ///
    /// This type is the WPF-only half of the seam. A WinUI 3 shell provides its own
    /// implementation backed by <c>ContentDialog</c> + <c>XamlRoot</c> instead.
    /// </summary>
    public sealed class WpfUserPromptService : IUserPromptService
    {
        public bool Confirm(string title, string subtitle, string message, string confirmText, string cancelText = "Cancel")
            => ShowModal(title, subtitle, message, confirmText, cancelText, showCancelButton: true) == true;

        // WPF's ShowDialog already blocks, so the async shape completes synchronously. Awaiting it
        // from the UI thread keeps the exact old behaviour; awaiting it from tests cannot deadlock.
        public Task<bool> ConfirmAsync(string title, string subtitle, string message, string confirmText, string cancelText = "Cancel")
            => Task.FromResult(Confirm(title, subtitle, message, confirmText, cancelText));

        public void Notify(string title, string subtitle, string message, string confirmText)
            => ShowModal(title, subtitle, message, confirmText, cancelText: "Cancel", showCancelButton: false);

        public Task NotifyAsync(string title, string subtitle, string message, string confirmText)
        {
            Notify(title, subtitle, message, confirmText);
            return Task.CompletedTask;
        }

        private static bool? ShowModal(string title, string subtitle, string message,
            string confirmText, string cancelText, bool showCancelButton)
        {
            // No WPF application host (design host, early shutdown): behave like the unattended
            // default instead of throwing.
            Application? app = Application.Current;
            if (app?.Dispatcher is null)
                return null;

            bool? result = null;
            void Show()
            {
                var dialog = new CustomModalDialog
                {
                    Owner = app.MainWindow,
                    Title = title,
                    HeaderSubtitle = subtitle,
                    Message = message,
                    ConfirmText = confirmText,
                    CancelText = cancelText,
                    ShowCancelButton = showCancelButton
                };
                result = dialog.ShowDialog();
            }

            if (app.Dispatcher.CheckAccess())
                Show();
            else
                app.Dispatcher.Invoke(Show);

            return result;
        }
    }
}
