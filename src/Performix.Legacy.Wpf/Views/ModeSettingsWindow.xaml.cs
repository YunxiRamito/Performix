using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using PreySense.Core.Driver;
using PreySense.Frontend.Controls;
using PreySense.Frontend.ViewModels;
using PreySense.Helpers;

namespace PreySense.Frontend.Views
{
    public partial class ModeSettingsWindow : Window
    {
        private int _selectedModeIndex = 2; // 0=Eco, 1=Quiet, 2=Balanced, 3=Perf, 4=Turbo
        private bool _isUpdatingUi;
        private readonly Dictionary<int, PreySense.Core.Configuration.ModeTuningConfig> _drafts = new();
        private PreySense.Core.Configuration.ModeTuningConfig Draft => _drafts[_selectedModeIndex];
        private readonly System.Windows.Threading.DispatcherTimer _verificationTimer = new()
        {
            Interval = TimeSpan.FromSeconds(3)
        };

        private readonly System.Windows.Threading.DispatcherTimer _statusTimer = new()
        {
            Interval = TimeSpan.FromSeconds(1)
        };

        public ModeSettingsWindow()
        {
            InitializeComponent();
            SliderGpuDelta.PreviewMouseLeftButtonUp += (_, _) => CommitSlider(SliderGpuDelta);
            SliderGpuDynamicBoost.PreviewMouseLeftButtonUp += (_, _) => CommitSlider(SliderGpuDynamicBoost);
            SliderPlatformPowerOffset.PreviewMouseLeftButtonUp += (_, _) => CommitSlider(SliderPlatformPowerOffset);
            SliderTau.PreviewMouseLeftButtonUp += (_, _) => CommitSlider(SliderTau);
            _verificationTimer.Tick += (_, _) =>
            {
                _verificationTimer.Stop();
                SetVerificationText(string.Empty);
            };
            MaxHeight = SystemParameters.WorkArea.Height - 40;
            TuningScroll.MaxHeight = Math.Max(1, MaxHeight - 292);
            Loaded += ModeSettingsWindow_Loaded;
            foreach (string name in MainViewModel.CpuBoostModeNames)
                DropdownCpuBoost.Items.Add(name);
            DropdownCpuBoost.SelectionChanged += CpuBoostDropdown_Changed;
            Closed += (_, _) =>
            {
                _verificationTimer.Stop();
                _statusTimer.Stop();
                if (DataContext is MainViewModel vm)
                    vm.PropertyChanged -= OnTuningVmPropertyChanged;
                DropdownCpuBoost.SelectionChanged -= CpuBoostDropdown_Changed;
            };
            _statusTimer.Tick += (_, _) => UpdateStatusBar();
        }

        /// <summary>Live status bar (pipe telemetry): CPU/GPU °C and true fan RPM.</summary>
        private void UpdateStatusBar()
        {
            if (StatusCpuTemp is null) return;
            var snapshot = PreySense.Core.Services.TelemetryCoordinator.CurrentSnapshot;
            StatusCpuTemp.Text = snapshot.CpuTemp is > 0 and int cpu ? $"{cpu} °C" : "N/A";
            StatusGpuTemp.Text = snapshot.GpuTemp is > 0 and int gpu ? $"{gpu} °C" : "N/A";
            StatusCpuFan.Text = snapshot.CpuRpm is > 0 and int cpuFan ? $"{cpuFan} RPM" : "N/A";
            StatusGpuFan.Text = snapshot.GpuRpm is > 0 and int gpuFan ? $"{gpuFan} RPM" : "N/A";
        }

