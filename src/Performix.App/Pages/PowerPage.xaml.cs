// Power Profiles — settings sub-page code-behind (IA phase 1).
//
// Moved 1:1 out of the old flat SettingsPage: battery limit, USB power-off charging, the
// hardware readback line, and the two power-source profiles plus the Windows energy saver.
// The binding directions and the command semantics are unchanged.
//
//   Battery Limit   OneWay IsOn + command flips the state (WPF parity).
//   USB charging    dropdown index comes from the ViewModel; a user selection calls
//                   SetUsbChargingAsync (one write to the hardware layer, then a readback).
//   On Battery/AC   SelectedIndex TwoWay — the ViewModel persists the preference, no command.
//   Energy saver    TwoWay, no command (ViewModel property).
//
// The profile names are Core's English ProfileManager.SourceProfileNames; the combo shows
// them through the same Loc.T table the home/tuning mode tiles use.

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
    /// Power settings: battery limit / USB power-off charging / on-battery and on-AC
    /// profiles / energy saver on battery, bound against <see cref="MainViewModel"/>.
    /// </summary>
    public sealed partial class PowerPage : Page, INotifyPropertyChanged, IPredexPage
    {
        private bool _syncingUsbCharging;

        public PowerPage()
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
                SyncUsbChargingSelection();
            }
        }

        /// <summary>
        /// S1: the power-profile dropdown shows Core's ProfileManager.SourceProfileNames, which
        /// are English ("Eco"/"Balanced"). The UI localizes them through the same Loc.T table
        /// the tiles use, so this page reads 节能 / 静音 / 均衡 / 性能 / 狂暴 like home.
        /// </summary>
        public IReadOnlyList<string> ProfileNames
            => Vm?.SourceProfileNames.Select(LocalizationService.Translate).ToList() ?? [];

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        /// <summary>Empty status line collapses instead of leaving a blank row (WPF DataTrigger).</summary>
        public Visibility StatusVisibility(string? status)
            => string.IsNullOrWhiteSpace(status) ? Visibility.Collapsed : Visibility.Visible;

        // ── Lifetime ─────────────────────────────────────────────────────────────
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            LocalizationService.LanguageChanged -= OnLanguageChanged;
            LocalizationService.LanguageChanged += OnLanguageChanged;
            SyncUsbChargingSelection();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
            => LocalizationService.LanguageChanged -= OnLanguageChanged;

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

        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            SyncUsbChargingSelection();
            OnPropertyChanged(nameof(ProfileNames));
            Bindings.Update();
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
