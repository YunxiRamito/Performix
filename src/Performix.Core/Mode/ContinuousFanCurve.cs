using System;

namespace PreySense.Mode;

/// <summary>One (temperature °C, duty %) point of a continuous fan curve.</summary>
public readonly record struct FanCurvePoint(int TempC, int DutyPercent);

/// <summary>
/// Continuous fan curve: GHelper-compatible 8-point table (see <see cref="GHelperFanCurve"/>),
/// software piecewise-linear interpolation, asymmetric slew-rate limiting (fast rise, slow fall)
/// and a temperature hysteresis band. Pure logic, no hardware.
/// </summary>
public sealed class ContinuousFanCurve
{
    public GHelperFanCurve CpuCurve { get; set; } = GHelperFanCurve.Default(1);
    public GHelperFanCurve GpuCurve { get; set; } = GHelperFanCurve.Default(1);

    /// <summary>Duty may rise at most this many percent per second (1..50).</summary>
    public int RampUpPercentPerSecond { get; set; } = 8;

    /// <summary>Duty may fall at most this many percent per second (slow fall-back, 1..50).</summary>
    public int RampDownPercentPerSecond { get; set; } = 4;

    /// <summary>Temperature must move at least this many °C before the target is re-evaluated.</summary>
    public int HysteresisCelsius { get; set; } = 2;

    private int? _cpuDuty;
    private int? _gpuDuty;
    private int? _cpuTarget;
    private int? _gpuTarget;
    private int? _lastCpuTemp;
    private int? _lastGpuTemp;
    private long _lastMs;
    private long _lastTrimMs;
    private int _cpuTrim;
    private int _gpuTrim;

    public void Reset()
    {
        _cpuDuty = _gpuDuty = null;
        _cpuTarget = _gpuTarget = null;
        _lastCpuTemp = _lastGpuTemp = null;
        _cpuTrim = _gpuTrim = 0;
        _lastTrimMs = 0;
        _lastMs = 0;
    }

    public ContinuousFanCurve Copy() => new()
    {
        CpuCurve = CpuCurve.Copy(),
        GpuCurve = GpuCurve.Copy(),
        RampUpPercentPerSecond = RampUpPercentPerSecond,
        RampDownPercentPerSecond = RampDownPercentPerSecond,
        HysteresisCelsius = HysteresisCelsius
    };

    public void Normalize()
    {
        CpuCurve.Normalize();
        GpuCurve.Normalize();
        RampUpPercentPerSecond = Math.Clamp(RampUpPercentPerSecond, 1, 50);
        RampDownPercentPerSecond = Math.Clamp(RampDownPercentPerSecond, 1, 50);
        HysteresisCelsius = Math.Clamp(HysteresisCelsius, 0, 5);
    }

    /// <summary>Piecewise-linear interpolation over the 8 GHelper points; clamps to the ends.</summary>
    public static int Interpolate(GHelperFanCurve curve, int temperature, bool gpu = false) =>
        curve.Interpolate(temperature, gpu);

    /// <summary>Evaluation with hysteresis + asymmetric slew limiting. Call once per telemetry tick.</summary>
    public (int CpuDuty, int GpuDuty) Evaluate(int cpuTemp, int gpuTemp, long nowMs,
        int? cpuRpm = null, int? gpuRpm = null, bool closedLoopEnabled = false, int closedLoopTrimPercent = 15)
    {
        int targetCpu = UpdateTarget(ref _cpuTarget, ref _lastCpuTemp, cpuTemp, CpuCurve, gpu: false);
        int targetGpu = gpuTemp > 0
            ? UpdateTarget(ref _gpuTarget, ref _lastGpuTemp, gpuTemp, GpuCurve, gpu: true)
            : 0;

        double seconds = _lastMs == 0 ? 0 : Math.Clamp((nowMs - _lastMs) / 1000.0, 0.2, 5);
        bool first = _cpuDuty is null;
        _cpuDuty = Step(_cpuDuty, targetCpu, seconds, first);
        _gpuDuty = Step(_gpuDuty, targetGpu, seconds, first);

        // Same closed-loop rule as the staged scheduler: ±trim %, at most once per second, open
        // loop when RPM is unavailable.
        if (closedLoopEnabled && _lastMs != 0 && nowMs - _lastTrimMs >= 1000)
        {
            int maxTrim = Math.Clamp(closedLoopTrimPercent, 0, 15);
            if (cpuRpm is int cr && cr > 0)
            {
                int desiredDuty = FanRpmCalibration.RpmToDuty(cr); int step = Math.Clamp(_cpuDuty.Value - desiredDuty, -maxTrim, maxTrim);
                _cpuTrim = Math.Clamp(_cpuTrim + step, -maxTrim, maxTrim);
            }
            else _cpuTrim = 0;
            if (gpuRpm is int gr && gr > 0)
            {
                int desiredDuty = FanRpmCalibration.RpmToDuty(gr); int step = Math.Clamp(_gpuDuty.Value - desiredDuty, -maxTrim, maxTrim);
                _gpuTrim = Math.Clamp(_gpuTrim + step, -maxTrim, maxTrim);
            }
            else _gpuTrim = 0;
            _lastTrimMs = nowMs;
        }
        else if (!closedLoopEnabled) _cpuTrim = _gpuTrim = 0;

        _lastMs = nowMs;
        return (Math.Clamp(_cpuDuty.Value + _cpuTrim, 0, 100), Math.Clamp(_gpuDuty.Value + _gpuTrim, 0, 100));
    }

    private int UpdateTarget(ref int? target, ref int? lastTemp, int temp, GHelperFanCurve curve, bool gpu)
    {
        // Hysteresis: only recompute after the sensor moves by the configured band.
        if (target is null || lastTemp is null || Math.Abs(temp - lastTemp.Value) >= Math.Max(1, HysteresisCelsius))
        {
            lastTemp = temp;
            target = curve.Interpolate(temp, gpu);
        }
        return target.Value;
    }

    private int Step(int? current, int target, double seconds, bool first)
    {
        if (first || seconds <= 0) return Math.Clamp(target, 0, 100);
        int duty = current ?? target;
        int delta = target - duty;
        double maxUp = Math.Clamp(RampUpPercentPerSecond, 1, 50) * seconds;
        double maxDown = Math.Clamp(RampDownPercentPerSecond, 1, 50) * seconds;
        int limited = delta > 0
            ? duty + Math.Min(delta, (int)Math.Round(maxUp, MidpointRounding.AwayFromZero))
            : duty - Math.Min(-delta, (int)Math.Round(maxDown, MidpointRounding.AwayFromZero));
        return Math.Clamp(limited, 0, 100);
    }
}

