// Settings — directory page code-behind (settings IA v2, compact icon rows).
//
// This page owns no setting: every row is navigation. The old flat settings surface has been
// split into the six sub-pages the design asks for (General / Personalisation / Tuning /
// Services / Update / About). The old Power page is gone — its content is the
// "Power Configuration" group of GeneralPage — and the old Updates-and-About page is now two
// entries (Update, About).
//
// The only data this page renders is the version string in the page header. It is a static
// read — no ViewModel, no hardware access, and deliberately no per-row subtitle: the design
// review asked for icon + name only.
//
// Navigation is the shell's job (SHELL-CONTRACT §2): App.CurrentShell.NavigateTo(tag). Back from
// any of the five settings sub-pages returns here through ShellWindow.PageBackTargets; Tuning is
// not in that table because it is also reachable from the home panel, so its back target is the
// hop that opened it (Settings from here, Home from the panel).

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Predex.WinUI.Services;
using PreySense.Frontend.Services;
using System;

namespace Predex.WinUI.Pages
{
    /// <summary>
    /// Settings directory: General / Personalisation / Tuning / Services / Update / About.
    /// </summary>
    public sealed partial class SettingsPage : Page
    {
        public SettingsPage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        /// <summary>
        /// Full version string, e.g. "版本 v0.1.0-alpha.1", shown at the top right of this page.
        /// UX review item 12: the bare string had no label, so a reader could not tell whether
        /// "v0.1.0-alpha.1" was the version or some other identifier.
        /// </summary>
        public string VersionText
            => LocalizationService.Format("Version {0}", UpdateCheckService.DisplayVersion);

        /// <summary>
        /// The Services entry names the machine's vendor ("ASUS services" / "Acer services" /
        /// the neutral "Vendor services"), from the same ServicesTitle helper the shell title bar
        /// uses, so the two never disagree.
        /// </summary>
        public string ServicesLabel => ServicesTitle.For(OemBrandProbe.Detect());

        // ── Lifetime ─────────────────────────────────────────────────────────────
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            LocalizationService.LanguageChanged -= OnLanguageChanged;
            LocalizationService.LanguageChanged += OnLanguageChanged;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
            => LocalizationService.LanguageChanged -= OnLanguageChanged;

        private void OnLanguageChanged(object? sender, EventArgs e) => Bindings.Update();

        // ── Directory rows ───────────────────────────────────────────────────────
        // Navigation only: the shell switches pages, each sub-page has its own back target.
        private void OpenGeneral_Click(object sender, RoutedEventArgs e)
            => App.CurrentShell?.NavigateTo("General");

        private void OpenPersonalize_Click(object sender, RoutedEventArgs e)
            => App.CurrentShell?.NavigateTo("Personalize");

        private void OpenTuning_Click(object sender, RoutedEventArgs e)
            => App.CurrentShell?.NavigateTo("Tuning");

        private void OpenServices_Click(object sender, RoutedEventArgs e)
            => App.CurrentShell?.NavigateTo("Services");

        private void OpenUpdate_Click(object sender, RoutedEventArgs e)
            => App.CurrentShell?.NavigateTo("Update");

        private void OpenAbout_Click(object sender, RoutedEventArgs e)
            => App.CurrentShell?.NavigateTo("About");
    }
}
