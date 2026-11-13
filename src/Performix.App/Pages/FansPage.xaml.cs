// Fans page (S4) code-behind — ported from PreySense.Frontend\Views\FanCurveWindow.xaml.cs.
//
// What changed vs. WPF:
//   • Window chrome / footer buttons  → page inside the single-window shell (no chrome here)
//   • WpfFanCurveControl (OnRender)   → Controls\FanCurveCanvas (Canvas element tree)
//   • Mouse*-events + CaptureMouse    → Pointer*-events + CapturePointer, hidden inside the control
//   • Stage tables (FanStage[]/EC)    → not on this page: the curve editor works on the GHelper
//                                       8-point ContinuousFanCurve, which is what ModeFanCurveConfig
//                                       .CurveCpuTemps/CurveCpuDuties/CurveGpuTemps/CurveGpuDuties
//                                       actually persist (FanCurveStorage.Save / Load).
//
// Live model write-back: FanCurveCanvas mutates the arrays it is given, and those arrays ARE
// _profile.Curve.CpuCurve.Temps / .Duties / GpuCurve.GpuTemps / .Duties. So every pointer move
// writes straight into the ViewModel's curve arrays (no copy, no deferred commit); Apply only
// persists them via Vm.SaveFanProfile + Vm.ApplyFanProfileAsync.
//
// No hardware is touched by this file directly (red line 12): every write goes through
// MainViewModel.SaveFanProfile / ApplyFanProfileAsync.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Predex.WinUI.Controls;
using Predex.WinUI.Services;
using PreySense.Core.Services;
using PreySense.Frontend.Services;
using PreySense.Frontend.ViewModels;
using PreySense.Mode;

namespace Predex.WinUI.Pages
{
    /// <summary>
    /// Fan curve page: profile-mode picker, control-mode row, CPU+GPU drag editors, ramp/hysteresis
    /// parameters, live RPM and the Apply / Restore Defaults pair.
    /// </summary>
    public sealed partial class FansPage : Page, INotifyPropertyChanged, IPredexPage
    {
        // Same order as MainViewModel.FanCurveModeNames (Eco / Quiet / Balanced / Performance / Turbo).
        private static readonly byte[] Modes = [6, 0, 1, 4, 5];

        private const string LockedHintSource = "Switch to Custom or Curve mode to edit the curve.";
        private const string DragHintSource = "Drag a point to change its duty; x is temperature 20–110 °C.";

        private MainViewModel? _vm;

        /// <summary>
        /// The live CpuFanText / GpuFanText subscription. The shell caches the page instance, so
        /// Loaded / Unloaded repeat on the same object while <see cref="Vm"/> is assigned only
        /// once — subscribing inside this setter alone left the page deaf after the first return
        /// (RPM frozen on re-entry). Loaded now re-attaches unconditionally; see OnLoaded.
        /// </summary>
        private readonly PropertyChangedSubscription _vmSubscription = new();

        /// <summary>Assigned by the shell before the page is shown (page contract).</summary>
        public MainViewModel? Vm
        {
            get => _vm;
            set
            {
                if (ReferenceEquals(_vm, value)) return;
                _vm = value;
                _vmSubscription.Attach(_vm, OnVmPropertyChanged);
                RaiseFanTexts();
            }
        }

        /// <summary>
        /// R4 bug 3: the fan values come from MainViewModel.CpuFanText / GpuFanText — the same
        /// two properties the home and tuning pages show. The page used to read
        /// TelemetryCoordinator.CurrentSnapshot on its own 1 Hz timer, i.e. a SECOND mapping
        /// of the same sample that could be one poll newer than the values on the other pages.
        /// There is exactly one mapping now (MainViewModel.ApplyMainWindowTelemetry).
        /// </summary>
        public string CpuFanText => _vm?.CpuFanText ?? "—";

        public string GpuFanText => _vm?.GpuFanText ?? "—";