        private void ModeButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button { Tag: string tag } || !int.TryParse(tag, out int index)) return;
            if (ComboModeSelect.SelectedIndex == index) return;
            ComboModeSelect.SelectedIndex = index;
        }

        private void UpdateModeButtons()
        {
            var selected = (Brush)(TryFindResource("AccentBrush") ?? Brushes.DodgerBlue);
            var normal = (Brush)(TryFindResource("ControlBorderBrush") ?? Brushes.Transparent);
            foreach (var (button, index) in new (System.Windows.Controls.Button, int)[]
                     {
                         (BtnModeEco, 0), (BtnModeQuiet, 1), (BtnModeBalanced, 2), (BtnModePerformance, 3), (BtnModeTurbo, 4)
                     })
            {
                bool isSelected = index == _selectedModeIndex;
                button.BorderBrush = isSelected ? selected : normal;
                button.BorderThickness = new Thickness(isSelected ? 2 : 1);
                button.FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }

        private async void ApplyGpuClock_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await ApplySelectedModeAsync(persist: false);
            }
            catch (Exception ex)
            {
                ReportTuningError("Apply GPU clock offsets", ex);
            }
        }

        /// <summary>
        /// Logs an unexpected failure and surfaces it in the window instead of letting the
        /// exception reach the global handler and close the app.
        /// </summary>
        private void ReportTuningError(string action, Exception ex)
        {
            AppLogger.Error($"{action} failed: {ex}");
            string message = Services.LocalizationService.Format("Failed: {0}", ex.Message);
            SetVerificationText(message, TryFindResource("WarningTextBrush") as Brush ?? Brushes.OrangeRed);
        }

        private void OpenFanCurveEditor_Click(object sender, RoutedEventArgs e)
        {
            var window = new FanCurveWindow
            {
                DataContext = DataContext,
                Owner = this
            };
            window.Show();
        }

        private void CpuScheduleTextBox_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_isUpdatingUi || SliderCpuMaxState is null) return;
            _isUpdatingUi = true;
            try
            {
                if (ReferenceEquals(sender, BoxCpuMaxState) && int.TryParse(BoxCpuMaxState.Text, out int max))
                    SliderCpuMaxState.Value = Math.Clamp(max, 0, 100);
                else if (ReferenceEquals(sender, BoxCpuEpp) && int.TryParse(BoxCpuEpp.Text, out int epp))
                    SliderCpuEpp.Value = Math.Clamp(epp, 0, 100);
                else if (ReferenceEquals(sender, BoxCpuCoreMin) && int.TryParse(BoxCpuCoreMin.Text, out int min))
                    SliderCpuCoreMin.Value = Math.Clamp(min, 0, 100);
                else if (ReferenceEquals(sender, BoxCpuCoreMax) && int.TryParse(BoxCpuCoreMax.Text, out int maxCores))
                    SliderCpuCoreMax.Value = Math.Clamp(maxCores, 0, 100);
            }
            finally
            {
                _isUpdatingUi = false;
            }
        }

        private void SetVerificationText(string text, Brush? foreground = null)
        {
            bool hasText = !string.IsNullOrWhiteSpace(text);
            TuningVerificationText.Text = hasText ? text : string.Empty;
            if (foreground is not null)
                TuningVerificationText.Foreground = foreground;
            TuningVerificationText.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
            TuningVerificationText.Margin = hasText ? new Thickness(14, 8, 14, 4) : new Thickness(14, 0, 14, 0);
        }

        private async void ModeSettingsWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (DataContext is MainViewModel vm)
                {
                    vm.PropertyChanged -= OnTuningVmPropertyChanged;
                    vm.PropertyChanged += OnTuningVmPropertyChanged;
                    await System.Threading.Tasks.Task.WhenAll(vm.RefreshProchotLockStateAsync(), vm.RefreshGpuClockTuningAsync());
                }
                SyncFromViewModel();
                UpdateStatusBar();
                _statusTimer.Start();
            }
            catch (Exception ex)
            {
                ReportTuningError("Load tuning window", ex);
            }
        }

        private void OnTuningVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.IsHardwareTuningEnabled) && IsLoaded)
                LoadModeData();
            else if (e.PropertyName == nameof(MainViewModel.IsProchotOffsetEnabled) && !_isUpdatingUi)
            {
                if (SwitchProchotOffset != null &&
                    SwitchProchotOffset.IsChecked != (DataContext as MainViewModel)?.IsProchotOffsetEnabled)
                {
                    _isUpdatingUi = true;
                    SwitchProchotOffset.IsChecked = (DataContext as MainViewModel)?.IsProchotOffsetEnabled == true;
                    _isUpdatingUi = false;
                }
                UpdateActionButtonsEnabled();
            }
        }

        internal void SyncFromViewModel()
        {
            if (DataContext is MainViewModel vm)
            {
                _selectedModeIndex = vm.ActivePowerMode switch
                {
                    0x06 => 0,
                    0x00 => 1,
                    0x04 => 3,
                    0x05 => 4,
                    _ => 2
                };
            }

            RowProchotOffset.Value = (DataContext as MainViewModel)?.TuningEditorProchotOffset is int offset ? offset : double.NaN;
            LoadModeData();
        }

        private void ComboModeSelect_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ComboModeSelect == null || _isUpdatingUi || !IsLoaded || ComboModeSelect.SelectedIndex < 0) return;
            _selectedModeIndex = ComboModeSelect.SelectedIndex;
            LoadModeData();
        }

        private static string FormatTau(int seconds)
        {
            string text = CpuPowerLimitConfiguration.FormatTauSelection(seconds);
            return text is "Off" ? Services.LocalizationService.Translate(text) : text;
        }

        private int ReadTauSeconds() =>
            CpuPowerLimitConfiguration.TauSecondsForIndex((int)Math.Round(SliderTau?.Value ?? 0));

        private string GetSelectedModeName() => _selectedModeIndex switch
        {
            0 => "Eco",
            1 => "Quiet",
            2 => "Balanced",
            3 => "Performance",
            4 => "Turbo",
            _ => "Custom"
        };

        private void LoadModeData()
        {
            if (DataContext is not MainViewModel vm) return;
            _isUpdatingUi = true;
            if (!_drafts.ContainsKey(_selectedModeIndex)) _drafts[_selectedModeIndex] = vm.TuningEditorMode(_selectedModeIndex);
            SetVerificationText(string.Empty);

            if (ComboModeSelect != null && ComboModeSelect.SelectedIndex != _selectedModeIndex)
            {
                ComboModeSelect.SelectedIndex = _selectedModeIndex;
            }

            int pl1 = Draft.CpuPl1Watts ?? 0;
            int pl2 = Draft.CpuPl2Watts ?? 0;
            int limitedPl1 = vm.LimitPl1ToPlatform(pl1);
            WriteClampedLimitsToEditor(limitedPl1, vm.LimitPl2(limitedPl1, pl2));

            if (SliderTau != null)
            {
                int tau = CpuPowerLimitConfiguration.SnapTauSelection(Draft.TauSeconds ?? 0);
                SliderTau.Maximum = CpuPowerLimitConfiguration.TauSliderMaximum;
                SliderTau.Value = CpuPowerLimitConfiguration.TauStepIndex(tau);
                if (LabelTau != null) LabelTau.Text = FormatTau(tau);
            }

            if (DropdownCpuBoost != null)
                DropdownCpuBoost.SelectedIndex = Math.Clamp(Draft.EffectiveCpuBoostMode ?? vm.CpuBoostForModeIndex(_selectedModeIndex), 0, 6);

            SliderGpuDelta.Value = Draft.GpuDeltaWatts ?? 0;
            SliderGpuDynamicBoost.Value = Draft.GpuDynamicBoostWatts ?? 0;
            SliderPlatformPowerOffset.Value = Draft.PlatformPowerOffsetWatts ?? 0;
            if (!Draft.CpuPl1Watts.HasValue) RowPl1.Value = double.NaN;
            if (!Draft.CpuPl2Watts.HasValue) RowPl2.Value = double.NaN;
            if (!Draft.TauSeconds.HasValue && LabelTau is not null) LabelTau.Text = string.Empty;
            if (RowProchotOffset != null)
                RowProchotOffset.Value = vm.TuningEditorProchotOffset is int offset ? offset : double.NaN;
            if (SwitchProchotOffset != null)
                SwitchProchotOffset.IsChecked = vm.IsProchotOffsetEnabled;
            if (SwitchApplyCpuLimits != null)
                SwitchApplyCpuLimits.IsChecked = Draft.ApplyCpuLimits;
            if (!vm.IsGpuProfileTuningAvailable)
                Draft.ApplyGpuLimits = false;
            if (SwitchApplyGpuLimits != null)
                SwitchApplyGpuLimits.IsChecked = Draft.ApplyGpuLimits && vm.IsGpuProfileTuningAvailable;
            LoadGpuClockRows(vm);
            LoadCpuSchedule(vm);
            UpdateModeButtons();
            if (FanPolicyText is not null)
                FanPolicyText.Text = vm.LoadFanProfile(ModeIndexToByte(_selectedModeIndex)).ControlMode.ToString();
            UpdateGpuDraftLabels();
            UpdateApplyLimitPanels();
            UpdateActionButtonsEnabled();
            _isUpdatingUi = false;
        }

        // ---- CPU scheduling (Windows power scheme / SUB_PROCESSOR) --------------------

        private static readonly string[] CpuBoostModeNames =
            ["Disabled", "Enabled", "Aggressive", "Efficient", "Aggressive At Guaranteed", "Enabled At Guaranteed", "Efficient At Guaranteed"];

        private void EnsureCpuBoostItems()
        {
            if (ComboBoostMode.Items.Count == 0)
                foreach (string name in CpuBoostModeNames) ComboBoostMode.Items.Add(name);
        }

        private bool CpuScheduleAcSelected => ComboCpuPowerSource?.SelectedIndex != 1;

        private void LoadCpuSchedule(MainViewModel vm)
        {
            if (SliderCpuMaxState is null) return;
            EnsureCpuBoostItems();
            var schedule = vm.CpuScheduleForMode(ModeIndexToByte(_selectedModeIndex));
            var values = schedule.For(CpuScheduleAcSelected);
            _isUpdatingUi = true;
            SliderCpuMaxState.Value = values.MaxProcessorState;
            SliderCpuEpp.Value = values.Epp;
            SliderCpuCoreMin.Value = values.CoreParkingMin;
            SliderCpuCoreMax.Value = values.CoreParkingMax;
            ComboBoostMode.SelectedIndex = Math.Clamp(values.BoostMode, 0, 6);
            BoxCpuCoreMin.Text = values.CoreParkingMin.ToString();
            BoxCpuCoreMax.Text = values.CoreParkingMax.ToString();
            _isUpdatingUi = false;
            UpdateCpuScheduleLabels();
        }

        private void UpdateCpuScheduleLabels()
        {
            if (BoxCpuMaxState is null) return;
            if (!_isUpdatingUi)
            {
                _isUpdatingUi = true;
                BoxCpuMaxState.Text = ((int)Math.Round(SliderCpuMaxState.Value)).ToString();
                BoxCpuEpp.Text = ((int)Math.Round(SliderCpuEpp.Value)).ToString();
                BoxCpuCoreMin.Text = ((int)Math.Round(SliderCpuCoreMin.Value)).ToString();
                BoxCpuCoreMax.Text = ((int)Math.Round(SliderCpuCoreMax.Value)).ToString();
                _isUpdatingUi = false;
            }
        }

        private void CpuSchedule_Changed(object sender, EventArgs e)
        {
            if (_isUpdatingUi || ComboCpuPowerSource is null) return;
            UpdateCpuScheduleLabels();
        }

        private PreySense.Mode.CpuScheduling.CpuScheduleValues ReadCpuScheduleValues()
        {
            double maxState = SliderCpuMaxState?.Value ?? 100;
            double epp = SliderCpuEpp?.Value ?? 0;
            int boost = Math.Clamp(ComboBoostMode?.SelectedIndex ?? 1, 0, 6);
            int minCores = (int)Math.Round(SliderCpuCoreMin?.Value ?? 100);
            int maxCores = (int)Math.Round(SliderCpuCoreMax?.Value ?? 100);
            return PreySense.Mode.CpuScheduling.CpuScheduleValues.Default(
                (int)Math.Round(maxState), boost, (int)Math.Round(epp),
                Math.Clamp(minCores, 0, 100), Math.Clamp(maxCores, 0, 100));
        }

        private async void ApplyCpuSchedule_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (DataContext is not MainViewModel vm) return;
                byte mode = ModeIndexToByte(_selectedModeIndex);
                var schedule = vm.CpuScheduleForMode(mode);
                var values = ReadCpuScheduleValues();
                if (CpuScheduleAcSelected) schedule.Ac = values; else schedule.Dc = values;
                vm.SaveCpuScheduleForMode(mode, schedule);
                var result = await vm.ApplyCpuScheduleForModeAsync(mode);
                SetVerificationText(
                    result.Success
                        ? Services.LocalizationService.Translate("CPU scheduling applied.")
                        : Services.LocalizationService.Translate(result.Status),
                    result.Success
                        ? TryFindResource("SuccessTextBrush") as Brush ?? Brushes.LightGreen
                        : TryFindResource("WarningTextBrush") as Brush ?? Brushes.OrangeRed);
                _verificationTimer.Stop();
                if (IsLoaded) _verificationTimer.Start();
            }
            catch (Exception ex)
            {
                ReportTuningError("Apply CPU scheduling", ex);
            }
        }

        private async void ResetCpuSchedule_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (DataContext is not MainViewModel vm) return;
                var result = await vm.ResetCpuScheduleToWindowsDefaultsAsync();
                LoadCpuSchedule(vm);
                SetVerificationText(
                    result.Success
                        ? Services.LocalizationService.Translate("CPU scheduling restored to Windows defaults.")
                        : Services.LocalizationService.Translate(result.Status),
                    result.Success
                        ? TryFindResource("SuccessTextBrush") as Brush ?? Brushes.LightGreen
                        : TryFindResource("WarningTextBrush") as Brush ?? Brushes.OrangeRed);
            }
            catch (Exception ex)
            {
                ReportTuningError("Restore CPU scheduling", ex);
            }
        }

        private void ApplyLimitsToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi || !_drafts.ContainsKey(_selectedModeIndex)) return;
            Draft.ApplyCpuLimits = SwitchApplyCpuLimits?.IsChecked == true;
            bool gpuOk = DataContext is MainViewModel { IsGpuProfileTuningAvailable: true };
            Draft.ApplyGpuLimits = gpuOk && SwitchApplyGpuLimits?.IsChecked == true;
            UpdateApplyLimitPanels();
            UpdateActionButtonsEnabled();
        }

        private void LoadGpuClockRows(MainViewModel vm)
        {
            var state = vm.GpuClockTuningState;
            bool available = HasSteppedClockRanges(state);

            // Firmware capabilities decide the block's visibility. GPU clock offsets are NVAPI-only,
            // so this block stays usable even when Hardware Tuning is off (no kernel driver needed).
            GpuOverclockingSection.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
            GpuOverclockingUnavailableHint.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
            GpuOverclockingUnavailableHint.Text = available
                ? string.Empty
                : Services.LocalizationService.Translate(vm.GpuClockTuningUnavailableReason);

            if (!available)
            {
                Draft.ApplyGpuClockOffsets = false;
                SwitchGpuOverclocking.IsChecked = false;
                SwitchGpuOverclocking.IsEnabled = false;
                GpuOverclockingPanel.IsEnabled = false;
                BtnResetGpuClocks.IsEnabled = false;
                RowGpuCoreClock.ValueText = Services.LocalizationService.Translate("N/A");
                RowGpuMemoryClock.ValueText = Services.LocalizationService.Translate("N/A");
                return;
            }

            SwitchGpuOverclocking.IsChecked = Draft.ApplyGpuClockOffsets;
            SwitchGpuOverclocking.IsEnabled = true;
            BtnResetGpuClocks.IsEnabled = true;

            int coreStep = PreySense.Gpu.GpuClockTuningState.CoreStepMhz;
            int memoryStep = PreySense.Gpu.GpuClockTuningState.MemoryStepMhz;
            var core = state!.Core.ForStep(coreStep);
            var memory = state.Memory.ForStep(memoryStep);
            RowGpuCoreClock.SmallChange = RowGpuCoreClock.LargeChange = RowGpuCoreClock.TickFrequency = coreStep;
            RowGpuMemoryClock.SmallChange = RowGpuMemoryClock.LargeChange = RowGpuMemoryClock.TickFrequency = memoryStep;
            RowGpuCoreClock.Minimum = Math.Max(0, core.MinimumMhz);
            RowGpuCoreClock.Maximum = Math.Min(PreySense.Gpu.GpuClockTuningState.MaximumCoreOffsetMhz, core.MaximumMhz);
            RowGpuMemoryClock.Minimum = Math.Max(0, memory.MinimumMhz);
            RowGpuMemoryClock.Maximum = memory.MaximumMhz;
            RowGpuCoreClock.Value = state.Core.Snap(Draft.GpuCoreClockOffsetMhz ?? state.Current.CoreMhz, coreStep);
            RowGpuMemoryClock.Value = state.Memory.Snap(Draft.GpuMemoryClockOffsetMhz ?? state.Current.MemoryMhz, memoryStep);
            RowGpuCoreClock.ValueText = $"{RowGpuCoreClock.Value:+0;-0;0} MHz";
            RowGpuMemoryClock.ValueText = $"{RowGpuMemoryClock.Value:+0;-0;0} MHz";
            GpuOverclockingPanel.IsEnabled = available && Draft.ApplyGpuClockOffsets;
        }

        private async void ResetGpuClocks_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm) return;
            BtnResetGpuClocks.IsEnabled = false;
            try
            {
                var result = await vm.ResetGpuClockOffsetsAsync();
                if (result.Success)
                {
                    if (_drafts.TryGetValue(_selectedModeIndex, out var draft))
                    {
                        draft.ApplyGpuClockOffsets = false;
                        draft.GpuCoreClockOffsetMhz = null;
                        draft.GpuMemoryClockOffsetMhz = null;
                    }
                    _isUpdatingUi = true;
                    SwitchGpuOverclocking.IsChecked = false;
                    _isUpdatingUi = false;
                    LoadGpuClockRows(vm);
                    SetVerificationText(
                        Services.LocalizationService.Translate("GPU clocks restored to 0/0 MHz."),
                        TryFindResource("SuccessTextBrush") as Brush ?? Brushes.LightGreen);
                    _verificationTimer.Stop();
                    if (IsLoaded) _verificationTimer.Start();
                }
                else
                {
                    SetVerificationText(
                        Services.LocalizationService.Translate(result.Status),
                        TryFindResource("WarningTextBrush") as Brush ?? Brushes.OrangeRed);
                }
            }
            catch (Exception ex)
            {
                ReportTuningError("Restore GPU clocks", ex);
            }
            finally
            {
                UpdateActionButtonsEnabled();
            }
        }

        private void GpuOverclockingToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi || !_drafts.ContainsKey(_selectedModeIndex)) return;
            Draft.ApplyGpuClockOffsets = SwitchGpuOverclocking?.IsChecked == true;
            GpuOverclockingPanel.IsEnabled = Draft.ApplyGpuClockOffsets &&
                DataContext is MainViewModel vm && HasSteppedClockRanges(vm.GpuClockTuningState);
            if (DataContext is MainViewModel clockVm && SwitchGpuOverclocking != null)
                SwitchGpuOverclocking.IsEnabled = HasSteppedClockRanges(clockVm.GpuClockTuningState) || Draft.ApplyGpuClockOffsets;
            UpdateActionButtonsEnabled();
        }

        private static bool HasSteppedClockRanges(PreySense.Gpu.GpuClockTuningState? state) => state is not null
            && state.Core.ForStep(PreySense.Gpu.GpuClockTuningState.CoreStepMhz) is var core && core.MinimumMhz <= core.MaximumMhz
            && state.Memory.ForStep(PreySense.Gpu.GpuClockTuningState.MemoryStepMhz) is var memory && memory.MinimumMhz <= memory.MaximumMhz;

        private void GpuClockOffset_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingUi || !_drafts.ContainsKey(_selectedModeIndex)) return;
            StoreGpuClockSliders();
        }

        private void StoreGpuClockSliders()
        {
            if (DataContext is not MainViewModel vm || !HasSteppedClockRanges(vm.GpuClockTuningState)) return;
            var state = vm.GpuClockTuningState!;
            Draft.GpuCoreClockOffsetMhz = state.Core.Snap((int)Math.Round(RowGpuCoreClock.Value), PreySense.Gpu.GpuClockTuningState.CoreStepMhz);
            Draft.GpuMemoryClockOffsetMhz = state.Memory.Snap((int)Math.Round(RowGpuMemoryClock.Value), PreySense.Gpu.GpuClockTuningState.MemoryStepMhz);
            RowGpuCoreClock.ValueText = $"{Draft.GpuCoreClockOffsetMhz:+0;-0;0} MHz";
            RowGpuMemoryClock.ValueText = $"{Draft.GpuMemoryClockOffsetMhz:+0;-0;0} MHz";
        }

        private void ProchotOffsetToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi || DataContext is not MainViewModel vm) return;
            bool enabled = SwitchProchotOffset?.IsChecked == true;
            vm.IsProchotOffsetEnabled = enabled;
            if (enabled && vm.ProchotOffsetCelsius is null)
                RowProchotOffset_LostFocus(RowProchotOffset, new RoutedEventArgs());
            UpdateActionButtonsEnabled();
        }

        private void UpdateApplyLimitPanels()
        {
            if (CpuLimitsPanel != null)
                CpuLimitsPanel.IsEnabled = SwitchApplyCpuLimits?.IsChecked == true;
            bool gpuAvailable = DataContext is MainViewModel { IsGpuProfileTuningAvailable: true };
            if (GpuLimitsPanel != null)
                GpuLimitsPanel.IsEnabled = gpuAvailable && SwitchApplyGpuLimits?.IsChecked == true;
        }

        private void UpdateActionButtonsEnabled()
        {
            bool anyLimits = SwitchApplyCpuLimits?.IsChecked == true || SwitchApplyGpuLimits?.IsChecked == true;
            bool prochotOnly = SwitchProchotOffset?.IsChecked == true;
            bool canAct = anyLimits || prochotOnly || SwitchGpuOverclocking?.IsChecked == true;
            if (BtnApply != null) BtnApply.IsEnabled = canAct;
            if (BtnSave != null) BtnSave.IsEnabled = true;
            if (BtnReset != null) BtnReset.IsEnabled = canAct;
        }

        private void CommitSlider(System.Windows.Controls.Slider slider)
        {
            if (_isUpdatingUi || !_drafts.ContainsKey(_selectedModeIndex)) return;
            if (ReferenceEquals(slider, SliderTau))
            {
                Draft.TauSeconds = ReadTauSeconds(); LabelTau.Text = FormatTau(Draft.TauSeconds.Value);
            }
            else
            {
                if (ReferenceEquals(slider, SliderGpuDelta)) Draft.GpuDeltaWatts = SnapGpuDelta(slider.Value);
                else if (ReferenceEquals(slider, SliderGpuDynamicBoost)) Draft.GpuDynamicBoostWatts = SnapGpuDynamicBoost(slider.Value);
                else Draft.PlatformPowerOffsetWatts = SnapPlatformPowerOffset(slider.Value);
                UpdateGpuDraftLabels();
            }
        }

        private void UpdateGpuDraftLabels()
        {
            LabelGpuDelta.Text = Draft.GpuDeltaWatts.HasValue ? $"{Draft.GpuDeltaWatts} W" : string.Empty;
            LabelGpuDynamicBoost.Text = Draft.GpuDynamicBoostWatts.HasValue ? $"{Draft.GpuDynamicBoostWatts} W" : string.Empty;
            LabelPlatformPowerOffset.Text = Draft.PlatformPowerOffsetWatts.HasValue ? $"{Draft.PlatformPowerOffsetWatts} W" : string.Empty;
            RowGpuPower.Value = Draft.GpuDeltaWatts.HasValue && Draft.GpuDynamicBoostWatts.HasValue
                ? ReadGpuWatts() ?? double.NaN : double.NaN;
        }

        private (int pl1, int pl2) ReadClampedLimits(bool preferPl2)
        {
            if (DataContext is not MainViewModel vm)
                return (10, 10);

            int pl1 = vm.LimitPl1ToPlatform((int)Math.Round(RowPl1?.Value ?? 10));
            int pl2 = (int)Math.Round(RowPl2?.Value ?? pl1);
            if (preferPl2 && pl2 < pl1)
                pl1 = vm.LimitPl1ToPlatform(pl2);
            pl2 = vm.LimitPl2(pl1, pl2);
            return (pl1, pl2);
        }

        private void WriteClampedLimitsToEditor(int pl1, int pl2)
        {
            bool wasUpdating = _isUpdatingUi;
            _isUpdatingUi = true;
            if (RowPl1 != null) RowPl1.Value = pl1;
            if (RowPl2 != null) RowPl2.Value = pl2;
            _isUpdatingUi = wasUpdating;
        }

        private void StoreModeLimits(int pl1, int pl2)
        {
            if (DataContext is not MainViewModel vm) return;
            switch (_selectedModeIndex)
            {
                case 0: vm.CustomEcoPl1 = pl1; vm.CustomEcoPl2 = pl2; break;
                case 1: vm.CustomQuietPl1 = pl1; vm.CustomQuietPl2 = pl2; break;
                case 2: vm.CustomBalancedPl1 = pl1; vm.CustomBalancedPl2 = pl2; break;
                case 3: vm.CustomPerfPl1 = pl1; vm.CustomPerfPl2 = pl2; break;
                case 4: vm.CustomTurboPl1 = pl1; vm.CustomTurboPl2 = pl2; break;
            }
        }

        private void RowPl1_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi || DataContext is not MainViewModel vm || !vm.IsHardwareTuningEnabled) return;
            Draft.CpuPl1Watts = double.IsFinite(RowPl1.Value) ? (int)RowPl1.Value : null;
            Draft.CpuPl2Watts = double.IsFinite(RowPl2.Value) ? (int)RowPl2.Value : null;
            if (!Draft.CpuPl1Watts.HasValue || !Draft.CpuPl2Watts.HasValue) return;
            var (pl1, pl2) = ReadClampedLimits(preferPl2: false);
            WriteClampedLimitsToEditor(pl1, pl2);
            StoreModeLimits(pl1, pl2);
            Draft.CpuPl1Watts = pl1; Draft.CpuPl2Watts = pl2;
        }

        private void RowProchotOffset_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi || DataContext is not MainViewModel vm) return;
            RowProchotOffset.CommitText(notify: false);
            vm.ProchotOffsetCelsius = double.IsFinite(RowProchotOffset.Value)
                ? (int)Math.Round(RowProchotOffset.Value) : null;
        }

        private void RowPl2_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi || DataContext is not MainViewModel vm || !vm.IsHardwareTuningEnabled) return;
            Draft.CpuPl1Watts = double.IsFinite(RowPl1.Value) ? (int)RowPl1.Value : null;
            Draft.CpuPl2Watts = double.IsFinite(RowPl2.Value) ? (int)RowPl2.Value : null;
            if (!Draft.CpuPl1Watts.HasValue || !Draft.CpuPl2Watts.HasValue) return;
            var (pl1, pl2) = ReadClampedLimits(preferPl2: true);
            WriteClampedLimitsToEditor(pl1, pl2);
            StoreModeLimits(pl1, pl2);
            Draft.CpuPl1Watts = pl1; Draft.CpuPl2Watts = pl2;
        }

        private int? ReadGpuWatts()
        {
            int? baseWatts = (DataContext as MainViewModel)?.GpuBasePowerWatts;
            return ReadGpuOffsets().NominalReferenceWatts(baseWatts);
        }

        private GpuPowerOffsets ReadGpuOffsets() => new(
            SnapGpuDelta(SliderGpuDelta.Value),
            SnapGpuDynamicBoost(SliderGpuDynamicBoost.Value),
            SnapPlatformPowerOffset(SliderPlatformPowerOffset.Value));

        private static int SnapGpuDelta(double watts) =>
            (int)Math.Round(Math.Clamp(watts, 0, PreySense.Core.Hardware.DriverFactoryCalibration.MaxGpuDeltaWatts) / 5, MidpointRounding.AwayFromZero) * 5;

        private static int SnapGpuDynamicBoost(double watts) =>
            (int)Math.Round(Math.Clamp(watts, 0, PreySense.Core.Hardware.DriverFactoryCalibration.MaxGpuDynamicBoostWatts) / 5, MidpointRounding.AwayFromZero) * 5;

        private static int SnapPlatformPowerOffset(double watts) =>
            (int)Math.Round(Math.Clamp(watts, 0, 100) / 5, MidpointRounding.AwayFromZero) * 5;

        private void GpuOffsets_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingUi || DataContext is not MainViewModel vm || !vm.IsHardwareTuningEnabled) return;
            if (ReferenceEquals(sender, SliderGpuDelta)) Draft.GpuDeltaWatts = SnapGpuDelta(SliderGpuDelta.Value);
            if (ReferenceEquals(sender, SliderGpuDynamicBoost)) Draft.GpuDynamicBoostWatts = SnapGpuDynamicBoost(SliderGpuDynamicBoost.Value);
            if (ReferenceEquals(sender, SliderPlatformPowerOffset)) Draft.PlatformPowerOffsetWatts = SnapPlatformPowerOffset(SliderPlatformPowerOffset.Value);
            UpdateGpuDraftLabels();
            if (Draft.GpuDeltaWatts.HasValue && Draft.GpuDynamicBoostWatts.HasValue && Draft.PlatformPowerOffsetWatts.HasValue)
                StoreGpuWatts(ReadGpuWatts());
        }

        private void StoreGpuWatts(int? watts)
        {
            if (DataContext is not MainViewModel vm) return;
            vm.StoreGpuOffsets(_selectedModeIndex, ReadGpuOffsets());
            switch (_selectedModeIndex)
            {
                case 0: vm.CustomEcoGpuWatts = watts ?? 0; break;
                case 1: vm.CustomQuietGpuWatts = watts ?? 0; break;
                case 2: vm.CustomBalancedGpuWatts = watts ?? 0; break;
                case 3: vm.CustomPerfGpuWatts = watts ?? 0; break;
                case 4: vm.CustomTurboGpuWatts = watts ?? 0; break;
            }
            vm.GpuPowerTargetWatts = watts ?? 0;
        }

        private void RowGpuPower_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi || DataContext is not MainViewModel vm || !vm.IsHardwareTuningEnabled) return;
            int? gpuW = ReadGpuWatts();
            if (RowGpuPower != null) RowGpuPower.Value = gpuW ?? double.NaN;
            StoreGpuWatts(gpuW);
        }

        private void SliderTau_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (SliderTau == null || LabelTau == null) return;
            int val = ReadTauSeconds();
            LabelTau.Text = FormatTau(val);

            if (!_isUpdatingUi && DataContext is MainViewModel vm && vm.IsHardwareTuningEnabled)
            {
                Draft.TauSeconds = val;
                vm.SetTauForModeIndex(_selectedModeIndex, val);
            }
        }

        private async void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm) return;
            if (!vm.IsHardwareTuningEnabled || vm.IsBusy)
            {
                SetVerificationText(
                    vm.IsBusy
                        ? Services.LocalizationService.Translate("Tuning is busy. Try again in a moment.")
                        : Services.LocalizationService.Translate("Turn on Hardware Tuning first."),
                    TryFindResource("WarningTextBrush") as Brush ?? Brushes.OrangeRed);
                return;
            }

            string modeName = Services.LocalizationService.Translate(GetSelectedModeName());
            var confirmation = new CustomModalDialog
            {
                Owner = this,
                Title = "Performix \u2022 Reset Mode",
                HeaderSubtitle = " \u2022 Reset Mode",
                Message = Services.LocalizationService.Format("Reset {0} to factory power settings and apply them now?", modeName),
                ConfirmText = "Reset & Apply",
                CancelText = "Cancel",
                ShowCancelButton = true
            };
            if (confirmation.ShowDialog() != true)
                return;

            bool resetGpuClocks = SwitchGpuOverclocking.IsChecked == true;
            _isUpdatingUi = true;
            vm.ResetModeToCapturedFactoryDefaults(_selectedModeIndex);
            _drafts[_selectedModeIndex] = vm.FactoryTuningMode(_selectedModeIndex) ?? new();
            Draft.ApplyCpuLimits = false;
            Draft.ApplyGpuLimits = false;
            vm.IsProchotOffsetEnabled = false;
            _isUpdatingUi = false;
            LoadModeData();

            // Write factory CPU/GPU/PROCHOT once, then persist with every apply switch off.
            SetVerificationText(string.Empty);
            var (pl1, pl2, tau) = StoreEditorSettings(vm, updateActiveValues: true);
            byte mode = ModeIndexToByte(_selectedModeIndex);
            bool applyProchot = !vm.IsProchotOffsetLocked && vm.ProchotOffsetCelsius is not null;
            BtnApply.IsEnabled = BtnSave.IsEnabled = BtnReset.IsEnabled = false;
            ComboModeSelect.IsEnabled = TuningScroll.IsEnabled = false;
            try
            {
                bool verified = await vm.ApplyTuningProfileAsync(mode, pl1, pl2, tau,
                    ReadGpuOffsets(), persist: true, vm.ProchotOffsetCelsius, applyProchot,
                    applyCpuLimits: true, applyGpuLimits: vm.IsGpuProfileTuningAvailable,
                    persistApplySwitches: false, restoreMsrToStaticFloor: true,
                    clockOffsets: resetGpuClocks ? new PreySense.Gpu.GpuClockOffsets(0, 0) : null,
                    applyGpuClockOffsets: resetGpuClocks);
                Draft.ApplyCpuLimits = false;
                Draft.ApplyGpuLimits = false;
                vm.IsProchotOffsetEnabled = false;
                LoadModeData();
                string message = verified
                    ? Services.LocalizationService.Translate("Applied and read back successfully.")
                    : (string.IsNullOrWhiteSpace(vm.DiagnosticMessage)
                        ? Services.LocalizationService.Translate("Could not apply.")
                        : vm.DiagnosticMessage);
                SetVerificationText(message, verified
                    ? TryFindResource("SuccessTextBrush") as Brush ?? Brushes.LightGreen
                    : TryFindResource("WarningTextBrush") as Brush ?? Brushes.OrangeRed);
                _verificationTimer.Stop();
                if (IsLoaded) _verificationTimer.Start();
            }
            catch (Exception ex)
            {
                ReportTuningError("Reset mode to factory defaults", ex);
            }
            finally
            {
                UpdateActionButtonsEnabled();
                ComboModeSelect.IsEnabled = TuningScroll.IsEnabled = true;
            }
        }

        private async void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await ApplySelectedModeAsync();
            }
            catch (Exception ex)
            {
                ReportTuningError("Apply tuning profile", ex);
            }
        }

        private async System.Threading.Tasks.Task ApplySelectedModeAsync(bool persist = false)
        {
            if (DataContext is not MainViewModel vm) return;
            // GPU clock offsets are pure NVAPI: allow an apply that only touches them while the
            // driver-backed Hardware Tuning switch is off.
            bool stagedCpu = SwitchApplyCpuLimits?.IsChecked == true;
            bool stagedGpu = vm.IsGpuProfileTuningAvailable && SwitchApplyGpuLimits?.IsChecked == true;
            bool stagedProchot = SwitchProchotOffset?.IsChecked == true;
            bool gpuClockOnly = SwitchGpuOverclocking?.IsChecked == true && !stagedCpu && !stagedGpu && !stagedProchot;
            if ((!vm.IsHardwareTuningEnabled && !gpuClockOnly) || vm.IsBusy)
            {
                SetVerificationText(
                    vm.IsBusy
                        ? Services.LocalizationService.Translate("Tuning is busy. Try again in a moment.")
                        : Services.LocalizationService.Translate("Turn on Hardware Tuning first."),
                    TryFindResource("WarningTextBrush") as Brush ?? Brushes.OrangeRed);
                return;
            }
            SetVerificationText(string.Empty);
            RowPl1.CommitText(notify: false); RowPl2.CommitText(notify: false);
            RowProchotOffset_LostFocus(RowProchotOffset, new RoutedEventArgs());
            StoreGpuClockSliders();
            Draft.ApplyGpuClockOffsets = SwitchGpuOverclocking?.IsChecked == true;
            bool applyProchot = SwitchProchotOffset?.IsChecked == true;
            vm.IsProchotOffsetEnabled = applyProchot;
            Draft.ApplyCpuLimits = SwitchApplyCpuLimits?.IsChecked == true;
            Draft.ApplyGpuLimits = vm.IsGpuProfileTuningAvailable && SwitchApplyGpuLimits?.IsChecked == true;
            Draft.CpuPl1Watts = double.IsFinite(RowPl1.Value) ? (int)RowPl1.Value : null;
            Draft.CpuPl2Watts = double.IsFinite(RowPl2.Value) ? (int)RowPl2.Value : null;
            Draft.TauSeconds = ReadTauSeconds();
            Draft.GpuDeltaWatts = SnapGpuDelta(SliderGpuDelta.Value);
            Draft.GpuDynamicBoostWatts = SnapGpuDynamicBoost(SliderGpuDynamicBoost.Value);
            Draft.PlatformPowerOffsetWatts = SnapPlatformPowerOffset(SliderPlatformPowerOffset.Value);
            if (applyProchot && vm.ProchotOffsetCelsius is null)
            {
                SetVerificationText(Services.LocalizationService.Translate("Enter a PROCHOT offset, or turn its switch off."),
                    TryFindResource("WarningTextBrush") as Brush ?? Brushes.OrangeRed);
                return;
            }
            if (!persist && !Draft.ApplyCpuLimits && !Draft.ApplyGpuLimits && !applyProchot && !Draft.ApplyGpuClockOffsets)
            {
                SetVerificationText(Services.LocalizationService.Translate("Turn on CPU limits, GPU limits, GPU overclocking, or PROCHOT first."),
                    TryFindResource("WarningTextBrush") as Brush ?? Brushes.OrangeRed);
                return;
            }
            if (Draft.ApplyCpuLimits && !Draft.HasCpuLimitValues)
            {
                SetVerificationText(Services.LocalizationService.Translate("Set CPU limits and time limit before applying."),
                    TryFindResource("WarningTextBrush") as Brush ?? Brushes.OrangeRed);
                return;
            }
            if (Draft.ApplyGpuLimits && !Draft.HasGpuLimitValues)
            {
                SetVerificationText(Services.LocalizationService.Translate("Set GPU offsets before applying."),
                    TryFindResource("WarningTextBrush") as Brush ?? Brushes.OrangeRed);
                return;
            }
            var (pl1, pl2, tau) = StoreEditorSettings(vm, updateActiveValues: true);
            byte mode = ModeIndexToByte(_selectedModeIndex);
            BtnApply.IsEnabled = BtnSave.IsEnabled = BtnReset.IsEnabled = false;
            ComboModeSelect.IsEnabled = TuningScroll.IsEnabled = false;
            try
            {
                bool verified = await vm.ApplyTuningProfileAsync(mode, pl1, pl2, tau,
                    ReadGpuOffsets(), persist, vm.ProchotOffsetCelsius, applyProchot,
                    Draft.ApplyCpuLimits, Draft.ApplyGpuLimits,
                    clockOffsets: Draft.HasGpuClockValues ? new PreySense.Gpu.GpuClockOffsets(
                        Draft.GpuCoreClockOffsetMhz!.Value, Draft.GpuMemoryClockOffsetMhz!.Value) : null,
                    applyGpuClockOffsets: Draft.ApplyGpuClockOffsets);
                string message = verified
                    ? Services.LocalizationService.Translate(!Draft.ApplyCpuLimits && !Draft.ApplyGpuLimits && !applyProchot && !Draft.ApplyGpuClockOffsets
                        ? "Saved. Disabled tuning sections were skipped." : "Applied and read back successfully.")
                    : (string.IsNullOrWhiteSpace(vm.DiagnosticMessage)
                        ? Services.LocalizationService.Translate("Could not apply.")
                        : vm.DiagnosticMessage);
                SetVerificationText(message, verified
                    ? TryFindResource("SuccessTextBrush") as Brush ?? Brushes.LightGreen
                    : TryFindResource("WarningTextBrush") as Brush ?? Brushes.OrangeRed);

                _verificationTimer.Stop();
                if (IsLoaded) _verificationTimer.Start();
            }
            catch (Exception ex)
            {
                ReportTuningError("Apply tuning profile", ex);
            }
            finally
            {
                UpdateActionButtonsEnabled();
                ComboModeSelect.IsEnabled = TuningScroll.IsEnabled = true;
            }
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await ApplySelectedModeAsync(persist: true);
            }
            catch (Exception ex)
            {
                ReportTuningError("Save tuning profile", ex);
            }
        }
        private (int Pl1, int Pl2, int Tau) StoreEditorSettings(MainViewModel vm, bool updateActiveValues)
        {
            var (pl1, pl2) = ReadClampedLimits(preferPl2: false);
            int tau = ReadTauSeconds();
            WriteClampedLimitsToEditor(pl1, pl2);
            StoreModeLimits(pl1, pl2);
            vm.SetTauForModeIndex(_selectedModeIndex, tau);
            StoreCpuBoost(vm);
            Draft.CpuBoostMode = vm.CpuBoostForModeIndex(_selectedModeIndex);
            Draft.CpuBoost = Draft.CpuBoostMode > 0;
            int? gpuWatts = ReadGpuWatts();
            RowGpuPower.Value = gpuWatts ?? double.NaN;
            StoreGpuWatts(gpuWatts);
            if (updateActiveValues)
            {
                vm.Pl1Watts = pl1;
                vm.Pl2Watts = pl2;
            }
            return (pl1, pl2, tau);
        }

        private void StoreCpuBoost(MainViewModel vm)
        {
            int boost = Math.Clamp(DropdownCpuBoost?.SelectedIndex ?? 1, 0, 6);
            vm.SetCpuBoostForModeIndex(_selectedModeIndex, boost);
            Draft.CpuBoostMode = boost;
            Draft.CpuBoost = boost > 0;
        }

        private void CpuBoostDropdown_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_isUpdatingUi || DataContext is not MainViewModel vm)
                return;
            int boost = Math.Clamp(DropdownCpuBoost.SelectedIndex, 0, 6);
            vm.SetCpuBoostForModeIndex(_selectedModeIndex, boost);
            Draft.CpuBoostMode = boost;
            Draft.CpuBoost = boost > 0;
        }

        private static byte ModeIndexToByte(int modeIndex) => modeIndex switch
        {
            0 => 0x06,
            1 => 0x00,
            3 => 0x04,
            4 => 0x05,
            _ => 0x01
        };
    }
}
