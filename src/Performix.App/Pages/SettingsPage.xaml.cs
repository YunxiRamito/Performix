// S2 — Settings page code-behind (WinUI 3).
//
// Port of app\Fork\PreySense.Frontend\Views\SettingsWindow.xaml.cs (58 lines). What the WPF
// window did, mapped to a page:
//
//   ctor  Loaded → RefreshStartupPreferenceAsync()   → OnLoaded (same call, VM is a field now)
//   ctor  LanguageSelector.SelectedIndex = …         → SyncLanguageSelection()
//   ctor  UpdateUsbChargingLabel()                   → SyncUsbChargingSelection() (S2: dropdown)
//   UsbCharging_ValueChanged/MouseReleased/…         → Slider ValueChanged + PointerReleased /
//                                                      PointerCaptureLost / KeyUp ("commit on
//                                                      release", never per tick)
//   LanguageSelector_SelectionChanged                → Language_SelectionChanged (persist is
//                                                      re-added here: the WinUI
//                                                      LocalizationService only switches)
//
// The three switch rows keep the WPF bind direction, because the ViewModel commands read
// their own state as the target value:
//   • Run on Startup  — WPF bound IsChecked TwoWay, then ran ToggleRunOnStartupCommand.
//                       WinUI binds IsOn OneWay and mirrors the toggle into the VM in the
//                       handler, then runs the command (same net effect, no TwoWay echo loop).
//   • Acer Services   — WPF bound OneWay + command; the command flips the state itself.
//   • Battery Limit   — same as Acer Services.
// Every Toggled handler first compares the switch with the VM: a Toggled raised by the
// OneWay binding push is an echo, not a user action.
//
// Navigation, ViewModel creation and window sizing are the shell's job — this page only
// renders when the shell assigns Vm and never touches hardware directly.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using PreySense.Frontend.Services;
using PreySense.Frontend.ViewModels;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Predex.WinUI.Pages
{
    /// <summary>
    /// Settings: language / startup / Acer services / battery / USB charging and the AC &amp;
    /// battery power profiles, bound against <see cref="MainViewModel"/>. The four appearance
    /// controls (mode / window style / material / accent) live on <see cref="ThemePage"/> and
    /// are reached through the Theme row at the top of this page.
    /// </summary>
    public sealed partial class SettingsPage : Page, INotifyPropertyChanged, IPredexPage
    {
        private bool _syncingLanguageSelection;
        private bool _syncingUsbCharging;

        public SettingsPage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        // ── Page contract ────────────────────────────────────────────────────────
        // Assigned by the shell after construction. x:Bind paths hang off this property, so a
        // late assignment has to notify — the page is the binding source here.
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
                OnPropertyChanged(nameof(ProfileNames));
                SyncUsbChargingSelection();
                SyncLanguageSelection();
            }
        }

        /// <summary>
        /// Full version string, e.g. "v0.1.0-alpha.1". The Settings header is the only place
        /// the UI shows it (the shell title bar is reserved for Performix + CPU temperature).
        /// </summary>
        public string VersionText => PreySense.Frontend.Services.UpdateCheckService.DisplayVersion;

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// S1: the power-profile dropdown shows Core's ProfileManager.SourceProfileNames, which
        /// are English ("Eco"/"Balanced"). The UI localizes them through the same Loc.T table
        /// the tiles use, so the Settings page reads 节能 / 静音 / 均衡 / 性能 / 狂暴 like home.
        /// </summary>
        public IReadOnlyList<string> ProfileNames
            => Vm?.SourceProfileNames.Select(LocalizationService.Translate).ToList() ?? [];

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        // ── Derived text ─────────────────────────────────────────────────────────

        /// <summary>Empty status line collapses instead of leaving a blank row (WPF DataTrigger).</summary>
        public Visibility StatusVisibility(string? status)
            => string.IsNullOrWhiteSpace(status) ? Visibility.Collapsed : Visibility.Visible;

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
            // The language dropdown is not a ViewModel property (LocalizationService owns it).
            SyncLanguageSelection();
            SyncUsbChargingSelection();

            if (Vm is not { } vm) return;
            try { await vm.RefreshStartupPreferenceAsync(); }
            catch (Exception ex) { Predex.WinUI.Services.ShellLog.Write("settings", $"startup preference refresh failed: {ex.Message}"); }
        }

        private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.UsbChargingCutoffPercent):
                    SyncUsbChargingSelection();
                    break;
                case nameof(MainViewModel.UsbChargingStatus):
                    // Visibility comes from a function binding; force it to re-evaluate.
                    Bindings.Update();
                    break;
                case nameof(MainViewModel.SourceProfileNames):
                    OnPropertyChanged(nameof(ProfileNames));
                    break;
            }
        }

        // ── Theme page entry (appearance lives on Pages\ThemePage) ───────────────
        /// <summary>
        /// The Theme row is navigation, not a setting: the shell owns page switching
        /// (SHELL-CONTRACT §2). Back from the theme page returns here (ShellWindow's back
        /// stack), not straight to Home.
        /// </summary>
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
            SyncUsbChargingSelection();
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
            SyncUsbChargingSelection();
            OnPropertyChanged(nameof(ProfileNames));
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

        // ── Battery Limit (WPF: OneWay IsChecked + command flips the state) ─────
        private async void BatteryLimit_Toggled(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleSwitch toggle || Vm is not { } vm) return;
            if (toggle.IsOn == vm.IsBatteryLimitEnabled) return;   // binding echo, not a click

            try { await vm.ToggleBatteryLimitCommand.ExecuteAsync(null); }
            finally { Bindings.Update(); }
        }

        // ── USB Power-Off Charging (S2: 4-position dropdown, commits on selection) ─────
        private void UsbCharging_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingUsbCharging) return;
            if (sender is not ComboBox combo || combo.SelectedItem is not ComboBoxItem item) return;
            if (item.Tag is not string tag || !int.TryParse(tag, out int percent)) return;
            if (Vm is not { } vm || percent == vm.UsbChargingCutoffPercent) return;
            _ = vm.SetUsbChargingAsync(percent);
        }

        private void SyncUsbChargingSelection()
        {
            if (CmbUsbCharging is null) return;
            _syncingUsbCharging = true;
            try
            {
                int percent = Vm?.UsbChargingCutoffPercent ?? 0;
                CmbUsbCharging.SelectedIndex = Math.Clamp(percent / 10, 0, 3);
            }
            finally { _syncingUsbCharging = false; }
        }
    }
}
