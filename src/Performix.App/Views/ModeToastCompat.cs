// ModeToastWindow — WinUI shell replacement, now backed by the desktop overlay (S8/OSD).
//
// The linked WPF ViewModel (MainViewModel.PowerModes.cs: `else ModeToastWindow.ShowConfirmedMode`)
// calls this once the EC confirms a performance-mode change. Two WinUI paths can reach it:
//
//   1. `MainViewModel.ModeToastPresenter` (the seam the tests use) — wired by
//      ShellWindow to Views\ModeOsdWindow.ShowMode, so the overlay appears without ever
//      touching this compat type.
//   2. This fallback, when no presenter is installed: it forwards to the same overlay.
//
// Before the overlay existed (S6) this type drew an InfoBar toast inside the shell window.
// That was wrong for a mode confirmation: the toast only exists while the shell window is
// visible (the notification has to reach the user while the panel is hidden to the tray) and
// it shared the strip with real errors. The InfoBar is kept for error/notice text only —
// prompts and failures stay on ContentDialogUserPromptService / ShellWindow.ShowToast, the
// success path never mixes with them.
//
// The exact API surface the linked ViewModel expects is unchanged
// (Initialize(DispatcherQueue) / ShowConfirmedMode(byte, string) / Shutdown()), so nothing in
// PreySense.Frontend had to change (red line).

using Microsoft.UI.Dispatching;
using Predex.WinUI.Views;
using System;

namespace PreySense.Frontend.Views
{
    /// <summary>Brief mode-confirmed notification, rendered by the desktop OSD overlay.</summary>
    public static class ModeToastWindow
    {
        private static DispatcherQueue? _queue;

        /// <summary>Called once from the ShellWindow constructor so the overlay has a UI queue.</summary>
        internal static void Initialize(DispatcherQueue queue)
        {
            _queue = queue;
            ModeOsdWindow.Initialize(queue);
        }

        /// <summary>Show the mode-confirmed overlay. <paramref name="title"/> is already localized.</summary>
        public static void ShowConfirmedMode(byte mode, string title)
        {
            System.Diagnostics.Debug.WriteLine($"[Predex] Mode confirmed: {title} (0x{mode:X2})");
            if (_queue is null) return;
            _queue.TryEnqueue(() => ModeOsdWindow.ShowMode(mode, title));
        }

        /// <summary>Called from the shell quit path: close the overlay window.</summary>
        public static void Shutdown() => ModeOsdWindow.Shutdown();
    }
}
