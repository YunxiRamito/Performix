using System;
using System.Threading.Tasks;
using PreySense.Core.Services;
using PreySense.Mode;

namespace PreySense.Frontend.ViewModels;

public partial class MainViewModel
{
    public string[] FanCurveModeNames { get; } = { "Eco", "Quiet", "Balanced", "Performance", "Turbo" };

    private readonly object _fanControlGate = new();
    private readonly FanCurveFollower _fanFollower = new();
    private readonly FanScheduler _fanScheduler = new();
    private FanCurveProfile? _runningFanProfile;
    private byte _runningFanProfileMode = 0xFF;
    private byte _fanBoundProfileMode = 0xFF;
    private int? _sentCpuFan, _sentGpuFan;
    private bool _sentMaxFanMode, _fanOverrideActive, _autoFanModeApplied, _fanWriteFailed;
    private long _fanModeSettlesAt;
    private long _fanWriteSuspendUntil;
    private long _lastFanWriteMs;
    private bool _fanSnapshotsSubscribed;
    private HardwareTelemetry _lastFanTelemetry;
    private bool _hasLastFanTelemetry;

    private bool RequiresFanTemperatures
    {
        get
        {
            lock (_fanControlGate)
            {
                return !_fanWriteFailed && _runningFanProfile?.ControlMode == FanControlMode.Custom;
            }
        }
    }

    private void UpdateFanDemand()
    {
        lock (_fanControlGate)
        {
            bool required = !_disposed && RequiresFanTemperatures;
            if (required != _fanSnapshotsSubscribed)
            {
                if (required) TelemetryCoordinator.SnapshotAvailable += OnFanSnapshot;
                else TelemetryCoordinator.SnapshotAvailable -= OnFanSnapshot;
                _fanSnapshotsSubscribed = required;
            }
            TelemetryCoordinator.SetDemand(TelemetryDemand.FanController, required);
        }
    }

    private void OnFanSnapshot(TelemetrySnapshot snapshot)
    {
        if (!_disposed && _fanSnapshotsSubscribed)
            ProcessFanCurves(snapshot.ToHardwareTelemetry(), snapshot.CpuUsage, snapshot.GpuUsage,
                snapshot.CpuRpm, snapshot.GpuRpm);
    }

    private void DisposeFanTelemetry()
    {
        lock (_fanControlGate)
        {
            TelemetryCoordinator.SnapshotAvailable -= OnFanSnapshot;
            _fanSnapshotsSubscribed = false;
            TelemetryCoordinator.SetDemand(TelemetryDemand.FanController, false);
        }
    }