        private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(MainViewModel.CpuFanText) or nameof(MainViewModel.GpuFanText))
                RaiseFanTexts();
        }

        private void RaiseFanTexts()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CpuFanText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GpuFanText)));
        }

        private readonly Dictionary<byte, FanCurveProfile> _drafts = new();
        private FanCurveProfile _profile = new();
        private byte _mode = 1;                 // Balanced
        private bool _loading = true;           // true until OnLoaded has loaded the profile
        private bool _applying;
        private bool _syncingModeRadios;        // set while code (not the user) sets IsChecked

        private DispatcherQueueTimer? _autoApplyTimer;

        public FansPage()
        {
            InitializeComponent();

            CpuChart.CurveEdited += OnChartCurveEdited;
            CpuChart.EditCompleted += OnChartEditCompleted;
            CpuChart.SelectionChanged += OnChartSelectionChanged;
            GpuChart.CurveEdited += OnChartCurveEdited;
            GpuChart.EditCompleted += OnChartEditCompleted;
            GpuChart.SelectionChanged += OnChartSelectionChanged;
            ClampOption.Checked += OnClampOptionChanged;
            ClampOption.Unchecked += OnClampOptionChanged;

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        // ── Bindable page state (x:Bind targets) ────────────────────────────────

        private string _editingHint = string.Empty;
        private bool _editorEnabled;
        private double _editorOpacity = 1.0;
        private Visibility _lockedHintVisibility = Visibility.Collapsed;
        private string _editorTooltip = string.Empty;

        private string _cpuPointText = string.Empty;
        private string _gpuPointText = string.Empty;

        public string EditingHint { get => _editingHint; private set => SetField(ref _editingHint, value); }

        /// <summary>False in Auto / Max: the whole editor block is greyed out.</summary>
        public bool EditorEnabled { get => _editorEnabled; private set => SetField(ref _editorEnabled, value); }

        public double EditorOpacity { get => _editorOpacity; private set => SetField(ref _editorOpacity, value); }

        public Visibility LockedHintVisibility { get => _lockedHintVisibility; private set => SetField(ref _lockedHintVisibility, value); }

        public string EditorTooltip { get => _editorTooltip; private set => SetField(ref _editorTooltip, value); }


        public string CpuPointText { get => _cpuPointText; private set => SetField(ref _cpuPointText, value); }

        public string GpuPointText { get => _gpuPointText; private set => SetField(ref _gpuPointText, value); }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        // ── Lifetime ────────────────────────────────────────────────────────────

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // The shell assigns Vm before showing the page; if it is late (design-time / bare host)
            // fall back to the shell's single ViewModel rather than constructing a second one.
            // Note: assigning through Vm (not `Vm ??= ...`) so the setter still runs when empty.
            if (_vm is null) Vm = TryGetShellViewModel();

            // The shell caches page instances (ShellWindow._pages), so this runs again on every
            // re-entry while the Vm setter does not. Re-subscribe here, idempotently: without
            // this the fan RPM readout keeps the values from before the page was unloaded.
            _vmSubscription.Attach(_vm, OnVmPropertyChanged);

            if (Vm is null)
            {
                // No ViewModel: keep the page inert but honest — editor greyed out, no fake data.
                _loading = false;
                RefreshHints();
                return;
            }

            _loading = true;

            // VM exposes the English profile names; localize them for the picker so a zh-CN UI
            // does not show "Balanced" next to the localized hint "正在编辑: 均衡".
            ProfileCombo.ItemsSource = Vm.FanCurveModeNames.Select(Loc.T).ToArray();
            _mode = SafeMode();
            int index = Array.IndexOf(Modes, _mode);
            ProfileCombo.SelectedIndex = index >= 0 ? index : 2;

            LoadProfile();
            _loading = false;

            CpuChart.DeviceLabel = Loc.T("CPU Fan");
            GpuChart.DeviceLabel = Loc.T("GPU Fan");
            RefreshHints();
            UpdateClampFromOption();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _autoApplyTimer?.Stop();
            _vmSubscription.Detach();
        }

        private static MainViewModel? TryGetShellViewModel()
        {
            try { return App.CurrentShell?.ViewModel; }
            catch { return null; }
        }

        /// <summary>Mode the firmware actually reports (pipe cmd 33), not the optimistic UI state.</summary>
        private byte SafeMode()
        {
            try
            {
                if (Vm is { } vm && vm.TryGetPowerMode(out byte actual) && ProfileManager.IsSupportedMode(actual))
                    return actual;
            }
            catch { /* hardware unavailable → fall back to the saved mode */ }
            return Vm?.ActivePowerMode ?? 1;
        }

        // ── Profile loading ─────────────────────────────────────────────────────

        private void LoadProfile()
        {
            bool wasLoading = _loading;
            _loading = true;

            _profile = _drafts.TryGetValue(_mode, out FanCurveProfile? draft)
                ? draft.Copy()
                : Vm?.LoadFanProfile(_mode) ?? FanCurveProfile.CreateDefault(_mode);

            // Load() already normalizes; normalize the draft too so what is drawn is exactly what
            // FanCurveStorage.Save would persist (GHelper fixes the 20/110 ends and the 6 °C gaps).
            _profile.Normalize(_mode);

            SetModeRadios(_profile.ControlMode);
            RampUpBox.Value = _profile.Curve.RampUpPercentPerSecond;
            RampDownBox.Value = _profile.Curve.RampDownPercentPerSecond;
            HysteresisBox.Value = _profile.Curve.HysteresisCelsius;
            SyncChartsFromProfile();
            // A point selection belongs to one curve; a profile/fan switch starts from no selection.
            CpuChart.SelectedIndex = -1;
            GpuChart.SelectedIndex = -1;
            _loading = wasLoading;

            RefreshPointReadout();
        }

        /// <summary>Points both charts at the profile's live arrays (no copies).</summary>
        private void SyncChartsFromProfile()
        {
            ContinuousFanCurve curve = _profile.Curve;
            CpuChart.Temperatures = curve.CpuCurve.Temps;
            CpuChart.Duties = curve.CpuCurve.Duties;
            GpuChart.Temperatures = curve.GpuCurve.GpuTemps;
            GpuChart.Duties = curve.GpuCurve.GpuDuties;
        }

        private void SetModeRadios(FanControlMode mode)
        {
            _syncingModeRadios = true;
            try
            {
                AutoMode.IsChecked = mode == FanControlMode.Auto;
                MaxMode.IsChecked = mode == FanControlMode.Max;
                CustomMode.IsChecked = mode == FanControlMode.Custom;
                CurveMode.IsChecked = mode == FanControlMode.Curve;
            }
            finally
            {
                _syncingModeRadios = false;
            }
        }

        private FanControlMode SelectedControlMode() =>
            CurveMode.IsChecked == true ? FanControlMode.Curve
            : CustomMode.IsChecked == true ? FanControlMode.Custom
            : MaxMode.IsChecked == true ? FanControlMode.Max
            : FanControlMode.Auto;

        private static bool IsEditableMode(FanControlMode mode) =>
            mode is FanControlMode.Custom or FanControlMode.Curve;

        // ── Editing: the control already wrote into _profile's arrays ────────────

        private void OnChartCurveEdited(object? sender, EventArgs e)
        {
            if (_loading) return;
            _drafts[_mode] = _profile.Copy();   // keep the draft per operating mode (WPF behaviour)
            RefreshPointReadout();
        }

        private void OnChartSelectionChanged(object? sender, EventArgs e) => RefreshPointReadout();

        private void OnChartEditCompleted(object? sender, EventArgs e)
        {
            if (_loading || _applying) return;
            if (AutoApplyOption?.IsChecked != true) return;
            ScheduleAutoApply();
        }

        /// <summary>
        /// Auto Apply is debounced by 500 ms: releasing the pointer repeatedly must not turn into a
        /// burst of EC writes (red line 8 — never re-write fan duty faster than the existing queue).
        /// </summary>
        private void ScheduleAutoApply()
        {
            _autoApplyTimer ??= DispatcherQueue.CreateTimer();
            _autoApplyTimer.Stop();
            _autoApplyTimer.Interval = TimeSpan.FromMilliseconds(500);
            _autoApplyTimer.IsRepeating = false;
            _autoApplyTimer.Tick -= OnAutoApplyTick;
            _autoApplyTimer.Tick += OnAutoApplyTick;
            _autoApplyTimer.Start();
        }

        private void OnAutoApplyTick(DispatcherQueueTimer sender, object args)
        {
            sender.Stop();
            _ = ApplyAsync();
        }

        private void RefreshPointReadout()
        {
            CpuPointText = FormatPoint(CpuChart);
            GpuPointText = FormatPoint(GpuChart);
        }

        private static string FormatPoint(FanCurveCanvas chart)
        {
            int index = chart.SelectedIndex;
            if (index < 0) return string.Empty;
            if (chart.Temperatures is not { } temps || chart.Duties is not { } duties) return string.Empty;
            if (index >= temps.Length || index >= duties.Length) return string.Empty;
            return $"{Loc.T("Selected Point")}: {temps[index]} °C / {duties[index]} %";
        }

        private void OnClampOptionChanged(object sender, RoutedEventArgs e) => UpdateClampFromOption();

        private void UpdateClampFromOption()
        {
            bool snap = ClampOption?.IsChecked == true;
            CpuChart.SnapToGrid = snap;
            GpuChart.SnapToGrid = snap;
        }

        private void CurveField_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (_loading || double.IsNaN(sender.Value)) return;
            ContinuousFanCurve curve = _profile.Curve;
            if (ReferenceEquals(sender, RampUpBox))
                curve.RampUpPercentPerSecond = (int)Math.Round(sender.Value);
            else if (ReferenceEquals(sender, RampDownBox))
                curve.RampDownPercentPerSecond = (int)Math.Round(sender.Value);
            else if (ReferenceEquals(sender, HysteresisBox))
                curve.HysteresisCelsius = (int)Math.Round(sender.Value);
            else
                return;
            _drafts[_mode] = _profile.Copy();
        }

        // ── Mode / hint state ───────────────────────────────────────────────────

        private void ModeRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (_loading || _syncingModeRadios) return;
            RefreshEditorState();   // switching mode unlocks/locks the editor block

            // A click on the control-mode group writes straight through, like the WPF window's
            // fan mode buttons did: persist the draft with the new ControlMode, then push it to
            // the EC. The Apply button below stays available as "re-apply the current mode".
            if (_applying) return;
            FanControlMode mode = SelectedControlMode();
            if (mode == _profile.ControlMode) return;
            _ = ApplyControlModeAsync(mode);
        }

        /// <summary>
        /// Writes one control-mode change to the firmware. Re-entrancy is blocked three ways
        /// (the radio group is disabled while in flight, <see cref="_applying"/> rejects a second
        /// call, and the page is not clickable through a disabled parent) because rapid mode
        /// writes hammer the EC. If <see cref="MainViewModel.ApplyFanProfileAsync"/> throws, the
        /// radios and the draft roll back to the previous mode so UI and hardware agree, and the
        /// page's existing error line explains why.
        /// </summary>
        private async Task ApplyControlModeAsync(FanControlMode mode)
        {
            if (_applying) return;
            if (Vm is not { } vm)
            {
                ShowError(new InvalidOperationException(
                    Loc.T("Fan settings are unavailable: the shell ViewModel is missing.")));
                return;
            }

            FanControlMode previous = _profile.ControlMode;
            _applying = true;
            RefreshEditorState();          // also disables the radio group

            try
            {
                _profile.ControlMode = mode;
                _profile.Normalize(_mode);
                _drafts[_mode] = _profile.Copy();

                byte operatingMode = _mode;
                FanCurveProfile snapshot = _profile.Copy();
                await Task.Run(() => vm.SaveFanProfile(operatingMode, snapshot));
                await vm.ApplyFanProfileAsync();

                // --trace evidence: a control-mode click really wrote through to the EC.
                Predex.WinUI.Services.ShellLog.Write("fans",
                    $"controlMode {previous} -> {mode} applied for operating mode {operatingMode}");
                RefreshHints();
            }
            catch (Exception ex)
            {
                _profile.ControlMode = previous;
                _drafts[_mode] = _profile.Copy();
                SetModeRadios(previous);   // guarded: does not re-enter this method
                Predex.WinUI.Services.ShellLog.Write("fans",
                    $"controlMode {previous} -> {mode} FAILED, rolled back: {ex.Message}");
                ShowError(ex);
            }
            finally
            {
                _applying = false;
                RefreshEditorState();      // re-enables the group and re-evaluates the editor lock
            }
        }

        private void ProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _applying) return;
            int index = ProfileCombo.SelectedIndex;
            if (index < 0 || index >= Modes.Length) return;
            byte next = Modes[index];
            if (next == _mode) return;

            _drafts[_mode] = _profile.Copy();
            _mode = next;
            LoadProfile();
            RefreshHints();
        }

        private void RefreshHints()
        {
            byte actual = SafeMode();
            string editing = Loc.T(ProfileManager.ModeToProfileName(_mode));
            EditingHint = _mode == actual
                ? LocalizationService.Format("Editing: {0} (current mode)", editing)
                : LocalizationService.Format("Editing: {0} — current mode is {1}",
                    editing, Loc.T(ProfileManager.ModeToProfileName(actual)));

            RefreshEditorState();
        }

        private void RefreshEditorState()
        {
            bool editable = IsEditableMode(SelectedControlMode()) && !_applying;
            EditorEnabled = editable;
            EditorOpacity = editable ? 1.0 : 0.45;
            LockedHintVisibility = editable ? Visibility.Collapsed : Visibility.Visible;
            EditorTooltip = Loc.T(editable ? DragHintSource : LockedHintSource);
            ApplyButton.IsEnabled = !_applying;
            RestoreButton.IsEnabled = !_applying;
            ProfileCombo.IsEnabled = !_applying;
            ModeRow.IsEnabled = !_applying;
        }


        // ── Apply / Restore ─────────────────────────────────────────────────────

        private async void Apply_Click(object sender, RoutedEventArgs e) => await ApplyAsync();

        private async void Restore_Click(object sender, RoutedEventArgs e)
        {
            if (_applying) return;

            FanCurveProfile defaults = FanCurveProfile.CreateDefault(_mode);
            defaults.ControlMode = FanControlMode.Curve;   // the editor is a curve editor
            // CreateDefault() leaves Curve at the Balanced table; use this mode's GHelper defaults.
            defaults.Curve.CpuCurve = GHelperFanCurve.Default(_mode);
            defaults.Curve.GpuCurve = GHelperFanCurve.Default(_mode);
            _drafts[_mode] = defaults;
            _profile = defaults.Copy();
            _profile.Normalize(_mode);

            SetModeRadios(_profile.ControlMode);
            SyncChartsFromProfile();
            RefreshPointReadout();
            RefreshEditorState();

            await ApplyAsync();
        }

        private async Task ApplyAsync()
        {
            if (_applying) return;
            _applying = true;
            RefreshEditorState();
            ErrorText.Visibility = Visibility.Collapsed;

            try
            {
                // Persist the parameters the NumberBoxes own, then normalize exactly like
                // FanCurveStorage.Save will, so the drawn curve matches the stored curve.
                _profile.ControlMode = SelectedControlMode();
                if (!double.IsNaN(RampUpBox.Value)) _profile.Curve.RampUpPercentPerSecond = (int)Math.Round(RampUpBox.Value);
                if (!double.IsNaN(RampDownBox.Value)) _profile.Curve.RampDownPercentPerSecond = (int)Math.Round(RampDownBox.Value);
                if (!double.IsNaN(HysteresisBox.Value)) _profile.Curve.HysteresisCelsius = (int)Math.Round(HysteresisBox.Value);
                _profile.Normalize(_mode);

                if (Vm is not { } vm)
                {
                    ShowError(new InvalidOperationException(Loc.T("Fan settings are unavailable: the shell ViewModel is missing.")));
                    return;
                }

                byte mode = _mode;
                FanCurveProfile snapshot = _profile.Copy();
                await Task.Run(() => vm.SaveFanProfile(mode, snapshot));
                await vm.ApplyFanProfileAsync();

                _drafts[mode] = _profile.Copy();
                SetModeRadios(_profile.ControlMode);
                SyncChartsFromProfile();   // Normalize may have shifted a point; show the stored form
                RefreshPointReadout();
                RefreshHints();
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
            finally
            {
                _applying = false;
                RefreshEditorState();
            }
        }

        private void ShowError(Exception error)
        {
            ErrorText.Text = LocalizationService.Format("Fan operation failed: {0}", error.Message);
            ErrorText.Visibility = Visibility.Visible;
        }
    }
}
