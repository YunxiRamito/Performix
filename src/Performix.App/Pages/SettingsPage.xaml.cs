// Settings — directory page code-behind (IA phase 1).
//
// This page owns no setting any more: every row is navigation. The old value/switch logic
// (language, startup, Acer services, battery limit, USB charging, power profiles) moved to
// Pages\GeneralPage and Pages\PowerPage unchanged; Theme stays on Pages\ThemePage.
//
// The only data this page renders is the version string in the page header and the subtitle
// of the Updates and About row. Both are static reads — no ViewModel, no hardware access.
//
// Navigation is the shell's job (SHELL-CONTRACT §2): App.CurrentShell.NavigateTo(tag). Back
// from any of the four sub-pages returns here through ShellWindow.PageBackTargets.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PreySense.Frontend.Services;
using System;

namespace Predex.WinUI.Pages
{
    /// <summary>
    /// Settings directory: General / Power Profiles / Theme / Updates and About.
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
        /// Full version string, e.g. "v0.1.0-alpha.1", shown at the top right of this page
        /// and repeated on the Updates and About page (the shell title bar is reserved for
        /// the wordmark and the CPU/GPU temperatures — UI-ARCH §2.8).
        /// </summary>
        public string VersionText => UpdateCheckService.DisplayVersion;

        /// <summary>
        /// The Updates and About row's subtitle: version plus the real update state. The feed
        /// is not configured yet, so this says so instead of inventing a check result.
        /// </summary>
        public string MetaSubtitle
            => LocalizationService.Format("Version {0} · no update feed configured", VersionText);

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
        // Navigation only: the shell switches pages and each sub-page has its own back target
        // (ShellWindow.PageBackTargets sends all four back to Settings).
        private void OpenGeneral_Click(object sender, RoutedEventArgs e)
            => App.CurrentShell?.NavigateTo("General");

        private void OpenPower_Click(object sender, RoutedEventArgs e)
            => App.CurrentShell?.NavigateTo("Power");

        private void OpenTheme_Click(object sender, RoutedEventArgs e)
            => App.CurrentShell?.NavigateTo("Theme");

        private void OpenMeta_Click(object sender, RoutedEventArgs e)
            => App.CurrentShell?.NavigateTo("Meta");
    }
}
