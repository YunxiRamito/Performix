// Tuning page (S3) code-behind.
//
// PAGE CONTRACT (shared by the four page agents):
//   public MainViewModel? Vm { get; set; }   // assigned by the shell
//   public TuningPage() { InitializeComponent(); }
// The page never constructs a ViewModel, never touches hardware on load and never writes a
// setting on its own — every write happens behind an explicit Apply button that the user
// (or an integration test) has to click. On Loaded the page falls back to
// App.CurrentShell.ViewModel so it also works when a host forgets to assign Vm.
//
// WHY PAGE-LOCAL DRAFTS
// The WPF window kept its edits in control properties and its own `_drafts` dictionary,
// because MainViewModel exposes no observable property for the per-mode AC/DC power-scheme
// values (CpuScheduleValues) or for the NVAPI clock offsets (GpuClockOffsets) — it exposes
// the operations instead. The WinUI page mirrors that: the drafts below are the ONLY source
// for the slider + NumberBox pairs (so dragging echoes instantly and nothing is applied on
// release), and Apply hands them to the ViewModel:
//   CpuScheduleForMode / SaveCpuScheduleForMode / ApplyCpuScheduleForModeAsync /
//   ResetCpuScheduleToWindowsDefaultsAsync / GpuOffsetsForMode / ApplyTuningProfileAsync /
//   ResetGpuClockOffsetsAsync.
// Everything that *can* be bound straight to the ViewModel is bound straight to it (see the
// x:Bind list in the S3 hand-off): telemetry texts, the five mode flags, SetModeCommand,
// IsKernelDriverSupported, IsHardwareTuningEnabled, IsProchotOffsetEnabled,
// CanToggleProchotOffset, CanEditProchotOffset, ProchotActivationLabel,
// IsGpuClockTuningAvailable, GpuClockTuningUnavailableReason, CpuBoostMode.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PreySense.Core.Driver;
using PreySense.Frontend.ViewModels;
using PreySense.Gpu;
using PreySense.Mode;

namespace Predex.WinUI.Pages
{
    public sealed partial class TuningPage : Page, INotifyPropertyChanged, IPredexPage
    {
        // PERFBOOSTMODE 0..6, the same table the WPF window filled its dropdown with.
        private static readonly string[] BoostModes = MainViewModel.CpuBoostModeNames;

        private MainViewModel? _vm;
        private bool _loading;

        public TuningPage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>Raised by "Open curve editor"; the shell may route it to the Fans page.</summary>
        public event EventHandler? FanEditorRequested;

