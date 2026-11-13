using System.Threading.Tasks;

namespace PreySense.Frontend.Services
{
    /// <summary>
    /// The ViewModel's only way to ask the user something. WPF shows a modal
    /// <c>CustomModalDialog</c>; WinUI 3 will show a <c>ContentDialog</c> over the shell's
    /// <c>XamlRoot</c> (see docs/WINUI3-MIGRATION.md §7.4). Keeping this as an interface is what
    /// lets <c>ViewModels\*.cs</c> move to another UI stack without editing dialog code.
    /// </summary>
    public interface IUserPromptService
    {
        /// <summary>Shows a confirmation and blocks until the user chooses. Returns false on cancel/close.</summary>
        bool Confirm(string title, string subtitle, string message, string confirmText, string cancelText = "Cancel");

        /// <summary>Async counterpart. WinUI 3 can only implement this shape; WPF forwards to <see cref="Confirm"/>.</summary>
        Task<bool> ConfirmAsync(string title, string subtitle, string message, string confirmText, string cancelText = "Cancel");

        /// <summary>Shows a single-button acknowledgement (no cancel button) and blocks until it is dismissed.</summary>
        void Notify(string title, string subtitle, string message, string confirmText);

        /// <summary>Async counterpart of <see cref="Notify"/>.</summary>
        Task NotifyAsync(string title, string subtitle, string message, string confirmText);
    }

    /// <summary>
    /// Default when nothing is injected (unit tests, headless hosts, WinUI before the dialog
    /// layer exists): never touches a window, confirms answer "no", notifications are swallowed.
    /// Callers stay null-safe and no UI thread is required.
    /// </summary>
    public sealed class UnattendedUserPromptService : IUserPromptService
    {
        public static UnattendedUserPromptService Instance { get; } = new();

        private UnattendedUserPromptService() { }

        public bool Confirm(string title, string subtitle, string message, string confirmText, string cancelText = "Cancel")
            => false;

        public Task<bool> ConfirmAsync(string title, string subtitle, string message, string confirmText, string cancelText = "Cancel")
            => Task.FromResult(false);

        public void Notify(string title, string subtitle, string message, string confirmText) { }

        public Task NotifyAsync(string title, string subtitle, string message, string confirmText)
            => Task.CompletedTask;
    }
}
