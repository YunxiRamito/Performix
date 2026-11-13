// Lighting page code-behind (S5).
//
// The WPF original (Views\KeyboardWindow.xaml.cs, 615 lines) did everything by hand:
// every colour wheel wrote a "#RRGGBB" string, every combo box carried a Tag string, and
// panels were toggled with `Visibility` from MultiDataTriggers. WinUI 3 has no
// MultiDataTrigger and no IValueConverter support in x:Bind, so the split here is:
//
//   * static one-way text/visibility/bool->visibility: XAML x:Bind (everything declarative),
//   * two-way editing: x:Bind for exact-type bools (ToggleSwitch.IsOn), code-behind for
//     int <-> double (Slider), Color <-> "#RRGGBB" (ColorSpectrum/TextBox) and combo
//     Tag/DisplayMemberPath selections.
//
// All UI->VM writes are guarded by _syncingUi, which is true only while VM->UI sync runs;
// this is the WinUI replacement for the WPF page's four _isUpdating* flags.

using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PreySense.Core.Hardware;
using PreySense.Frontend.ViewModels;
using PreySense.Rgb;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.UI;

namespace Predex.WinUI.Pages
{
    /// <summary>
    /// Lighting page. The shell assigns <see cref="Vm"/>; the page never constructs a
    /// MainViewModel, never navigates and never talks to hardware directly.
    /// </summary>
    public sealed partial class LightingPage : Page, IPredexPage
    {
        // E92E = keyboard, E9A9 = brightness/lighting, E895 = refresh (FluentGlyphs table).
        private static readonly TimeSpan StatusLifetime = TimeSpan.FromSeconds(3);

        private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
        private MainViewModel? _vm;
        private bool _syncingUi;
        private bool _hidSubscribed;
        private DispatcherQueueTimer? _statusTimer;

        public LightingPage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        /// <summary>Assigned by the shell right after construction (page contract).</summary>
        public MainViewModel? Vm
        {
            get => _vm;
            set
            {
                if (ReferenceEquals(_vm, value)) return;
                if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
                _vm = value;
                if (_vm is not null) _vm.PropertyChanged += OnVmPropertyChanged;

                // Re-evaluate every compiled binding against the new ViewModel.
                try { Bindings.Update(); } catch { /* called before the XAML tree is live */ }
                SyncFromVm();
                RaiseDeviceRowState();
            }
        }

        // ── Lifecycle ────────────────────────────────────────────────────────────
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Compatibility fallback: if the shell has not assigned Vm yet, use the shared
            // instance (integration can drop this once PageTypes sets Vm explicitly).
            if (_vm is null) Vm = App.CurrentShell?.ViewModel;
            if (_vm is null) return;

            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.PropertyChanged += OnVmPropertyChanged;

            // HID hot-plug: same static fan-out the WPF window used (HidArrivalHub), so a
            // keyboard plugged in later shows up without restarting the app.
            if (!_hidSubscribed)
            {
                HidArrivalHub.Arrived -= OnHidArrived;
                HidArrivalHub.Arrived += OnHidArrived;
                _hidSubscribed = true;
            }

            SyncFromVm();

            if (!_vm.HasLightingDevices && !_vm.IsRefreshingLightingDevices)
                _ = _vm.DetectLightingDevicesAsync();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
            if (_hidSubscribed)
            {
                HidArrivalHub.Arrived -= OnHidArrived;
                _hidSubscribed = false;
            }
        }

        private void OnHidArrived()
        {
            _dispatcher.TryEnqueue(() =>
            {
                if (_vm is not null) _ = _vm.DetectLightingDevicesAsync();
            });
        }

        // ── ViewModel -> UI ──────────────────────────────────────────────────────
        private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (_vm is null || string.IsNullOrEmpty(e.PropertyName)) return;