    public int? AppliedCpuFanPercent
    {
        get { lock (_fanControlGate) return !_fanOverrideActive ? null : _sentMaxFanMode ? 100 : _sentCpuFan; }
    }
    public int? AppliedGpuFanPercent
    {
        get { lock (_fanControlGate) return !_fanOverrideActive ? null : _sentMaxFanMode ? 100 : _sentGpuFan; }
    }
    public FanCurveProfile LoadFanProfile(byte mode) => FanCurveStorage.Load(mode);
    public void SaveFanProfile(byte mode, FanCurveProfile profile)
    {
        FanCurveStorage.Save(mode, profile);
        DiagnosticMessage = $"{ProfileManager.ModeToProfileName(mode)} fan settings saved.";
    }
    private void ResetFanFollower()
    {
        _fanFollower.Reset();
        _runningFanProfile?.Curve.Reset();
        _runningFanProfile = null;
        _runningFanProfileMode = 0xFF;
        _sentCpuFan = _sentGpuFan = null;
        _sentMaxFanMode = false;
        _fanWriteFailed = false;
        _autoFanModeApplied = false;
        // Keep override ownership until hardware successfully returns to Auto.
    }
    private void ProcessFanCurves(HardwareTelemetry telemetry,
        double? cpuLoadPercent = null, double? gpuLoadPercent = null,
        int? pipeCpuRpm = null, int? pipeGpuRpm = null)
    {
        if (_disposed) return;
        lock (_fanControlGate)
        {
            if (_disposed || _fanWriteFailed) return;
            _lastFanTelemetry = telemetry;
            _hasLastFanTelemetry = true;

            // Mode changes already invalidate this profile through the mode event/apply paths.
            // Avoid another ACPI query on every telemetry sample.
            byte mode = _runningFanProfileMode;
            if (_runningFanProfile == null && !_hw.TryGetPowerMode(out mode)) return;

            // Per-process fan-curve binding: run another mode's curve without switching hardware mode.
            byte profileMode = mode;
            string? foreground = ForegroundProcessReader.GetForegroundProcessName();
            if (foreground is not null
                && FanCurveStorage.LoadForegroundBindings().TryGetValue(foreground, out byte boundMode)
                && ProfileManager.IsSupportedMode(boundMode))
            {
                profileMode = boundMode;
            }

            if (_runningFanProfile == null || _runningFanProfileMode != mode || _fanBoundProfileMode != profileMode)
            {
                ResetFanFollower();
                _runningFanProfile = FanCurveStorage.Load(profileMode);
                _runningFanProfileMode = mode;
                _fanBoundProfileMode = profileMode;
                _hw.InvalidateFanTargets();
                _fanModeSettlesAt = Environment.TickCount64 + 3000;
            }
            if (_runningFanProfile.ControlMode == FanControlMode.Auto)
            {
                if (!_autoFanModeApplied)
                {
                    // Auto hands control back to firmware; one failure pauses (no OEM reclaim retries).
                    if (!_hw.SetFanMode(0)) { FanWriteFailed(); return; }
                    _fanOverrideActive = false;
                    _autoFanModeApplied = true;
                    _sentCpuFan = _sentGpuFan = null;
                    UpdateFanDemand();
                }
                return;
            }
            if (_runningFanProfile.ControlMode == FanControlMode.Max)
            {
                if (!_sentMaxFanMode)
                {
                    _fanOverrideActive = true;
                    if (_hw.SetFanMode(1))
                    {
                        _sentMaxFanMode = _fanOverrideActive = true;
                        _autoFanModeApplied = false;
                        _sentCpuFan = _sentGpuFan = null;
                        UpdateFanDemand();
                    }
                    else FanWriteFailed();
                }
                return;
            }

            if (_runningFanProfile.ControlMode == FanControlMode.Curve)
            {
                long nowCurve = Environment.TickCount64;
                if (nowCurve < _fanWriteSuspendUntil) return;
                bool curveValid = FanCurveProfile.ValidTemperatures(telemetry.CpuTemp, telemetry.GpuTemp);
                if (nowCurve < _fanModeSettlesAt || !curveValid) return;
                int gpuCurveTemp = telemetry.GpuTemp is > 0 and <= 125 ? telemetry.GpuTemp : 0;

                int cpuDuty, gpuDuty;
                if (telemetry.CpuTemp >= FanScheduler.CpuSafetyCelsius || gpuCurveTemp >= FanScheduler.GpuSafetyCelsius)
                {
                    cpuDuty = gpuDuty = 100;
                }
                else
                {
                    // Continuous piecewise-linear curve: hysteresis + asymmetric slew limiting, plus
                    // the same optional RPM closed-loop trim the staged mode uses.
                    (cpuDuty, gpuDuty) = _runningFanProfile.Curve.Evaluate(
                        telemetry.CpuTemp, gpuCurveTemp, nowCurve,
                        pipeCpuRpm, pipeGpuRpm,
                        _runningFanProfile.Scheduler.ClosedLoopEnabled,
                        _runningFanProfile.Scheduler.ClosedLoopTrimPercent);
                }

                if (_fanOverrideActive && _sentCpuFan is not null && nowCurve - _lastFanWriteMs >= 5000)
                {
                    AppLogger.Log("[Fan] watchdog re-assert (curve)");
                    _hw.InvalidateFanTargets();
                    _sentCpuFan = _sentGpuFan = null;
                }
                if (_sentCpuFan == cpuDuty && _sentGpuFan == gpuDuty) return;
                _fanOverrideActive = true;
                if (_hw.SetFanSpeeds((byte)cpuDuty, (byte)gpuDuty))
                {
                    _sentCpuFan = cpuDuty; _sentGpuFan = gpuDuty;
                    _autoFanModeApplied = _sentMaxFanMode = false;
                    _lastFanWriteMs = Environment.TickCount64;
                    AppLogger.Log($"[Fan] curve duty {cpuDuty}/{gpuDuty}%");
                }
                else FanWriteFailed();
                return;
            }

            long now = Environment.TickCount64;
            // Mode switch: pause both sides for ~300 ms so our writes do not fight the firmware policy.
            if (now < _fanWriteSuspendUntil) return;
            bool valid = FanCurveProfile.ValidTemperatures(telemetry.CpuTemp, telemetry.GpuTemp);
            if (now < _fanModeSettlesAt || !valid) return;
            int gpuTemp = telemetry.GpuTemp is > 0 and <= 125 ? telemetry.GpuTemp : 0;

            // GHelper-style overlay: load anticipation + safety cap + optional RPM closed loop.
            _fanScheduler.Settings = _runningFanProfile.Scheduler;
            var scheduled = _fanScheduler.Evaluate(
                new FanSchedulerInput(telemetry.CpuTemp, gpuTemp, cpuLoadPercent, gpuLoadPercent, pipeCpuRpm, pipeGpuRpm),
                _runningFanProfile, _fanFollower, now);
            int cpu = scheduled.CpuDuty;
            int gpu = scheduled.GpuDuty;

            // Watchdog: the firmware reclaims the fans after a mode switch. Re-assert at most once
            // per 5 s (never every tick — the EC would whine/oscillate).
            if (_fanOverrideActive && _sentCpuFan is not null && now - _lastFanWriteMs >= 5000)
            {
                AppLogger.Log($"[Fan] watchdog re-assert ({scheduled.Reason})");
                _hw.InvalidateFanTargets();
                _sentCpuFan = _sentGpuFan = null;
            }

            if (_sentCpuFan == cpu && _sentGpuFan == gpu) return;
            _fanOverrideActive = true;
            if (_hw.SetFanSpeeds((byte)cpu, (byte)gpu))
            {
                _sentCpuFan = cpu; _sentGpuFan = gpu;
                _fanOverrideActive = true;
                _autoFanModeApplied = _sentMaxFanMode = false;
                _lastFanWriteMs = Environment.TickCount64;
                AppLogger.Log($"[Fan] duty {cpu}/{gpu}% ({scheduled.Reason})");
            }
            else FanWriteFailed();
        }
    }