        // ─────────────────────────────────────────────────────────────────────────
        // Shell contract
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The shared ViewModel. Assigned by the shell; every x:Bind on Vm re-evaluates when
        /// this changes because the page raises PropertyChanged for it.
        /// </summary>
        public MainViewModel? Vm
        {
            get => _vm;
            set
            {
                if (ReferenceEquals(_vm, value)) return;
                if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
                _vm = value;
                if (_vm is not null) _vm.PropertyChanged += OnVmPropertyChanged;
                Raise(nameof(Vm));
                ReloadFromVm();
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Static text (kept off the XAML side: an x:Bind string literal cannot carry an
        // apostrophe, and these are full sentences that belong to the localization table)
        // ─────────────────────────────────────────────────────────────────────────

        // The page subtitle was removed with the rest of the settings description text
        // (docs\UI-DESIGN-RULES.md §7): the shell title bar already names the page. The strings
        // below are not visible prose — they are the reasons a control is disabled, so they stay.

        /// <summary>Reason text for every capability that needs the kernel driver.</summary>
        public string DriverLockedTooltip =>
            Loc.T("Locked by this machine's BIOS - the kernel driver is required.");

        public string HardwareTuningTooltip =>
            Loc.T("Requires the bundled kernel driver; this build ships without it, so the editor stays read-only.");

        public string ModeSwitchTooltip =>
            Loc.T("Switching the Acer operating mode also applies that mode's CPU scheduling and fan profile.");

        public string CpuSchedulingTooltip =>
            Loc.T("CPU undervolt and power limits need the kernel driver and are locked by this BIOS - here you can only tune frequency caps, Boost and EPP.");

        public string GpuOverclockTooltip =>
            Loc.T("NVIDIA core and memory clock offsets via NVAPI. This works without the kernel driver.");

        public string FanPolicyTooltip =>
            Loc.T("The fan profile follows the operating mode. Open the Fans page to edit the curve.");

        public string DriverNoticeText =>
            Loc.T("These sections need the bundled kernel driver. This BIOS locks them, so they are shown disabled.");

        /// <summary>
        /// T1: the ViewModel's CpuBoostModeNames are English source strings, so the dropdown
        /// values were the only English left on the page. Translate at the binding surface.
        /// </summary>
        public string[] BoostModeNames
        {
            get
            {
                var names = new string[BoostModes.Length];
                for (int i = 0; i < BoostModes.Length; i++) names[i] = Loc.T(BoostModes[i]);
                return names;
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Drafts / read-only mirrors (see the file header for why they live here)
        // ─────────────────────────────────────────────────────────────────────────

        private int _cpuPowerSourceIndex;
        /// <summary>0 = AC, 1 = DC. Switching reloads the draft from that side's schedule.</summary>
        public int CpuPowerSourceIndex
        {
            get => _cpuPowerSourceIndex;
            set
            {
                if (!Set(ref _cpuPowerSourceIndex, value)) return;
                if (!_loading) LoadCpuSchedule();
            }
        }

        private double _cpuMaxState = 100;
        public double CpuMaxState { get => _cpuMaxState; set => Set(ref _cpuMaxState, Clamp(value, 0, 100)); }

        private double _cpuEpp;
        public double CpuEpp { get => _cpuEpp; set => Set(ref _cpuEpp, Clamp(value, 0, 100)); }

        private double _cpuCoreParkMin = 100;
        public double CpuCoreParkMin { get => _cpuCoreParkMin; set => Set(ref _cpuCoreParkMin, Clamp(value, 0, 100)); }

        private double _cpuCoreParkMax = 100;
        public double CpuCoreParkMax { get => _cpuCoreParkMax; set => Set(ref _cpuCoreParkMax, Clamp(value, 0, 100)); }

        private int _cpuBoostIndex = 1;
        public int CpuBoostIndex
        {
            get => _cpuBoostIndex;
            set => Set(ref _cpuBoostIndex, Math.Clamp(value, 0, BoostModes.Length - 1));
        }

        private bool _gpuOverclockEnabled;
        public bool GpuOverclockEnabled
        {
            get => _gpuOverclockEnabled;
            set
            {
                if (!Set(ref _gpuOverclockEnabled, value)) return;
                Raise(nameof(GpuChildrenEnabled));
            }
        }

        /// <summary>
        /// T5: the core/memory sliders and the Apply / Restore buttons are only usable when the
        /// overclock switch is on AND the GPU allows clock offsets. One property so the XAML
        /// cannot enable half of the block.
        /// </summary>
        public bool GpuChildrenEnabled => _gpuOverclockEnabled && (Vm?.IsGpuClockTuningAvailable ?? false);

        private double _gpuCoreOffset;
        public double GpuCoreOffset { get => _gpuCoreOffset; set => Set(ref _gpuCoreOffset, Clamp(value, 0, GpuClockTuningState.MaximumCoreOffsetMhz)); }

        private double _gpuMemoryOffset;
        public double GpuMemoryOffset { get => _gpuMemoryOffset; set => Set(ref _gpuMemoryOffset, Clamp(value, 0, 100000)); }

        private double _pl1Value;
        public double Pl1Value { get => _pl1Value; set => Set(ref _pl1Value, Clamp(value, 0, 300)); }

        private double _pl2Value;
        public double Pl2Value { get => _pl2Value; set => Set(ref _pl2Value, Clamp(value, 0, 300)); }

        private double _tauValue;
        public double TauValue { get => _tauValue; set => Set(ref _tauValue, Clamp(value, 0, 11)); }

        private double _prochotOffsetValue;
        public double ProchotOffsetValue { get => _prochotOffsetValue; set => Set(ref _prochotOffsetValue, Clamp(value, 0, 63)); }

        private double _gpuDeltaValue;
        public double GpuDeltaValue { get => _gpuDeltaValue; set => Set(ref _gpuDeltaValue, Clamp(value, 0, 255)); }

        private double _gpuDynamicBoostValue;
        public double GpuDynamicBoostValue { get => _gpuDynamicBoostValue; set => Set(ref _gpuDynamicBoostValue, Clamp(value, 0, 25)); }

        private double _gpuPlatformOffsetValue;
        public double GpuPlatformOffsetValue { get => _gpuPlatformOffsetValue; set => Set(ref _gpuPlatformOffsetValue, Clamp(value, 0, 100)); }

        private string _fanPolicyText = "N/A";
        public string FanPolicyText { get => _fanPolicyText; set => Set(ref _fanPolicyText, value); }

        // ─────────────────────────────────────────────────────────────────────────
        // Lifecycle
        // ─────────────────────────────────────────────────────────────────────────

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
            if (_vm is null) Vm = App.CurrentShell?.ViewModel;      // host did not assign it
            if (_vm is not null) _vm.PropertyChanged += OnVmPropertyChanged;
            ReloadFromVm();
            StartLiveStamp();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
            StopLiveStamp();
        }

        private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // A changed reading is freshness evidence too (the snapshot event can be missed while
            // the page is being rebuilt), and it re-renders the freshness line.
            if (e.PropertyName is nameof(MainViewModel.CpuTemperatureText)
                or nameof(MainViewModel.GpuTemperatureText)
                or nameof(MainViewModel.CpuFanText)
                or nameof(MainViewModel.GpuFanText))
            {
                _lastTelemetryAt = DateTimeOffset.Now;
                RefreshLiveStamp();
                return;
            }

            // The page edits the active mode, so a mode switch re-seeds every block.
            if (e.PropertyName is nameof(MainViewModel.ActivePowerMode)
                or nameof(MainViewModel.GpuClockTuningState)
                or nameof(MainViewModel.IsGpuClockTuningAvailable)
                or nameof(MainViewModel.IsGpuProfileTuningAvailable))
            {
                ReloadFromVm();
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Load / mirror the ViewModel into the drafts
        // ─────────────────────────────────────────────────────────────────────────

        private void ReloadFromVm()
        {
            if (_vm is null)
            {
                FanPolicyText = "N/A";
                return;
            }

            try
            {
                LoadCpuSchedule();
                LoadGpuOverclock();
                LoadDriverEditors();
                LoadFanPolicy();
            }
            catch (Exception ex)
            {
                SetStatus(Loc.T("Could not read the current tuning state: ") + ex.Message, false);
            }
        }

        /// <summary>Seeds the CPU scheduling drafts from the active mode + selected power source.</summary>
        private void LoadCpuSchedule()
        {
            if (_vm is null) return;
            _loading = true;
            try
            {
                var schedule = _vm.CpuScheduleForMode(CurrentModeByte);
                var values = schedule.For(CpuPowerSourceIndex != 1);
                CpuMaxState = values.MaxProcessorState;
                CpuEpp = values.Epp;
                CpuCoreParkMin = values.CoreParkingMin;
                CpuCoreParkMax = values.CoreParkingMax;
                CpuBoostIndex = Math.Clamp(values.BoostMode, 0, BoostModes.Length - 1);
            }
            finally
            {
                _loading = false;
            }
        }

        /// <summary>Seeds the GPU overclocking drafts and resizes the sliders to the NVAPI range.</summary>
        private void LoadGpuOverclock()
        {
            if (_vm is null) return;
            var state = _vm.GpuClockTuningState;
            if (state is null)
            {
                GpuOverclockEnabled = false;
                GpuCoreOffset = 0;
                GpuMemoryOffset = 0;
                return;
            }

            var core = state.Core.ForStep(GpuClockTuningState.CoreStepMhz);
            var memory = state.Memory.ForStep(GpuClockTuningState.MemoryStepMhz);

            // Ranges first, then values: Slider/NumberBox clamp on the way in. Maximum is
            // assigned before Minimum so a range that starts above 0 can never trip the
            // RangeBase "Minimum > Maximum" guard.
            double coreMax = Math.Max(0, Math.Max(core.MinimumMhz, core.MaximumMhz));
            double coreMin = Math.Clamp(core.MinimumMhz, 0, coreMax);
            SldGpuCore.Maximum = coreMax;
            SldGpuCore.Minimum = coreMin;
            BoxGpuCore.Maximum = coreMax;
            BoxGpuCore.Minimum = coreMin;

            double memoryMax = Math.Max(0, Math.Max(memory.MinimumMhz, memory.MaximumMhz));
            double memoryMin = Math.Clamp(memory.MinimumMhz, 0, memoryMax);
            SldGpuMemory.Maximum = memoryMax;
            SldGpuMemory.Minimum = memoryMin;
            BoxGpuMemory.Maximum = memoryMax;
            BoxGpuMemory.Minimum = memoryMin;

            var draft = _vm.TuningEditorMode(ModeIndex);
            _loading = true;
            try
            {
                GpuOverclockEnabled = draft.ApplyGpuClockOffsets;
                GpuCoreOffset = state.Core.Snap(
                    draft.GpuCoreClockOffsetMhz ?? state.Current.CoreMhz, GpuClockTuningState.CoreStepMhz);
                GpuMemoryOffset = state.Memory.Snap(
                    draft.GpuMemoryClockOffsetMhz ?? state.Current.MemoryMhz, GpuClockTuningState.MemoryStepMhz);
            }
            finally
            {
                _loading = false;
            }
        }

        /// <summary>Seeds the driver-backed editors (PL1/PL2/Tau/PROCHOT/GPU watts).</summary>
        private void LoadDriverEditors()
        {
            if (_vm is null) return;
            var draft = _vm.TuningEditorMode(ModeIndex);
            _loading = true;
            try
            {
                int pl1 = _vm.LimitPl1ToPlatform(draft.CpuPl1Watts ?? 0);
                Pl1Value = pl1;
                Pl2Value = _vm.LimitPl2(pl1, draft.CpuPl2Watts ?? 0);
                TauValue = draft.TauSeconds ?? 0;
                ProchotOffsetValue = _vm.TuningEditorProchotOffset ?? 0;
                GpuDeltaValue = draft.GpuDeltaWatts ?? 0;
                GpuDynamicBoostValue = draft.GpuDynamicBoostWatts ?? 0;
                GpuPlatformOffsetValue = draft.PlatformPowerOffsetWatts ?? 0;
            }
            finally
            {
                _loading = false;
            }
        }

        private void LoadFanPolicy()
        {
            if (_vm is null) return;
            try
            {
                FanPolicyText = Loc.T(_vm.LoadFanProfile(CurrentModeByte).ControlMode.ToString());
            }
            catch
            {
                FanPolicyText = "N/A";
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // CPU scheduling (own Apply, nothing applied on slider release)
        // ─────────────────────────────────────────────────────────────────────────

        private async void ApplyCpuSchedule_Click(object sender, RoutedEventArgs e)
        {
            if (_vm is null) return;
            if (sender is Button button) button.IsEnabled = false;
            try
            {
                byte mode = CurrentModeByte;
                int coreMin = Math.Clamp((int)Math.Round(CpuCoreParkMin), 0, 100);
                int coreMax = Math.Clamp((int)Math.Round(CpuCoreParkMax), 0, 100);
                if (coreMax < coreMin) coreMax = coreMin;   // powercfg rejects min > max

                // CpuScheduleValues is nested in CpuScheduling (PreySense.Mode) — not a top-level type.
                var values = CpuScheduling.CpuScheduleValues.Default(
                    (int)Math.Round(CpuMaxState), CpuBoostIndex, (int)Math.Round(CpuEpp), coreMin, coreMax);

                var schedule = _vm.CpuScheduleForMode(mode);
                if (CpuPowerSourceIndex == 1) schedule.Dc = values;
                else schedule.Ac = values;

                _vm.SaveCpuScheduleForMode(mode, schedule);
                var result = await _vm.ApplyCpuScheduleForModeAsync(mode);
                SetStatus(result.Success ? Loc.T("CPU scheduling applied.") : Loc.T(result.Status), result.Success);
            }
            catch (Exception ex)
            {
                SetStatus(Loc.T("Apply failed: ") + ex.Message, false);
            }
            finally
            {
                if (sender is Button b) b.IsEnabled = true;
            }
        }

        private async void ResetCpuSchedule_Click(object sender, RoutedEventArgs e)
        {
            if (_vm is null) return;
            if (sender is Button button) button.IsEnabled = false;
            try
            {
                var result = await _vm.ResetCpuScheduleToWindowsDefaultsAsync();
                ReloadFromVm();
                SetStatus(result.Success
                        ? Loc.T("CPU scheduling restored to Windows defaults.")
                        : Loc.T(result.Status),
                    result.Success);
            }
            catch (Exception ex)
            {
                SetStatus(Loc.T("Restore failed: ") + ex.Message, false);
            }
            finally
            {
                if (sender is Button b) b.IsEnabled = true;
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // GPU overclocking (NVAPI only, still one explicit Apply)
        // ─────────────────────────────────────────────────────────────────────────

        private async void ApplyGpuClock_Click(object sender, RoutedEventArgs e)
        {
            if (_vm is null) return;
            if (!GpuOverclockEnabled)
            {
                SetStatus(Loc.T("Turn on GPU clock offsets before applying."), false);
                return;
            }

            var state = _vm.GpuClockTuningState;
            if (state is null)
            {
                SetStatus(_vm.GpuClockTuningUnavailableReason, false);
                return;
            }

            int core = state.Core.Snap((int)Math.Round(GpuCoreOffset), GpuClockTuningState.CoreStepMhz);
            int memory = state.Memory.Snap((int)Math.Round(GpuMemoryOffset), GpuClockTuningState.MemoryStepMhz);
            var offsets = new GpuClockOffsets(core, memory);
            if (!state.Allows(offsets))
            {
                SetStatus(Loc.T("GPU clock offsets are outside the supported range."), false);
                return;
            }

            if (sender is Button button) button.IsEnabled = false;
            try
            {
                // Clock offsets are the only thing this button touches: no CPU/GPU wattage and
                // no PROCHOT, so the driver-backed switches stay out of the request.
                var powerOffsets = _vm.GpuOffsetsForMode(ModeIndex) ?? new GpuPowerOffsets(0, 0, 0);
                bool applied = await _vm.ApplyTuningProfileAsync(
                    CurrentModeByte, 0, 0, 0, powerOffsets, persist: false,
                    applyCpuLimits: false, applyGpuLimits: false,
                    clockOffsets: offsets, applyGpuClockOffsets: true);

                GpuCoreOffset = core;
                GpuMemoryOffset = memory;
                SetStatus(applied
                        ? Loc.T("GPU clock offsets applied.")
                        : Loc.T("GPU clock offsets were not applied."),
                    applied);
            }
            catch (Exception ex)
            {
                SetStatus(Loc.T("Apply failed: ") + ex.Message, false);
            }
            finally
            {
                if (sender is Button b) b.IsEnabled = true;
            }
        }

        private async void ResetGpuClock_Click(object sender, RoutedEventArgs e)
        {
            if (_vm is null) return;
            if (sender is Button button) button.IsEnabled = false;
            try
            {
                var result = await _vm.ResetGpuClockOffsetsAsync();
                if (result.Success)
                {
                    GpuOverclockEnabled = false;
                    LoadGpuOverclock();
                }
                SetStatus(result.Success
                        ? Loc.T("GPU clocks restored to 0/0 MHz.")
                        : Loc.T(result.Status),
                    result.Success);
            }
            catch (Exception ex)
            {
                SetStatus(Loc.T("Restore failed: ") + ex.Message, false);
            }
            finally
            {
                if (sender is Button b) b.IsEnabled = true;
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Fans entry + Advanced
        // ─────────────────────────────────────────────────────────────────────────

        private void OpenFanEditor_Click(object sender, RoutedEventArgs e)
        {
            FanEditorRequested?.Invoke(this, EventArgs.Empty);
            // Same-window navigation: the integrator can either subscribe to the event or let
            // the shell route the click (the call is idempotent for the already-open page).
            App.CurrentShell?.NavigateTo("Fans");
            SetStatus(Loc.T("Fan curve editing opens on the Fans page."), true);
        }

        private void ApplyAdvanced_Click(object sender, RoutedEventArgs e)
        {
            // The button is disabled while the kernel driver is missing; if a future build
            // enables it, the request belongs to ApplyTuningProfileAsync. Keeping this handler
            // honest avoids a silent no-op click.
            SetStatus(Loc.T("Hardware tuning needs the kernel driver, which this build does not include."), false);
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>0=Eco, 1=Quiet, 2=Balanced, 3=Performance, 4=Turbo for the active mode.</summary>
        private int ModeIndex => _vm?.ActivePowerMode switch
        {
            0x06 => 0,
            0x00 => 1,
            0x01 => 2,
            0x04 => 3,
            0x05 => 4,
            _ => 2
        };

        private byte CurrentModeByte => ModeIndex switch
        {
            0 => 0x06,
            1 => 0x00,
            3 => 0x04,
            4 => 0x05,
            _ => 0x01
        };

        private void SetStatus(string message, bool success)
        {
            if (StatusText is null) return;
            StatusText.Text = message;
            StatusText.Foreground = new SolidColorBrush(success ? Colors.LightGreen : Colors.OrangeRed);
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Live-data freshness (UX review item 15)
        //
        // The four numbers in the status card came from the shared telemetry pipeline and
        // carried no age, so a stalled read looked exactly like a live one. The page now keeps
        // the time of the last real sample (TelemetryCoordinator.SnapshotAvailable, the same
        // source the ViewModel reads) and rewrites one line every second:
        //
        //   Updated 1 s ago                              fresh
        //   Last update 34 s ago · data may be stale      the pipeline stopped answering
        //   No live data: the sensor read failed.         the snapshot itself was invalid
        //
        // A failed read also already renders "—" in place of the number (TelemetryText), so the
        // card never shows a stale value as if it were current.
        // ─────────────────────────────────────────────────────────────────────────
        private const int StaleAfterSeconds = 5;

        private DateTimeOffset _lastTelemetryAt;
        private bool _telemetryInvalid;
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _liveStampTimer;

        private void StartLiveStamp()
        {
            _lastTelemetryAt = DateTimeOffset.Now;
            _telemetryInvalid = AllTelemetryMissing();
            RefreshLiveStamp();

            PreySense.Core.Services.TelemetryCoordinator.SnapshotAvailable -= OnTelemetrySnapshot;
            PreySense.Core.Services.TelemetryCoordinator.SnapshotAvailable += OnTelemetrySnapshot;

            _liveStampTimer ??= DispatcherQueue.CreateTimer();
            _liveStampTimer.Interval = TimeSpan.FromSeconds(1);
            _liveStampTimer.Tick -= OnLiveStampTick;
            _liveStampTimer.Tick += OnLiveStampTick;
            _liveStampTimer.Start();
        }

        private void StopLiveStamp()
        {
            PreySense.Core.Services.TelemetryCoordinator.SnapshotAvailable -= OnTelemetrySnapshot;
            if (_liveStampTimer is { } timer)
            {
                timer.Tick -= OnLiveStampTick;
                timer.Stop();
            }
        }

        /// <summary>Worker thread: record the sample time and let the UI thread redraw the line.</summary>
        private void OnTelemetrySnapshot(PreySense.Core.Services.TelemetrySnapshot snapshot)
        {
            bool invalid = !snapshot.IsValid;
            DispatcherQueue.TryEnqueue(() =>
            {
                _lastTelemetryAt = DateTimeOffset.Now;
                _telemetryInvalid = invalid;
                RefreshLiveStamp();
            });
        }

        private void OnLiveStampTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
            => RefreshLiveStamp();

        private void RefreshLiveStamp()
        {
            if (TxtLiveStamp is null) return;

            if (_telemetryInvalid || AllTelemetryMissing())
            {
                TxtLiveStamp.Text = Loc.T("No live data: the sensor read failed.");
                TxtLiveStamp.Foreground = Brush("DangerTextBrush");
                return;
            }

            int age = (int)Math.Max(0, Math.Round((DateTimeOffset.Now - _lastTelemetryAt).TotalSeconds));
            if (age > StaleAfterSeconds)
            {
                TxtLiveStamp.Text = PreySense.Frontend.Services.LocalizationService.Format(
                    "Last update {0} s ago · data may be stale", age);
                TxtLiveStamp.Foreground = Brush("PageTextSecondaryBrush");
                return;
            }

            TxtLiveStamp.Text = age <= 2
                ? Loc.T("Updated just now")
                : PreySense.Frontend.Services.LocalizationService.Format("Updated {0} s ago", age);
            TxtLiveStamp.Foreground = Brush("TextMutedBrush");
        }

        private bool AllTelemetryMissing()
            => IsMissing(_vm?.CpuTemperatureText) && IsMissing(_vm?.GpuTemperatureText)
               && IsMissing(_vm?.CpuFanText) && IsMissing(_vm?.GpuFanText);

        private static bool IsMissing(string? text)
            => string.IsNullOrWhiteSpace(text) || text == "N/A";

        private static Brush Brush(string key)
            => Application.Current?.Resources.TryGetValue(key, out object? value) == true &&
               value is Brush brush
                ? brush
                : new SolidColorBrush(Colors.Transparent);

        private static double Clamp(double value, double minimum, double maximum) =>
            double.IsNaN(value) ? minimum : Math.Clamp(value, minimum, maximum);

        private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(propertyName);
            return true;
        }

        private void Raise(string? propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
