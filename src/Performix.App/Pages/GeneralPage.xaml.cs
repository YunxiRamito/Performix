// General — settings sub-page code-behind (settings IA v2).
//
// This page merges what used to be two pages (General + Power): language, run on startup and
// the two design rows that have no implementation yet, plus the whole Power Configuration
// group. The behaviour of every moved control is unchanged, only the page boundary is new.
//
//   Language        LocalizationService owns the value, so the combo is built from its
//                   catalogue and synced by hand; the persist call stays here because the WinUI
//                   service only switches and has no configuration dependency.
//   Run on Startup  OneWay IsOn + mirror into the VM + command (the command reads its own
//                   state as the target value, so a TwoWay echo would double-toggle).
//   Always on Top / Special Key Wake
//                   disabled rows: no implementation behind them (see the XAML comment).
//   On Battery/AC   the index is pushed into the combo by SyncPowerModeSelection once the item
//                   list exists, and a user selection writes it back to the ViewModel (which
//                   persists it) — no command, and no TwoWay SelectedIndex (see that method for
//                   the empty-box bug that TwoWay caused).
//   USB charging    dropdown index comes from the ViewModel; a user selection calls
//                   SetUsbChargingAsync (one write to the hardware layer, then a readback).
//   Battery limit   OneWay IsOn + command flips the state; the bar re-reads the cap.
//   Energy saver    TwoWay, no command (ViewModel property).
//
// Every Toggled handler first compares the switch with the ViewModel: a Toggled raised by the
// OneWay binding push is an echo, not a user action.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PreySense.Frontend.Services;
using PreySense.Frontend.ViewModels;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Predex.WinUI.Pages
{
    /// <summary>
    /// General settings: language / run on startup / always on top / special key wake, plus the
    /// Power Configuration group (on battery, on AC, USB power-off charging, battery limit,
    /// energy saver). Bound against <see cref="MainViewModel"/>.
    /// </summary>
    public sealed partial class GeneralPage : Page, INotifyPropertyChanged, IPredexPage
    {
        private bool _syncingLanguageSelection;
        private bool _syncingUsbCharging;
        private bool _syncingPowerMode;

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
                OnPropertyChanged(nameof(ProfileNames));
                SyncLanguageSelection();
                SyncUsbChargingSelection();
                BuildPowerModeItems();
                SyncPowerModeSelection();
            }
        }

        /// <summary>
        /// The two power-source dropdowns show Core's ProfileManager.SourceProfileNames, which
        /// are English ("Eco"/"Balanced"). The UI localizes them through the same Loc.T table
        /// the mode tiles use, so this page reads 节能 / 静音 / 均衡 / 性能 / 狂暴 like home.
        ///
        /// The design's sixth AC entry ("restore the original configuration") is NOT offered:
        /// Session.AcPowerMode only stores the five profile bytes, so there is nothing to
        /// persist for it (see the page comment in the XAML).
        /// </summary>
        public IReadOnlyList<string> ProfileNames
            => Vm?.SourceProfileNames.Select(LocalizationService.Translate).ToList() ?? [];

        /// <summary>"Charge cap 80%" — the real cap the ViewModel holds, not a fake reading.</summary>
        public string CapText(int percent) => LocalizationService.Format("Charge cap {0}%", percent);

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        /// <summary>Empty status line collapses instead of leaving a blank row.</summary>
        public Visibility StatusVisibility(string? status)
            => string.IsNullOrWhiteSpace(status) ? Visibility.Collapsed : Visibility.Visible;

        // ── Lifetime ─────────────────────────────────────────────────────────────
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            LocalizationService.LanguageChanged -= OnLanguageChanged;
            LocalizationService.LanguageChanged += OnLanguageChanged;
            BuildLanguageItems();
            BuildPowerModeItems();
            _ = SyncFromViewModelAsync();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
            => LocalizationService.LanguageChanged -= OnLanguageChanged;

        private async Task SyncFromViewModelAsync()
        {
            SyncLanguageSelection();
            SyncUsbChargingSelection();
            SyncPowerModeSelection();
            if (Vm is not { } vm) return;
            try { await vm.RefreshStartupPreferenceAsync(); }
            catch (Exception ex) { Predex.WinUI.Services.ShellLog.Write("general", $"startup preference refresh failed: {ex.Message}"); }
        }

        private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.IsRunOnStartupEnabled):
                case nameof(MainViewModel.CanChangeRunOnStartup):
                case nameof(MainViewModel.BatteryLimitPercent):
                    Bindings.Update();
                    break;
                case nameof(MainViewModel.UsbChargingCutoffPercent):
                    SyncUsbChargingSelection();
                    break;
                case nameof(MainViewModel.UsbChargingStatus):
                    Bindings.Update();   // function-bound visibility
                    break;
                case nameof(MainViewModel.SourceProfileNames):
                    OnPropertyChanged(nameof(ProfileNames));
                    BuildPowerModeItems();
                    SyncPowerModeSelection();
                    break;
                case nameof(MainViewModel.AcPowerModeIndex):
                case nameof(MainViewModel.BatteryPowerModeIndex):
                    SyncPowerModeSelection();
                    break;
            }
        }

        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            SyncLanguageSelection();
            SyncUsbChargingSelection();
            OnPropertyChanged(nameof(ProfileNames));
            BuildPowerModeItems();
            SyncPowerModeSelection();
            Bindings.Update();
        }

        // ── Language ─────────────────────────────────────────────────────────────
        /// <summary>
        /// Builds the picker from LocalizationService.LanguageOptions. Each item shows the
        /// language's own name (never a translated one) and carries its tag; a language with no
        /// translation table is listed but disabled, with the reason as its tooltip — the same
        /// "grey out and explain, never hide" rule the material list follows.
        /// </summary>
        private void BuildLanguageItems()
        {
            if (CmbLanguage is null) return;

            _syncingLanguageSelection = true;
            try
            {
                CmbLanguage.Items.Clear();
                foreach (LocalizationService.LanguageOption option in LocalizationService.LanguageOptions)
                {
                    var item = new ComboBoxItem
                    {
                        Content = option.NativeName,
                        Tag = option.Tag,
                        IsEnabled = option.HasTranslation,
                    };
                    if (!option.HasTranslation)
                    {
                        ToolTipService.SetToolTip(item,
                            Loc.T("No translation table yet — this language is listed so a later PR can add it."));
                    }
                    CmbLanguage.Items.Add(item);
                }
            }
            finally
            {
                _syncingLanguageSelection = false;
            }
        }

        private void Language_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingLanguageSelection) return;
            if (sender is not ComboBox combo ||
                combo.SelectedItem is not ComboBoxItem item ||
                item.Tag is not string tag)
            {
                return;
            }

            // A disabled entry cannot be selected through the UI, but a stale programmatic
            // selection must not switch the UI into a language with no table either.
            if (LocalizationService.FindLanguage(tag) is { HasTranslation: false }) return;

            string normalized = LocalizationService.Normalize(tag);
            if (normalized == LocalizationService.CurrentLanguage) return;

            LocalizationService.SetLanguage(normalized);
            PersistLanguage(normalized);
            Vm?.RefreshLocalizedText();

            // OneTime x:Bind text (labels, tooltips) is re-evaluated on Update.
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
            if (CmbLanguage is null || CmbLanguage.Items.Count == 0) return;

            _syncingLanguageSelection = true;
            try
            {
                int index = 0;
                for (int i = 0; i < CmbLanguage.Items.Count; i++)
                {
                    if (CmbLanguage.Items[i] is ComboBoxItem item &&
                        item.Tag is string tag &&
                        string.Equals(tag, LocalizationService.CurrentLanguage, StringComparison.OrdinalIgnoreCase))
                    {
                        index = i;
                        break;
                    }
                }
                CmbLanguage.SelectedIndex = index;
            }
            finally
            {
                _syncingLanguageSelection = false;
            }
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

        // ── Battery Limit (WPF: OneWay IsChecked + command flips the state) ─────
        private async void BatteryLimit_Toggled(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleSwitch toggle || Vm is not { } vm) return;
            if (toggle.IsOn == vm.IsBatteryLimitEnabled) return;   // binding echo, not a click

            try { await vm.ToggleBatteryLimitCommand.ExecuteAsync(null); }
            finally { Bindings.Update(); }
        }

        // ── USB Power-Off Charging (4-position dropdown, commits on selection) ──
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

        // ── Power source profiles (On Battery / On AC) ───────────────────────────
        // UX review item 4: the "On AC" box rendered EMPTY. Two things combined: the dropdowns
        // were bound TwoWay to SelectedIndex while their ItemsSource is empty until the shell
        // hands the page a ViewModel (WinUI then pushed the out-of-range index back as -1, and
        // the ViewModel ignores negative values, so nothing restored it), and the ItemsSource
        // itself is an x:Bind that resolves after the first layout pass.
        //
        // So the items are built here (like the language picker) and the index is written after
        // they exist; the ViewModel is only told about a real user selection.
        //
        // An index that cannot be read is not left blank either: the box shows the "Unknown"
        // placeholder and is disabled, per the same review item.
        private void BuildPowerModeItems()
        {
            if (CmbAcPowerMode is null || CmbBatteryPowerMode is null) return;

            _syncingPowerMode = true;
            try
            {
                CmbAcPowerMode.Items.Clear();
                CmbBatteryPowerMode.Items.Clear();
                foreach (string name in ProfileNames)
                {
                    CmbAcPowerMode.Items.Add(new ComboBoxItem { Content = name });
                    CmbBatteryPowerMode.Items.Add(new ComboBoxItem { Content = name });
                }
            }
            finally { _syncingPowerMode = false; }
        }

        private void SyncPowerModeSelection()
        {
            if (CmbAcPowerMode is null || CmbBatteryPowerMode is null) return;

            _syncingPowerMode = true;
            try
            {
                ApplyPowerModeSelection(CmbAcPowerMode, Vm?.AcPowerModeIndex ?? -1);
                ApplyPowerModeSelection(CmbBatteryPowerMode, Vm?.BatteryPowerModeIndex ?? -1);
            }
            finally { _syncingPowerMode = false; }
        }

        private static void ApplyPowerModeSelection(ComboBox combo, int index)
        {
            bool readable = index >= 0 && index < combo.Items.Count;
            combo.SelectedIndex = readable ? index : -1;
            combo.IsEnabled = readable;
        }

        private void AcPowerMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingPowerMode) return;
            if (sender is not ComboBox combo || Vm is not { } vm) return;
            if (combo.SelectedIndex < 0 || combo.SelectedIndex == vm.AcPowerModeIndex) return;
            vm.AcPowerModeIndex = combo.SelectedIndex;
        }

        private void BatteryPowerMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingPowerMode) return;
            if (sender is not ComboBox combo || Vm is not { } vm) return;
            if (combo.SelectedIndex < 0 || combo.SelectedIndex == vm.BatteryPowerModeIndex) return;
            vm.BatteryPowerModeIndex = combo.SelectedIndex;
        }
    }
}