    /// <summary>Mode switch begins: hold fan writes for 300 ms so firmware and PreySense do not fight.</summary>
    internal void SuspendFanWritesForModeSwitch()
    {
        lock (_fanControlGate)
            _fanWriteSuspendUntil = Environment.TickCount64 + 300;
    }

    /// <summary>
    /// Mode switch succeeded: the firmware immediately applies its own fan policy, so re-assert the
    /// custom profile right after the 300 ms hold instead of waiting for the settle timer.
    /// </summary>
    internal void ReassertFanControlAfterModeSwitch()
    {
        lock (_fanControlGate)
        {
            _fanWriteSuspendUntil = Environment.TickCount64 + 300;
            _fanModeSettlesAt = 0;
            _fanBoundProfileMode = 0xFF;
            ResetFanFollower();
            if (_hw.TryGetPowerMode(out byte mode))
            {
                _runningFanProfile = FanCurveStorage.Load(mode);
                _runningFanProfileMode = mode;
            }
            _hw.InvalidateFanTargets();
            _sentCpuFan = _sentGpuFan = null;
            _lastFanWriteMs = 0;
            UpdateFanDemand();
        }
        _ = ReassertFanAfterDelayAsync();
    }

    private async Task ReassertFanAfterDelayAsync()
    {
        try
        {
            await Task.Delay(350);
            HardwareTelemetry telemetry;
            bool has;
            lock (_fanControlGate) { telemetry = _lastFanTelemetry; has = _hasLastFanTelemetry; }
            if (!_disposed && has) ProcessFanCurves(telemetry);
        }
        catch (Exception ex) { AppLogger.Log($"Fan re-assert after mode switch failed: {ex.Message}"); }
    }

    /// <summary>Saved per-process fan-curve bindings (exe name → operating mode).</summary>
    internal System.Collections.Generic.IReadOnlyDictionary<string, byte> GetForegroundFanBindings() =>
        FanCurveStorage.LoadForegroundBindings();

    /// <summary>Persists per-process fan-curve bindings.</summary>
    internal void SaveForegroundFanBindings(System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, byte>> bindings) =>
        FanCurveStorage.SaveForegroundBindings(bindings);
    private void FanWriteFailed()
    {
        _fanWriteFailed = true;
        UpdateFanDemand();
        _sentCpuFan = _sentGpuFan = null;
        _sentMaxFanMode = false;
        _dispatcher.InvokeAsync(() =>
        {
            DiagnosticMessage = "Fan update failed. Apply fan settings again to resume.";
            Services.InfoWindowService.ShowFailure("风扇写入失败", "设置已暂停，重新应用风扇设置可恢复");
        });
    }
    private void RestoreFanMode()
    {
        lock (_fanControlGate)
        {
            ResetFanFollower();
            if (!_hw.TryGetPowerMode(out byte mode))
            {
                _fanWriteFailed = true;
                UpdateFanDemand();
                return;
            }
            _runningFanProfile = FanCurveStorage.Load(mode);
            _runningFanProfileMode = mode;
            _hw.InvalidateFanTargets();
            _fanModeSettlesAt = Environment.TickCount64 + 3000;
            if (!RequiresFanTemperatures)
            {
                UpdateFanDemand();
                ProcessFanCurves(default);
                return;
            }
            UpdateFanDemand();
            // Custom fan control waits for the next shared sample.
        }
    }

    private void ReleaseCustomFans()
    {
        lock (_fanControlGate)
        {
            if (!_fanOverrideActive) return;
            try
            {
                _autoFanModeApplied = _hw.SetFanMode(0);
                if (_autoFanModeApplied) { _fanOverrideActive = _sentMaxFanMode = false; _sentCpuFan = _sentGpuFan = null; }
            }
            catch (Exception ex) { AppLogger.Log($"Could not return fans to Auto: {ex.Message}"); }
        }
    }
    public async Task ApplyFanProfileAsync()
    {
        await Task.Run(RestoreFanMode);
        lock (_fanControlGate)
        {
            if (_runningFanProfile == null || _fanWriteFailed)
                throw new InvalidOperationException("Fan settings could not be applied.");
        }
        DiagnosticMessage = "Fan settings saved; following the selected mode.";
        Services.InfoWindowService.ShowSuccess("风扇设置已应用", "将跟随当前性能模式生效");
    }
}



