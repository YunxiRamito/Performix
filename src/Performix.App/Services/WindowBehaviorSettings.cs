// The two window-behaviour switches of the General page (settings IA v2).
//
// WHAT THEY ACTUALLY CONTROL
// --------------------------
//   Always on Top     AppWindow.Presenter.IsAlwaysOnTop (OverlappedPresenter) on the shell
//                     window, plus the promise that losing focus no longer hides the panel.
//                     The auto-hide is the whole reason this switch used to be marked
//                     "not available": a topmost window that closes the moment you click
//                     somewhere else is a topmost window nobody can see.
//   Special Key Wake  The vendor hotkey (Predator logo key, scan code E0 75, delivered by the
//                     low-level keyboard hook in KeyboardHookService) summons the panel while
//                     another app is in front. Off = the key press no longer opens it.
//
// WHERE THE FLAGS LIVE
// --------------------
// preysense.json → General.AlwaysOnTop / General.SpecialKeyWake, next to General.RunOnStartup.
// Reasons:
//   • appearance.json is documented as "how the shell looks" (theme / material / accent /
//     window style / which home rows are visible). These two are window behaviour.
//   • The sibling row on the same card, Run on Startup, is already General.RunOnStartup, so one
//     card maps to one store and one save path.
//   • ConfigurationService is already loaded before the shell exists (LocalizationService
//     .Initialize reads it in OnLaunched), so the shell can apply the saved answer on its very
//     first frame — which is what "kept after a restart" means.
// A configuration file written before these switches existed has neither member, which reads as
// "not on top" + "hotkey wake on": exactly the behaviour the shell had until now.
//
// This type is deliberately dependency-free (no WinUI, no HWND, no dispatcher, no static state),
// so the test project links this file and proves the persistence round-trip and the two
// decisions (tests\Performix.Tests\WindowBehaviorSettingsTests.cs).

using System;
using PreySense.Core.Configuration;

namespace Predex.WinUI.Services
{
    /// <summary>
    /// Persisted window behaviour. Defaults are the behaviour the shell had before the switches
    /// existed: not on top, hotkey wake on.
    /// </summary>
    public sealed class WindowBehaviorSettings
    {
        public bool AlwaysOnTop { get; set; }

        public bool SpecialKeyWake { get; set; } = true;

        public WindowBehaviorSettings Clone() => new()
        {
            AlwaysOnTop = AlwaysOnTop,
            SpecialKeyWake = SpecialKeyWake,
        };
    }

    /// <summary>
    /// The two decisions the shell asks about. Pure, so the "does topmost really disable the
    /// auto-hide?" question is a test instead of a manual click-through.
    /// </summary>
    public static class WindowBehaviorPolicy
    {
        /// <summary>
        /// May the window hide itself when it loses focus at all? Two independent reasons say no:
        /// the user pinned it on top, or the verification switch <c>--no-autohide</c> is set
        /// (screenshot / UI-automation runs need a window that stays put).
        /// </summary>
        public static bool AutoHideAllowed(bool alwaysOnTop, bool noAutoHide)
            => !alwaysOnTop && !noAutoHide;

        /// <summary>
        /// The full decision behind the delayed hide that follows a Deactivated event. Every
        /// input is a fact the shell already holds; nothing here touches WinUI.
        /// </summary>
        public static bool ShouldHideAfterDeactivate(
            bool alwaysOnTop,
            bool noAutoHide,
            bool explicitExit,
            bool windowVisible,
            bool appForeground,
            bool popupOpen)
            => windowVisible
               && !explicitExit
               && !appForeground
               && !popupOpen
               && AutoHideAllowed(alwaysOnTop, noAutoHide);

        /// <summary>
        /// A vendor hotkey press summons the panel only while the switch is on. Off means the
        /// press is ignored here; the key is still swallowed by the hook, so nothing else on the
        /// machine acts on it either (that is what "wake" has always meant for this key).
        /// </summary>
        public static bool ShouldSummonOnVendorHotkey(bool specialKeyWake) => specialKeyWake;
    }

    /// <summary>
    /// Reads and writes <see cref="WindowBehaviorSettings"/> through the existing
    /// <see cref="ConfigurationService"/>. Kept separate from the WinUI callers so the round-trip
    /// can be tested against a real preysense.json in a temp directory.
    /// </summary>
    public static class WindowBehaviorStore
    {
        /// <summary>Pure read: a missing General section reads as the documented defaults.</summary>
        public static WindowBehaviorSettings Read(PreySenseConfig? config)
        {
            var settings = new WindowBehaviorSettings();
            if (config?.General is not { } general) return settings;
            settings.AlwaysOnTop = general.AlwaysOnTop;
            settings.SpecialKeyWake = general.SpecialKeyWake;
            return settings;
        }

        /// <summary>
        /// Read from the live configuration service. A configuration that cannot be read at all
        /// (damaged file, no permission) falls back to the defaults instead of failing startup —
        /// the same contract AppearanceService.LoadPersisted and StartupTaskService follow.
        /// </summary>
        public static WindowBehaviorSettings Read(ConfigurationService service)
        {
            if (service is null) return new WindowBehaviorSettings();
            try { return Read(service.GetCurrent()); }
            catch { return new WindowBehaviorSettings(); }
        }

        /// <summary>Pure write into a configuration draft (callers own the save).</summary>
        public static void Apply(WindowBehaviorSettings settings, PreySenseConfig config)
        {
            if (settings is null || config is null) return;
            config.General ??= new GeneralConfig();
            config.General.AlwaysOnTop = settings.AlwaysOnTop;
            config.General.SpecialKeyWake = settings.SpecialKeyWake;
        }

        /// <summary>
        /// Persist through <see cref="ConfigurationService.Update"/>, which clones, validates and
        /// saves atomically — so the other General fields (language, startup answer) are never
        /// overwritten by a stale draft. Throws when the configuration cannot be written; the
        /// caller decides whether that is worth a log line.
        /// </summary>
        public static void Write(ConfigurationService service, WindowBehaviorSettings settings)
        {
            if (service is null || settings is null) return;
            WindowBehaviorSettings snapshot = settings.Clone();
            service.Update(config => Apply(snapshot, config));
        }
    }
}
