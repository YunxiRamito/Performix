// General — settings sub-page code-behind (IA phase 1).
//
// Moved 1:1 out of the old flat SettingsPage (S2 port of the WPF SettingsWindow): language,
// the Theme navigation row, Run on Startup and Acer services. The behaviour and the binding
// directions are unchanged, only the page boundary is new.
//
//   Language        LocalizationService owns the value (not a ViewModel property), so the
//                   combo is synced by hand; the persist call stays here because the WinUI
//                   LocalizationService only switches and has no configuration dependency.
//   Run on Startup  OneWay IsOn + mirror into the VM + command (the command reads its own
//                   state as the target value, so a TwoWay echo would double-toggle).
//   Acer Services   OneWay IsOn + command; the command flips the state itself.
//   Theme           navigation only (SHELL-CONTRACT §2: the shell owns page switching).
//
// Every Toggled handler first compares the switch with the ViewModel: a Toggled raised by
// the OneWay binding push is an echo, not a user action.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PreySense.Frontend.Services;
using PreySense.Frontend.ViewModels;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Predex.WinUI.Pages
{
    /// <summary>
    /// General settings: language / theme entry / run on startup / Acer services, bound
    /// against <see cref="MainViewModel"/>.
    /// </summary>
    public sealed partial class GeneralPage : Page, INotifyPropertyChanged, IPredexPage
    {
        private bool _syncingLanguageSelection;

        public GeneralPage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        // ── Page contract ────────────────────────────────────────────────────────
        private MainViewModel? _vm;

        public MainViewModel? Vm
        {
            get => _vm;
            set
            {
                if (ReferenceEquals(_vm, value)) return;
                if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
                _vm = value;
                if (_vm is not null) _vm.PropertyChanged += OnVmPropertyChanged;

                OnPropertyChanged();
                SyncLanguageSelection();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        // ── Lifetime ─────────────────────────────────────────────────────────────
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            LocalizationService.LanguageChanged -= OnLanguageChanged;
            LocalizationService.LanguageChanged += OnLanguageChanged;
            _ = SyncFromViewModelAsync();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
            => LocalizationService.LanguageChanged -= OnLanguageChanged;

        private async Task SyncFromViewModelAsync()
        {
            SyncLanguageSelection();
            if (Vm is not { } vm) return;
            try { await vm.RefreshStartupPreferenceAsync(); }
            catch (Exception ex) { Predex.WinUI.Services.ShellLog.Write("general", $"startup preference refresh failed: {ex.Message}"); }
        }

        private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(MainViewModel.IsRunOnStartupEnabled) or nameof(MainViewModel.CanChangeRunOnStartup))
                Bindings.Update();
        }

        // ── Theme page entry (appearance lives on Pages\ThemePage) ───────────────
        private void OpenTheme_Click(object sender, RoutedEventArgs e)
            => App.CurrentShell?.NavigateTo("Theme");

        // ── Language ─────────────────────────────────────────────────────────────
        private void Language_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingLanguageSelection) return;
            if (sender is not ComboBox combo ||
                combo.SelectedItem is not ComboBoxItem item ||
                item.Tag is not string tag)
            {
                return;
            }

            string normalized = LocalizationService.Normalize(tag);
            if (normalized == LocalizationService.CurrentLanguage) return;

            LocalizationService.SetLanguage(normalized);
            PersistLanguage(normalized);
            Vm?.RefreshLocalizedText();

            // OneTime x:Bind text (labels, tooltips, combo items) is re-evaluated on Update.
            Bindings.Update();
        }

        /// <summary>
        /// The WPF service persisted the language inside SetLanguage(); the WinUI service only
        /// switches it (it has no config dependency). Persist here so a restart keeps it —
        /// same configuration field the WPF build writes.
        /// </summary>
        private static void PersistLanguage(string language)
        {
            try
            {
                PreySense.Core.Configuration.ConfigurationService.Instance
                    .Update(cfg => cfg.General.Language = language);
            }
            catch
            {
                // Configuration unavailable: the session still switches language.
            }
        }

        private void SyncLanguageSelection()
        {
            if (CmbLanguage is null) return;
            _syncingLanguageSelection = true;
            try { CmbLanguage.SelectedIndex = LocalizationService.LanguageIndex; }
            finally { _syncingLanguageSelection = false; }
        }

        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            SyncLanguageSelection();
            Bindings.Update();
        }

        // ── Run on Startup (WPF: TwoWay IsChecked + command) ────────────────────
        private async void RunOnStartup_Toggled(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleSwitch toggle || Vm is not { } vm) return;
            if (toggle.IsOn == vm.IsRunOnStartupEnabled) return;   // binding echo, not a click

            vm.IsRunOnStartupEnabled = toggle.IsOn;
            try { await vm.ToggleRunOnStartupCommand.ExecuteAsync(null); }
            finally { Bindings.Update(); }
        }

        // ── Acer Services (WPF: OneWay IsChecked + command flips the state) ─────
        private async void AcerServices_Toggled(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleSwitch toggle || Vm is not { } vm) return;
            if (toggle.IsOn == vm.IsAcerServicesEnabled) return;   // binding echo, not a click

            try { await vm.ToggleAcerServicesDebloaterCommand.ExecuteAsync(null); }
            finally { Bindings.Update(); }
        }
    }
}
