// About — settings sub-page code-behind (split out of "Updates and About").
//
// No ViewModel state: the page reads Services\AboutInfo (version, build time, logs folder) and
// the live process / OS architecture, so the values cannot drift from what is running. Vm is
// still part of the shell page contract (SHELL-CONTRACT §3) — every page the shell hands a
// ViewModel to exposes it.
//
// The repository URL is a display fact, not a configuration value: it is the same project the
// installer's User-Agent names (src\Performix.Installer\Program.cs), and the update feed is a
// separate, publisher-controlled concern that lives in Services\Update.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Predex.WinUI.Services;
using PreySense.Frontend.Services;
using PreySense.Frontend.ViewModels;
using System;
using System.Runtime.InteropServices;

namespace Predex.WinUI.Pages
{
    public sealed partial class AboutPage : Page, IPredexPage
    {
        /// <summary>The project page shown in the Repository row.</summary>
        private const string ProjectUrl = "https://github.com/YunxiRamito/Performix";

        public AboutPage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        /// <summary>Shell page contract (SHELL-CONTRACT §3). The page needs no property from it.</summary>
        public MainViewModel? Vm { get; set; }

        // ── Facts (re-read by Bindings.Update() after a language switch) ─────────
        public string VersionText => AboutInfo.VersionText;

        public string BuildTimeText => AboutInfo.BuildTimeText;

        /// <summary>
        /// "X64" when the process and the OS agree, "X86 · X64" only when the process is
        /// emulated. Printing the same architecture twice carried no information.
        /// </summary>
        public string ArchitectureText
        {
            get
            {
                Architecture process = RuntimeInformation.ProcessArchitecture;
                Architecture os = RuntimeInformation.OSArchitecture;
                return process == os ? os.ToString() : $"{process} · {os}";
            }
        }

        public string RepositoryUrl => ProjectUrl;

        /// <summary>The link label: the host + path without the scheme, so it fits the column.</summary>
        public string RepositoryLabel => ProjectUrl.Replace("https://", string.Empty, StringComparison.Ordinal);

        public string LogsFolderPath => AboutInfo.LogsFolderPath;

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
        private void OpenLogsFolder_Click(object sender, RoutedEventArgs e)
            => AboutInfo.OpenLogsFolder();
    }
}