            switch (e.PropertyName)
            {
                case nameof(MainViewModel.LightingDevices):
                    Sync(RaiseDeviceRowState);
                    break;

                case nameof(MainViewModel.SelectedHexColor):
                case nameof(MainViewModel.SelectedRgbZone):
                    Sync(() => SyncZoneColorUi());
                    break;

                case nameof(MainViewModel.CoverHexColor):
                    Sync(() => SyncCoverColorUi());
                    break;

                case nameof(MainViewModel.CoverLightingMode):
                case nameof(MainViewModel.IsCoverSyncWithKeyboard):
                    Sync(() =>
                    {
                        SyncCoverModeCombo();
                        UpdateCoverPanels();
                    });
                    break;

                case nameof(MainViewModel.RgbBrightness):
                    Sync(() => SetSliderValue(BrightnessSlider, _vm.RgbBrightness));
                    break;

                case nameof(MainViewModel.KbdTimeoutSeconds):
                case nameof(MainViewModel.KbdTimeoutDisplay):
                    Sync(() => SetSliderValue(TimeoutSlider, _vm.KbdTimeoutSeconds));
                    break;

                case nameof(MainViewModel.KeyboardAnimationSpeedLevel):
                    Sync(() => SetSliderValue(HardwareSpeedSlider, _vm.KeyboardAnimationSpeedLevel));
                    break;

                case nameof(MainViewModel.RgbSpeed):
                    Sync(() => SetSliderValue(SoftwareSpeedSlider, _vm.RgbSpeed));
                    break;

                case nameof(MainViewModel.CoverBrightness):
                    Sync(() => SetSliderValue(CoverBrightnessSlider, _vm.CoverBrightness));
                    break;

                case nameof(MainViewModel.LogoAnimationSpeedLevel):
                    Sync(() => SetSliderValue(CoverSpeedSlider, _vm.LogoAnimationSpeedLevel));
                    break;

                case nameof(MainViewModel.IsRgbSyncZonesEnabled):
                    Sync(UpdateLegacyModePanels);
                    break;

                case nameof(MainViewModel.LightingEffectTag):
                    Sync(() => ApplyLightingModeUi(_vm.LightingEffectTag));
                    break;

                case nameof(MainViewModel.SelectedZonePreset):
                    Sync(SyncStaticPresetCombo);
                    break;

                case nameof(MainViewModel.SelectedLightingTab):
                case nameof(MainViewModel.IsKeyboardTabSelected):
                case nameof(MainViewModel.IsCoverTabSelected):
                    Sync(SyncLegacyTargetTabs);
                    break;

                case nameof(MainViewModel.SelectedLightingDevice):
                    Sync(() =>
                    {
                        SyncDeviceList();
                        SyncDeviceModeCombo();
                        UpdateDevicePanels();
                        RaiseDeviceRowState();
                    });
                    break;

                case nameof(MainViewModel.SelectedLightingDeviceMode):
                    Sync(() =>
                    {
                        SyncDeviceModeCombo();
                        SyncDeviceSpeedSliderBounds();
                        SyncDeviceColorUi();
                        UpdateDevicePanels();
                    });
                    break;

                case nameof(MainViewModel.HasDeviceLightingSpeed):
                case nameof(MainViewModel.DeviceLightingSpeedMinimum):
                case nameof(MainViewModel.DeviceLightingSpeedMaximum):
                    Sync(SyncDeviceSpeedSliderBounds);
                    break;

                case nameof(MainViewModel.DeviceLightingSpeedLevel):
                    Sync(() => SetSliderValue(DeviceSpeedSlider, _vm.DeviceLightingSpeedLevel));
                    break;

                case nameof(MainViewModel.DeviceLightingBrightness):
                    Sync(() =>
                    {
                        SyncDeviceBrightnessStep();
                        SetSliderValue(DeviceBrightnessSlider, _vm.DeviceLightingBrightness);
                    });
                    break;

                case nameof(MainViewModel.DeviceLightingColorHex):
                    Sync(SyncDeviceColorUi);
                    break;

                case nameof(MainViewModel.SelectedLightingKey):
                case nameof(MainViewModel.HasSelectedLightingKey):
                    Sync(() =>
                    {
                        SyncLedListSelection();
                        SyncKeyColorUi();
                    });
                    break;

                case nameof(MainViewModel.SelectedModePowerModeColor):
                case nameof(MainViewModel.HasSelectedModePowerModeColor):
                    Sync(SyncModeColorUi);
                    break;

                case nameof(MainViewModel.IsModeKeySyncWithKeyboard):
                case nameof(MainViewModel.ShowModePowerModeColors):
                case nameof(MainViewModel.ShowDeviceColorFields):
                case nameof(MainViewModel.ShowDeviceColorWheel):
                case nameof(MainViewModel.ShowKeyboardLayout):
                case nameof(MainViewModel.ShowIndividualLedGrid):
                case nameof(MainViewModel.ShowIndividualLightingEditors):
                    Sync(UpdateDevicePanels);
                    break;
            }
        }

        /// <summary>Runs a VM->UI sync block with the feedback-loop guard raised.</summary>
        private void Sync(Action action)
        {
            if (_syncingUi) return;
            _syncingUi = true;
            try { action(); }
            catch { /* a detached page must never take the shell down */ }
            finally { _syncingUi = false; }
        }

        private void SyncFromVm()
        {
            if (_vm is null) return;
            Sync(() =>
            {
                SyncLegacyTargetTabs();
                SyncDeviceList();
                SyncDeviceModeCombo();
                SyncZoneColorUi();
                SyncCoverModeCombo();
                SyncCoverColorUi();
                SyncStaticPresetCombo();
                ApplyLightingModeUi(_vm.LightingEffectTag);
                SetSliderValue(BrightnessSlider, _vm.RgbBrightness);
                SetSliderValue(TimeoutSlider, _vm.KbdTimeoutSeconds);
                SetSliderValue(HardwareSpeedSlider, _vm.KeyboardAnimationSpeedLevel);
                SetSliderValue(SoftwareSpeedSlider, _vm.RgbSpeed);
                SetSliderValue(CoverBrightnessSlider, _vm.CoverBrightness);
                SetSliderValue(CoverSpeedSlider, _vm.LogoAnimationSpeedLevel);
                SetSliderValue(DeviceBrightnessSlider, _vm.DeviceLightingBrightness);
                SyncDeviceBrightnessStep();
                SyncDeviceSpeedSliderBounds();
                SyncDeviceColorUi();
                SyncLedListSelection();
                SyncKeyColorUi();
                SyncModeColorUi();
                UpdateLegacyModePanels();
                UpdateCoverPanels();
                UpdateDevicePanels();
            });
        }

        // ── Slider plumbing (int ViewModel values <-> double Slider.Value) ───────
        private static void SetSliderValue(Slider slider, int value)
        {
            double target = Math.Clamp(value, slider.Minimum, slider.Maximum);
            if (Math.Abs(slider.Value - target) < 0.5) return;
            slider.Value = target;
        }

        private void TimeoutSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_syncingUi || _vm is null) return;
            _vm.KbdTimeoutSeconds = (int)Math.Round(e.NewValue);
        }

        private void BrightnessSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_syncingUi || _vm is null) return;
            _vm.RgbBrightness = (int)Math.Round(e.NewValue);
        }

        private void HardwareSpeedSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_syncingUi || _vm is null) return;
            _vm.KeyboardAnimationSpeedLevel = (int)Math.Round(e.NewValue);
        }

        private void SoftwareSpeedSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_syncingUi || _vm is null) return;
            _vm.RgbSpeed = (int)Math.Round(e.NewValue);
        }

        private void CoverBrightnessSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_syncingUi || _vm is null) return;
            _vm.CoverBrightness = (int)Math.Round(e.NewValue);
        }

        private void CoverSpeedSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_syncingUi || _vm is null) return;
            _vm.LogoAnimationSpeedLevel = (int)Math.Round(e.NewValue);
        }

        private void DeviceBrightnessSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_syncingUi || _vm is null) return;
            _vm.DeviceLightingBrightness = (int)Math.Round(e.NewValue);
        }

        private void DeviceSpeedSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_syncingUi || _vm is null) return;
            _vm.DeviceLightingSpeedLevel = (int)Math.Round(e.NewValue);
        }

        private void SyncDeviceSpeedSliderBounds()
        {
            if (_vm is null) return;
            DeviceSpeedSlider.Minimum = _vm.DeviceLightingSpeedMinimum;
            DeviceSpeedSlider.Maximum = Math.Max(_vm.DeviceLightingSpeedMinimum, _vm.DeviceLightingSpeedMaximum);
            DeviceSpeedSlider.IsEnabled = _vm.HasDeviceLightingSpeed;
            SetSliderValue(DeviceSpeedSlider, _vm.DeviceLightingSpeedLevel);
        }

        private void SyncDeviceBrightnessStep()
        {
            if (_vm is null) return;
            int step = Math.Max(1, _vm.DeviceLightingBrightnessStep);
            DeviceBrightnessSlider.StepFrequency = step;
            DeviceBrightnessSlider.TickFrequency = step * 5;
        }

        // ── Colour pickers + hex boxes (Color <-> "#RRGGBB") ────────────────────
        private void SyncZoneColorUi()
        {
            if (_vm is null) return;
            SetPickerColor(ZoneColorPicker, _vm.SelectedHexColor);
            SetHexText(ZoneHexBox, _vm.SelectedHexColor);
        }

        private void SyncCoverColorUi()
        {
            if (_vm is null) return;
            SetPickerColor(CoverColorPicker, _vm.CoverHexColor);
            SetHexText(CoverHexBox, _vm.CoverHexColor);
        }

        private void SyncDeviceColorUi()
        {
            if (_vm is null) return;
            SetPickerColor(DeviceColorPicker, _vm.DeviceLightingColorHex);
            SetHexText(DeviceHexBox, _vm.DeviceLightingColorHex);
        }

        private void SyncKeyColorUi()
        {
            if (_vm is null) return;
            string? hex = _vm.SelectedLightingKey?.ColorHex;
            if (hex is null) return;
            SetPickerColor(KeyColorPicker, hex);
            SetHexText(KeyHexBox, hex);
        }

        private void SyncModeColorUi()
        {
            if (_vm is null) return;
            string? hex = _vm.SelectedModePowerModeColor?.ColorHex;
            if (hex is null) return;
            SetPickerColor(ModeColorPicker, hex);
            SetHexText(ModeHexBox, hex);
        }

        private static void SetPickerColor(Microsoft.UI.Xaml.Controls.Primitives.ColorSpectrum picker, string? hex)
        {
            Color color = LightingFormat.ParseHex(hex);
            if (picker.Color == color) return;
            picker.Color = color;
        }

        private static void SetHexText(TextBox box, string? hex)
        {
            if (string.IsNullOrEmpty(hex)) return;
            if (string.Equals(box.Text, hex, StringComparison.OrdinalIgnoreCase)) return;
            box.Text = hex;
        }

        private void ZoneColorPicker_ColorChanged(Microsoft.UI.Xaml.Controls.Primitives.ColorSpectrum sender, ColorChangedEventArgs args)
        {
            if (_syncingUi || _vm is null) return;
            string hex = LightingFormat.ToHex(args.NewColor);
            if (string.Equals(hex, _vm.SelectedHexColor, StringComparison.OrdinalIgnoreCase)) return;
            _vm.DiscardPendingZonePreset();
            _vm.SelectedHexColor = hex;
        }

        private void CoverColorPicker_ColorChanged(Microsoft.UI.Xaml.Controls.Primitives.ColorSpectrum sender, ColorChangedEventArgs args)
        {
            if (_syncingUi || _vm is null) return;
            string hex = LightingFormat.ToHex(args.NewColor);
            if (string.Equals(hex, _vm.CoverHexColor, StringComparison.OrdinalIgnoreCase)) return;
            _vm.CoverHexColor = hex;
        }

        private void DeviceColorPicker_ColorChanged(Microsoft.UI.Xaml.Controls.Primitives.ColorSpectrum sender, ColorChangedEventArgs args)
        {
            if (_syncingUi || _vm is null) return;
            string hex = LightingFormat.ToHex(args.NewColor);
            if (string.Equals(hex, _vm.DeviceLightingColorHex, StringComparison.OrdinalIgnoreCase)) return;
            _vm.DeviceLightingColorHex = hex;
        }

        private void ModeColorPicker_ColorChanged(Microsoft.UI.Xaml.Controls.Primitives.ColorSpectrum sender, ColorChangedEventArgs args)
        {
            if (_syncingUi || _vm?.SelectedModePowerModeColor is not { } editor) return;
            string hex = LightingFormat.ToHex(args.NewColor);
            if (string.Equals(hex, editor.ColorHex, StringComparison.OrdinalIgnoreCase)) return;
            editor.ColorHex = hex;
        }

        private void KeyColorPicker_ColorChanged(Microsoft.UI.Xaml.Controls.Primitives.ColorSpectrum sender, ColorChangedEventArgs args)
        {
            if (_syncingUi || _vm is null) return;
            if (!_vm.HasSelectedLightingKey) return;
            string hex = LightingFormat.ToHex(args.NewColor);
            if (_vm.SelectedLightingKey is { } key &&
                string.Equals(hex, key.ColorHex, StringComparison.OrdinalIgnoreCase) &&
                _vm.SelectedLightingKeys.Count <= 1)
                return;
            // Applies to every selected key, same entry point the WPF wheel used.
            _vm.SetSelectedLightingKeysColor(hex);
        }

        private void ZoneHexBox_TextChanged(object sender, TextChangedEventArgs e)
            => ApplyHexText(ZoneHexBox, hex => { _vm!.DiscardPendingZonePreset(); _vm.SelectedHexColor = hex; });

        private void CoverHexBox_TextChanged(object sender, TextChangedEventArgs e)
            => ApplyHexText(CoverHexBox, hex => _vm!.CoverHexColor = hex);

        private void DeviceHexBox_TextChanged(object sender, TextChangedEventArgs e)
            => ApplyHexText(DeviceHexBox, hex => _vm!.DeviceLightingColorHex = hex);

        private void ModeHexBox_TextChanged(object sender, TextChangedEventArgs e)
            => ApplyHexText(ModeHexBox, hex => { if (_vm?.SelectedModePowerModeColor is { } editor) editor.ColorHex = hex; });

        private void KeyHexBox_TextChanged(object sender, TextChangedEventArgs e)
            => ApplyHexText(KeyHexBox, hex => _vm!.SetSelectedLightingKeysColor(hex));

        private void ApplyHexText(TextBox box, Action<string> apply)
        {
            if (_syncingUi || _vm is null) return;
            string text = (box.Text ?? string.Empty).Trim();
            if (text.Length == 0) return;
            if (!text.StartsWith('#')) text = "#" + text;
            if (text.Length != 7 || !LightingFormat.IsHex(text)) return;
            apply(text);
        }

        // Typing an invalid hex leaves the box showing garbage; restore it on focus loss.
        private void ZoneHexBox_LostFocus(object sender, RoutedEventArgs e) => Sync(() => SyncZoneColorUi());
        private void CoverHexBox_LostFocus(object sender, RoutedEventArgs e) => Sync(() => SyncCoverColorUi());

        // ── Combo boxes ─────────────────────────────────────────────────────────
        private void LightingModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingUi || _vm is null) return;
            UpdateLegacyModePanels();
            switch (LightingModeCombo.SelectedIndex)
            {
                case 0:
                    _vm.SetLightingEffect("HW:0");
                    break;
                case 1:
                    if (DynamicEffectCombo.SelectedItem is LightingOption dynamic)
                        _vm.SetLightingEffect(dynamic.Tag);
                    break;
                case 2:
                    if (SoftwareEffectCombo.SelectedItem is LightingOption software)
                        _vm.SetLightingEffect(software.Tag);
                    break;
            }
        }

        private void DynamicEffectCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingUi || _vm is null) return;
            if (DynamicEffectCombo.SelectedItem is LightingOption option) _vm.SetLightingEffect(option.Tag);
        }

        private void SoftwareEffectCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingUi || _vm is null) return;
            if (SoftwareEffectCombo.SelectedItem is LightingOption option) _vm.SetLightingEffect(option.Tag);
        }

        private void StaticPresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingUi || _vm is null) return;
            if (StaticPresetCombo.SelectedItem is not LightingOption option) return;
            if (option.Tag == "Custom")
            {
                _vm.DiscardPendingZonePreset();
                return;
            }
            _vm.ApplyZonePreset(option.Tag);
        }

        private void CoverModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingUi || _vm is null) return;
            if (CoverModeCombo.SelectedItem is not LightingOption option) return;
            if (!int.TryParse(option.Tag, out int mode)) return;
            _vm.CoverLightingMode = Math.Clamp(mode, 1, 3);
            UpdateCoverPanels();
        }

        private void CoverPresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingUi || _vm is null) return;
            if (CoverPresetCombo.SelectedItem is not LightingOption option) return;
            if (option.Tag == "Custom") return;
            _vm.ApplyCoverPreset(option.Tag);
        }

        private void DeviceModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingUi || _vm is null) return;
            if (DeviceModeCombo.SelectedItem is not LightingModeInfo mode) return;
            _vm.SelectedLightingDeviceMode = mode;
        }

        private void SyncStaticPresetCombo()
        {
            if (_vm is null) return;
            LightingOption? match = ZonePresetOptions.FirstOrDefault(
                option => string.Equals(option.Tag, _vm.SelectedZonePreset, StringComparison.OrdinalIgnoreCase))
                ?? ZonePresetOptions[0];
            if (!ReferenceEquals(StaticPresetCombo.SelectedItem, match)) StaticPresetCombo.SelectedItem = match;
        }

        private void SyncCoverModeCombo()
        {
            if (_vm is null) return;
            LightingOption match = CoverModeOptions.FirstOrDefault(
                option => option.Tag == _vm.CoverLightingMode.ToString()) ?? CoverModeOptions[0];
            if (!ReferenceEquals(CoverModeCombo.SelectedItem, match)) CoverModeCombo.SelectedItem = match;
        }

        private void SyncDeviceModeCombo()
        {
            if (_vm is null) return;
            LightingModeInfo? mode = _vm.SelectedLightingDeviceMode;
            if (!ReferenceEquals(DeviceModeCombo.SelectedItem, mode)) DeviceModeCombo.SelectedItem = mode;
        }

        /// <summary>Mirrors the WPF RestoreLightingModeUi: effect tag decides the mode UI.</summary>
        private void ApplyLightingModeUi(string? effectTag)
        {
            if (_vm is null) return;
            string tag = string.IsNullOrWhiteSpace(effectTag) ? "HW:0" : effectTag;
            bool software = tag.StartsWith("DYN:", StringComparison.Ordinal);
            bool hardwareDynamic = tag.StartsWith("HW:", StringComparison.Ordinal) && tag != "HW:0";

            int index = software ? 2 : hardwareDynamic ? 1 : 0;
            if (LightingModeCombo.SelectedIndex != index) LightingModeCombo.SelectedIndex = index;

            if (hardwareDynamic)
            {
                LightingOption? option = HardwareEffectOptions.FirstOrDefault(o => o.Tag == tag);
                if (option is not null && !ReferenceEquals(DynamicEffectCombo.SelectedItem, option))
                    DynamicEffectCombo.SelectedItem = option;
            }
            else if (software)
            {
                LightingOption? option = SoftwareEffectOptions.FirstOrDefault(o => o.Tag == tag);
                if (option is not null && !ReferenceEquals(SoftwareEffectCombo.SelectedItem, option))
                    SoftwareEffectCombo.SelectedItem = option;
            }

            UpdateLegacyModePanels();
            SyncStaticPresetCombo();
        }

        /// <summary>Static panels follow the mode combo; presets/zones hide while sync-all is on.</summary>
        private void UpdateLegacyModePanels()
        {
            if (_vm is null) return;
            int index = LightingModeCombo.SelectedIndex < 0 ? 0 : LightingModeCombo.SelectedIndex;
            PanelStaticControls.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
            PanelDynamicControls.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
            PanelSoftwareControls.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;

            bool independentZones = index == 0 && !_vm.IsRgbSyncZonesEnabled;
            StaticPresetRow.Visibility = independentZones ? Visibility.Visible : Visibility.Collapsed;
            ZoneButtonRow.Visibility = independentZones ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateCoverPanels()
        {
            if (_vm is null) return;
            bool sync = _vm.IsCoverSyncWithKeyboard;
            bool isStatic = _vm.CoverLightingMode <= 1;
            CoverBrightnessCard.Visibility = sync ? Visibility.Collapsed : Visibility.Visible;
            CoverEffectModeCard.Visibility = sync ? Visibility.Collapsed : Visibility.Visible;
            CoverStaticPanel.Visibility = (!sync && isStatic) ? Visibility.Visible : Visibility.Collapsed;
            CoverSpeedCard.Visibility = (!sync && !isStatic) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateDevicePanels()
        {
            if (_vm is null) return;
            ModePowerColorPanel.Visibility = _vm.ShowModePowerModeColors ? Visibility.Visible : Visibility.Collapsed;
            DeviceColorPanel.Visibility = _vm.ShowDeviceColorFields ? Visibility.Visible : Visibility.Collapsed;
            KeyboardLayoutPanel.Visibility = _vm.ShowKeyboardLayout ? Visibility.Visible : Visibility.Collapsed;
            LedList.Visibility = _vm.ShowIndividualLedGrid ? Visibility.Visible : Visibility.Collapsed;
            IndividualKeyPanel.Visibility = _vm.ShowIndividualLightingEditors ? Visibility.Visible : Visibility.Collapsed;
        }

        // ── Lists / zone buttons ────────────────────────────────────────────────
        private void SyncLegacyTargetTabs()
        {
            if (_vm is null) return;
            KeyboardTabButton.IsChecked = _vm.IsKeyboardTabSelected;
            LogoTabButton.IsChecked = _vm.IsCoverTabSelected;
        }

        private void SyncDeviceList()
        {
            if (_vm is null) return;
            if (!ReferenceEquals(DeviceList.SelectedItem, _vm.SelectedLightingDevice))
                DeviceList.SelectedItem = _vm.SelectedLightingDevice;
        }

        private void SyncLedListSelection()
        {
            if (_vm is null) return;
            if (!ReferenceEquals(LedList.SelectedItem, _vm.SelectedLightingKey))
                LedList.SelectedItem = _vm.SelectedLightingKey;
        }

        private void RefreshDevices_Click(object sender, RoutedEventArgs e)
        {
            if (_vm is null) return;
            _ = _vm.DetectLightingDevicesAsync();
        }

        /// <summary>
        /// L5: with exactly one device there is nothing to choose — the list becomes a plain
        /// text row (name here, the 11 Muted description line right below it) and the chip
        /// list only shows up when there are two or more devices.
        /// </summary>
        public Visibility DeviceListVisibility
            => (_vm?.LightingDevices.Count ?? 0) > 1 ? Visibility.Visible : Visibility.Collapsed;

        public Visibility SingleDeviceVisibility
            => (_vm?.LightingDevices.Count ?? 0) == 1 ? Visibility.Visible : Visibility.Collapsed;

        public string SingleDeviceName
            => _vm?.LightingDevices.Count == 1 ? Loc.T(_vm.LightingDevices[0].DisplayName) : string.Empty;

        private void RaiseDeviceRowState()
        {
            // Compiled bindings on this page are pushed with Bindings.Update() (the page does
            // not implement INotifyPropertyChanged); this keeps the L5 row in sync.
            try { Bindings.Update(); } catch { /* not live yet */ }
        }

        private void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingUi || _vm is null) return;
            if (DeviceList.SelectedItem is LightingDeviceInfo device) _vm.SelectedLightingDevice = device;
        }

        private void LedList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingUi || _vm is null) return;
            if (LedList.SelectedItem is LightingLedEditor editor) _vm.SelectedLightingKey = editor;
        }

        private void ModeColorButton_Click(object sender, RoutedEventArgs e)
        {
            if (_vm is null) return;
            if ((sender as FrameworkElement)?.DataContext is ModePowerModeColorEditor editor)
                _vm.SelectModePowerModeColor(editor);
        }

        private void KeyboardTab_Click(object sender, RoutedEventArgs e)
        {
            if (_vm is null) return;
            _vm.SelectedLightingTab = 0;
        }

        private void LogoTab_Click(object sender, RoutedEventArgs e)
        {
            if (_vm is null) return;
            _vm.SelectedLightingTab = 1;
        }

        private void Zone1_Click(object sender, RoutedEventArgs e) => SelectZone(1);
        private void Zone2_Click(object sender, RoutedEventArgs e) => SelectZone(2);
        private void Zone3_Click(object sender, RoutedEventArgs e) => SelectZone(3);
        private void Zone4_Click(object sender, RoutedEventArgs e) => SelectZone(4);

        private void SelectZone(int zone)
        {
            if (_vm is null) return;
            _vm.SelectedRgbZone = zone;
        }

        // ── Apply / Reset ───────────────────────────────────────────────────────
        /// <summary>
        /// Persists the editor state to preysense.json through the ViewModel's existing queue.
        /// It does NOT push a colour frame: SaveLightingSettings() only carries LightingWork.Save,
        /// so clicking Apply is safe on a machine whose EC must not be written from a test run.
        /// (Edits themselves still live-commit through the VM debouncer, exactly like WPF.)
        /// </summary>
        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            if (_vm is null) return;
            _vm.SaveLightingSettings();
            ShowStatus(Loc.T("Saved."));
        }

        /// <summary>Destructive: restores the editor defaults, after a confirmation dialog.</summary>
        private async void Reset_Click(object sender, RoutedEventArgs e)
        {
            if (_vm is null) return;

            var dialog = new ContentDialog
            {
                Title = Loc.T("Reset lighting settings"),
                Content = Loc.T("Restore the built-in keyboard and logo lighting to their defaults?"),
                PrimaryButtonText = Loc.T("Reset"),
                CloseButtonText = Loc.T("Cancel"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };

            ContentDialogResult result;
            try { result = await dialog.ShowAsync(); }
            catch { return; }
            if (result != ContentDialogResult.Primary) return;

            _syncingUi = true;
            try
            {
                _vm.IsRgbSyncZonesEnabled = false;
                _vm.RgbBrightness = 100;
                _vm.RgbSpeed = 50;
                _vm.KbdTimeoutSeconds = 30;
                _vm.SetLightingEffect("HW:0");
                _vm.ApplyZonePreset("Predator");
                _vm.IsCoverSyncWithKeyboard = false;
                _vm.CoverLightingMode = 1;
                _vm.CoverBrightness = 100;
                _vm.CoverSpeed = 50;
                _vm.CoverHexColor = "#3B82F6";
            }
            finally { _syncingUi = false; }

            SyncFromVm();
            ShowStatus(Loc.T("Reset to defaults"));
        }

        private void ShowStatus(string message)
        {
            StatusText.Text = message;
            StatusText.Visibility = Visibility.Visible;
            _statusTimer?.Stop();
            _statusTimer = _dispatcher.CreateTimer();
            _statusTimer.Interval = StatusLifetime;
            _statusTimer.IsRepeating = false;
            _statusTimer.Tick += (timer, _) =>
            {
                timer.Stop();
                StatusText.Visibility = Visibility.Collapsed;
            };
            _statusTimer.Start();
        }

        // ── Localised option tables (mirror the WPF ComboBoxItems) ──────────────
        public IReadOnlyList<LightingOption> ZonePresetOptions { get; } = new[]
        {
            new LightingOption("Custom", Loc.T("Custom")),
            new LightingOption("Predator", Loc.T("Predator")),
            new LightingOption("Cyberpunk", Loc.T("Cyberpunk")),
            new LightingOption("FireIce", Loc.T("Fire & Ice")),
            new LightingOption("Matrix", Loc.T("Matrix")),
            new LightingOption("Stealth", Loc.T("Stealth")),
            new LightingOption("Sunset", Loc.T("Sunset")),
            new LightingOption("Ocean", Loc.T("Ocean")),
            new LightingOption("Ember", Loc.T("Ember")),
            new LightingOption("Vaporwave", Loc.T("Vaporwave")),
            new LightingOption("Sakura", Loc.T("Sakura")),
            new LightingOption("Rainbow", Loc.T("Rainbow")),
            new LightingOption("Toxic", Loc.T("Toxic")),
            new LightingOption("Midnight", Loc.T("Midnight")),
            new LightingOption("Dracula", Loc.T("Dracula")),
            new LightingOption("NeonHaze", Loc.T("Neon Haze")),
            new LightingOption("DeepSpace", Loc.T("Deep Space")),
            new LightingOption("Inferno", Loc.T("Inferno")),
            new LightingOption("CyberStorm", Loc.T("Cyber Storm")),
        };

        /// <summary>The seven EC hardware animation patterns (WPF tags HW:1..HW:7).</summary>
        public IReadOnlyList<LightingOption> HardwareEffectOptions { get; } = new[]
        {
            new LightingOption("HW:1", Loc.T("Breathing")),
            new LightingOption("HW:2", Loc.T("Neon Wave")),
            new LightingOption("HW:3", Loc.T("Color Wave")),
            new LightingOption("HW:4", Loc.T("Ripple")),
            new LightingOption("HW:5", Loc.T("Zoom")),
            new LightingOption("HW:6", Loc.T("Snake")),
            new LightingOption("HW:7", Loc.T("Disco")),
        };

        /// <summary>Software animation engine effects (WPF tags DYN:&lt;CustomEffectMode&gt;).</summary>
        public IReadOnlyList<LightingOption> SoftwareEffectOptions { get; } = new[]
        {
            new LightingOption("DYN:RainbowCycle", Loc.T("Rainbow Cycle")),
            new LightingOption("DYN:PoliceStrobe", Loc.T("Police Strobe")),
            new LightingOption("DYN:FireEmber", Loc.T("Fire Ember")),
            new LightingOption("DYN:MatrixCascade", Loc.T("Matrix Cascade")),
            new LightingOption("DYN:CylonScanner", Loc.T("Cylon Scanner")),
            new LightingOption("DYN:AuroraDrift", Loc.T("Aurora Drift")),
            new LightingOption("DYN:Heartbeat", Loc.T("Heartbeat")),
            new LightingOption("DYN:LightningStorm", Loc.T("Lightning Storm")),
            new LightingOption("DYN:Starlight", Loc.T("Starlight")),
            new LightingOption("DYN:NeonPulse", Loc.T("Neon Pulse")),
            new LightingOption("DYN:PlasmaOrb", Loc.T("Plasma Orb")),
            new LightingOption("DYN:GlitchStorm", Loc.T("Glitch Storm")),
            new LightingOption("DYN:BioHazard", Loc.T("Bio Hazard")),
            new LightingOption("DYN:SolarFlare", Loc.T("Solar Flare")),
        };

        public IReadOnlyList<LightingOption> CoverModeOptions { get; } = new[]
        {
            new LightingOption("1", Loc.T("Static")),
            new LightingOption("2", Loc.T("Breathing")),
            new LightingOption("3", Loc.T("Neon")),
        };

        public IReadOnlyList<LightingOption> CoverPresetOptions { get; } = new[]
        {
            new LightingOption("Custom", Loc.T("Custom")),
            new LightingOption("Predator", Loc.T("Predator")),
            new LightingOption("Fire", Loc.T("Hellfire Red")),
            new LightingOption("Amber", Loc.T("Tactical Amber")),
            new LightingOption("Neon", Loc.T("Cyber Neon")),
            new LightingOption("Matrix", Loc.T("Matrix Green")),
            new LightingOption("White", Loc.T("Arctic White")),
            new LightingOption("Purple", Loc.T("Deep Violet")),
            new LightingOption("Sunset", Loc.T("Sunset Orange")),
            new LightingOption("Ocean", Loc.T("Ocean Blue")),
            new LightingOption("Sakura", Loc.T("Sakura Pink")),
            new LightingOption("Toxic", Loc.T("Toxic Lime")),
            new LightingOption("Lava", Loc.T("Lava Orange")),
            new LightingOption("Midnight", Loc.T("Midnight Indigo")),
            new LightingOption("Ice", Loc.T("Ice Blue")),
            new LightingOption("Vapor", Loc.T("Vaporwave Pink")),
        };
    }

    /// <summary>One ComboBox row: stable tag sent to the ViewModel + localised label.</summary>
    public sealed record LightingOption(string Tag, string DisplayName);

    /// <summary>
    /// x:Bind helpers (WINUI3-MIGRATION.md §3.9: "HexRgbColorConverter -> static functions",
    /// because x:Bind cannot use IValueConverter).
    /// </summary>
    public static class LightingFormat
    {
        private static readonly SolidColorBrush Transparent = new(Colors.Transparent);
        private static readonly SolidColorBrush Accent = new(Color.FromArgb(0xFF, 0x3B, 0x82, 0xF6));

        public static Brush HexToBrush(string? hex) => new SolidColorBrush(ParseHex(hex));

        /// <summary>Accent outline for the selected RGB zone, transparent for the others.</summary>
        public static Brush ZoneOutlineBrush(int selectedZone, int zone)
            => selectedZone == zone ? Accent : Transparent;

        // ── L1: zone buttons show SELECTION, not the zone's colour ────────────────
        // Before, all four buttons were painted with the zone colours (blue by default), so
        // the four looked identical and the accent outline was the only hint. Now selected =
        // accent fill + Primary label, unselected = neutral fill + Secondary label, and the
        // "sync all zones" switch selects all four at once.
        private static readonly SolidColorBrush ZoneIdleFill = new(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        private static readonly SolidColorBrush ZonePrimaryText = new(Color.FromArgb(0xFF, 0xF2, 0xF2, 0xF2));
        private static readonly SolidColorBrush ZoneSecondaryText = new(Color.FromArgb(0xFF, 0xA8, 0xA8, 0xBC));

        public static Brush ZoneBackground(int selectedZone, int zone, bool syncAll)
            => syncAll || selectedZone == zone ? Accent : ZoneIdleFill;

        public static Brush ZoneForeground(int selectedZone, int zone, bool syncAll)
            => syncAll || selectedZone == zone ? ZonePrimaryText : ZoneSecondaryText;

        public static string Percent(int value) => $"{value}%";

        public static string Text(int value) => value.ToString();

        /// <summary>EC brightness level 1..4 shown next to the % value (0 % = Off).</summary>
        public static string BrightnessLevel(int percent)
            => percent <= 0 ? Predex.WinUI.Loc.T("Off") : $"{Math.Clamp((percent + 24) / 25, 1, 4)}/4";

        /// <summary>
        /// Tooltip for a greyed-out capability: returns null when the control is usable, so
        /// the enabled state does not carry a pointless tooltip (rule: grey out + explain,
        /// never hide).
        /// </summary>
        public static object? DisabledTip(bool available, string englishHint)
            => available ? null : Predex.WinUI.Loc.T(englishHint);

        public static bool IsHex(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return false;
            string value = hex.Trim().TrimStart('#');
            if (value.Length != 6) return false;
            return value.All(Uri.IsHexDigit);
        }

        public static Color ParseHex(string? hex) => ParseHex(hex, Color.FromArgb(0xFF, 0x3B, 0x82, 0xF6));

        public static Color ParseHex(string? hex, Color fallback)
        {
            if (!IsHex(hex)) return fallback;
            string value = hex!.Trim().TrimStart('#');
            try
            {
                return Color.FromArgb(0xFF,
                    Convert.ToByte(value[..2], 16),
                    Convert.ToByte(value.Substring(2, 2), 16),
                    Convert.ToByte(value.Substring(4, 2), 16));
            }
            catch { return fallback; }
        }

        public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }
}
