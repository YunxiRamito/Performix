// Updates and About — settings sub-page code-behind (IA phase 1).
//
// The page is read-only in practice: version / build time / architecture come from
// Services\AboutInfo (assembly metadata + the live runtime), and the action is "Open logs
// folder", which opens the directory ShellLog / CrashDoctor write to. The update card is bound
// to UpdateCheckService.IsUpdateFeedConfigured instead of a hard-coded string: while the flag
// is false the state line says "Not configured", the sentence explains why and "Check for
// updates" stays disabled. No fake progress bar is drawn anywhere.
//
// Vm is part of the shell page contract (SHELL-CONTRACT §3) even though this page reads no
// ViewModel value: every page the shell hands a ViewModel to exposes it, and a page that
// needs one later must not have to change the shell registry.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Predex.WinUI.Services;
using PreySense.Frontend.Services;
using PreySense.Frontend.ViewModels;
using System;

namespace Predex.WinUI.Pages
{
    /// <summary>
    /// Updates and About: update-feed state (placeholder, no feed configured), version, build
    /// metadata, logs folder entry and the licence line.
    /// </summary>
    public sealed partial class MiniMetaPage : Page, IPredexPage
    {
        public MiniMetaPage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        /// <summary>Shell page contract (SHELL-CONTRACT §3). The page needs no property from it.</summary>
        public MainViewModel? Vm { get; set; }

        // ── Facts (read once per bind; Bindings.Update() re-reads them) ──────────
        public string VersionText => AboutInfo.VersionText;
        public string BuildTimeText => AboutInfo.BuildTimeText;
        public string ArchitectureText => AboutInfo.ArchitectureText;
        public string LogsFolderPath => AboutInfo.LogsFolderPath;

        // ── Update placeholder: driven by the real flag, never hard-coded ────────
        // While UpdateCheckService.IsUpdateFeedConfigured is false the state line says
        // "not configured", the sentence explains why and the button is disabled. Flipping the
        // flag in the service makes all three change on the next bind, so the page cannot
        // keep claiming there is no feed after one is added (and cannot show fake progress).
        public bool IsUpdateFeedConfigured => UpdateCheckService.IsUpdateFeedConfigured;

        public string UpdateStateText
            => LocalizationService.Translate(IsUpdateFeedConfigured ? "Configured" : "Not configured");

        public string UpdateExplanationText => LocalizationService.Translate(IsUpdateFeedConfigured
            ? "Update checking uses the configured release feed."
            : "Performix has no release feed yet, so update checking stays offline and never contacts the network.");

        public string UpdateButtonHint => LocalizationService.Translate(IsUpdateFeedConfigured
            ? "Check the configured release feed for a newer version."
            : "Not wired to a release feed yet.");

        // ── Lifetime ─────────────────────────────────────────────────────────────
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            LocalizationService.LanguageChanged -= OnLanguageChanged;
            LocalizationService.LanguageChanged += OnLanguageChanged;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
            => LocalizationService.LanguageChanged -= OnLanguageChanged;

        private void OnLanguageChanged(object? sender, EventArgs e) => Bindings.Update();

        // ── Actions ──────────────────────────────────────────────────────────────
        /// <summary>
        /// Guarded on purpose: the button is only enabled when a feed exists, and even then
        /// this phase has no progress UI — the check logs its result, it never draws a bar.
        /// </summary>
        private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            if (!UpdateCheckService.IsUpdateFeedConfigured) return;
            await UpdateCheckService.CheckForUpdatesAsync();
        }

        private void OpenLogsFolder_Click(object sender, RoutedEventArgs e)
            => AboutInfo.OpenLogsFolder();
    }
}
