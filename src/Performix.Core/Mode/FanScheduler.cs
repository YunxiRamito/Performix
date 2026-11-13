using System;
using System.Diagnostics;

namespace PreySense.Mode;

/// <summary>Per-mode fan scheduler tuning (GHelper-style overlay on top of the EC stage machine).</summary>
public readonly record struct FanSchedulerSettings(
    bool PredictiveEnabled,
    int PredictiveLoadPercent,
    int PredictiveLeadCelsius,
    bool ClosedLoopEnabled,
    int ClosedLoopTrimPercent,
    int ClosedLoopIntervalMs)
{
    public static FanSchedulerSettings Default => new(
        PredictiveEnabled: true,
        PredictiveLoadPercent: 60,
        PredictiveLeadCelsius: 4,
        ClosedLoopEnabled: false,
        ClosedLoopTrimPercent: 15,
        ClosedLoopIntervalMs: 1000);
}

/// <summary>One fan-scheduler evaluation input (temperatures, loads and measured RPMs).</summary>
public readonly record struct FanSchedulerInput(
    int CpuTemp,
    int GpuTemp,
    double? CpuLoadPercent,
    double? GpuLoadPercent,
    int? CpuRpm,
    int? GpuRpm);

/// <summary>One fan-scheduler evaluation output.</summary>
public readonly record struct FanSchedulerResult(
    int CpuDuty,
    int GpuDuty,
    int Stage,
    bool PredictiveActive,
    bool ClosedLoopActive,
    bool SafetyActive,
    string Reason);

/// <summary>
/// Loads a per-mode curve, adds load anticipation (jump the curve by a few °C when the CPU/GPU is
/// suddenly busy so the fans lead the temperature), applies a safety cap, and optionally trims the
/// duty with the measured RPM (accounts for the EC's non-linear duty→RPM response).
/// </summary>
/// <remarks>
/// Pure logic: no hardware access, deterministic given inputs and <c>nowMs</c>. When RPM is missing
/// the closed loop degrades to open loop automatically.
/// </remarks>
public sealed class FanScheduler
{
    /// <summary>Hard safety caps: at or above these temperatures the fans run at 100 %.</summary>
    public const int CpuSafetyCelsius = 92;
    public const int GpuSafetyCelsius = 88;

    private int _cpuTrim;
    private int _gpuTrim;
    private long _lastClosedLoopMs = long.MinValue / 2;

    public FanSchedulerSettings Settings { get; set; } = FanSchedulerSettings.Default;

    /// <summary>Forgets trim state (call when the profile/mode changes).</summary>
    public void Reset()
    {
        _cpuTrim = 0;
        _gpuTrim = 0;
        _lastClosedLoopMs = long.MinValue / 2;
    }

    public FanSchedulerResult Evaluate(
        FanSchedulerInput input,
        FanCurveProfile profile,
        FanCurveFollower follower,
        long nowMs)
    {
        // 1. Safety cap first — never wait for the curve when the die is that hot.
        if (input.CpuTemp >= CpuSafetyCelsius || input.GpuTemp >= GpuSafetyCelsius)
        {
            Reset();
            return new FanSchedulerResult(100, 100, Math.Max(0, profile.Stages.Length - 1),
                PredictiveActive: false, ClosedLoopActive: false, SafetyActive: true,
                $"Safety cap: CPU {input.CpuTemp} °C / GPU {input.GpuTemp} °C");
        }

        // 2. Load anticipation: a busy CPU/GPU heats up before the temperature sensor moves,
        //    so shift the curve input up by the configured lead.
        bool predictive = Settings.PredictiveEnabled
                          && ((input.CpuLoadPercent ?? 0) >= Settings.PredictiveLoadPercent
                              || (input.GpuLoadPercent ?? 0) >= Settings.PredictiveLoadPercent);
        int lead = predictive ? Math.Clamp(Settings.PredictiveLeadCelsius, 0, 15) : 0;
        int cpuEval = input.CpuTemp + lead;
        int gpuEval = input.GpuTemp > 0 ? input.GpuTemp + lead : 0;

        // 3. EC-style stage machine on the (possibly anticipated) temperatures.
        var (cpuDuty, gpuDuty, stage) = follower.Evaluate(
            cpuEval, gpuEval, profile.Stages, profile.RampUpSeconds, nowMs);

        // 4. Closed loop: compare the RPM the EC should be delivering for that stage with the
        //    measured RPM and trim the duty. Bounded per adjustment (±trim %), at most once per
        //    interval, and skipped entirely when RPM is unavailable.
        bool closedLoop = false;
        if (Settings.ClosedLoopEnabled && profile.Stages.Length > 0
            && stage >= 0 && stage < profile.Stages.Length)
        {
            var stageInfo = profile.Stages[stage];
            bool due = nowMs - _lastClosedLoopMs >= Math.Max(250, Settings.ClosedLoopIntervalMs);
            int maxTrim = Math.Clamp(Settings.ClosedLoopTrimPercent, 0, 15);

            if (input.CpuRpm is int cpuRpm && cpuRpm > 0)
            {
                int desiredDuty = FanRpmCalibration.RpmToDuty(cpuRpm);
                if (due && maxTrim > 0)
                {
                    int step = Math.Clamp(cpuDuty - desiredDuty, -maxTrim, maxTrim);
                    _cpuTrim = Math.Clamp(_cpuTrim + step, -maxTrim, maxTrim);
                }
                cpuDuty = Math.Clamp(cpuDuty + _cpuTrim, 0, 100);
                closedLoop = due || _cpuTrim != 0;
            }
            else
            {
                _cpuTrim = 0;
            }

            if (input.GpuRpm is int gpuRpm && gpuRpm > 0)
            {
                int desiredDuty = FanRpmCalibration.RpmToDuty(gpuRpm);
                if (due && maxTrim > 0)
                {
                    int step = Math.Clamp(gpuDuty - desiredDuty, -maxTrim, maxTrim);
                    _gpuTrim = Math.Clamp(_gpuTrim + step, -maxTrim, maxTrim);
                }
                gpuDuty = Math.Clamp(gpuDuty + _gpuTrim, 0, 100);
                closedLoop = closedLoop || due || _gpuTrim != 0;
            }
            else
            {
                _gpuTrim = 0;
            }

            if (due) _lastClosedLoopMs = nowMs;
            _ = stageInfo;
        }
        else if (!Settings.ClosedLoopEnabled)
        {
            Reset();
        }

        string reason = predictive
            ? $"Predictive (+{lead} °C at load {input.CpuLoadPercent:0}/{input.GpuLoadPercent:0} %)"
            : "Curve";
        if (closedLoop) reason += " + RPM trim";
        return new FanSchedulerResult(cpuDuty, gpuDuty, stage, predictive, closedLoop, false, reason);
    }
}

