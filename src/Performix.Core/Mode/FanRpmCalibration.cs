using System;

namespace PreySense.Mode;

/// <summary>
/// Measured EC duty → RPM calibration for the PH317-55 fans (E1).
/// The EC response is NOT linear: 0 % already spins ~3529 RPM and the curve flattens towards the top.
/// Measured with the service pipe (id 2/6) while sweeping custom duty:
///   0 % → 3529, 10 % → 3947, 50 % → 5357, 100 % → 7692 RPM.
/// </summary>
/// <remarks>
/// Two different quantities are kept apart on purpose:
/// <list type="bullet">
/// <item>stored stage targets (<see cref="FanStage.CpuTargetRpm"/>) use the legacy linear 0..6400
/// encoding so existing <c>preysense.json</c> profiles keep their duty meaning;</item>
/// <item>anything that talks about the <b>physical</b> fan speed (closed-loop verification, RPM
/// display) uses this calibration.</item>
/// </list>
/// </remarks>
public static class FanRpmCalibration
{
    // (duty %, measured RPM)
    private static readonly (int Duty, int Rpm)[] Points =
    [
        (0, 3529),
        (10, 3947),
        (50, 5357),
        (100, 7692)
    ];

    /// <summary>Highest measured physical fan speed (RPM).</summary>
    public static int MaxRpm => Points[^1].Rpm;

    /// <summary>Lowest physical fan speed: the EC floor at 0 % duty (RPM).</summary>
    public static int FloorRpm => Points[0].Rpm;

    /// <summary>Physical RPM for a duty percentage (piecewise linear over the measured table).</summary>
    public static int DutyToRpm(int dutyPercent)
    {
        int duty = Math.Clamp(dutyPercent, 0, 100);
        for (int i = 1; i < Points.Length; i++)
        {
            if (duty > Points[i].Duty) continue;
            var left = Points[i - 1];
            var right = Points[i];
            double span = Math.Max(1, right.Duty - left.Duty);
            double fraction = (duty - left.Duty) / span;
            return (int)Math.Round(left.Rpm + (right.Rpm - left.Rpm) * fraction, MidpointRounding.AwayFromZero);
        }
        return MaxRpm;
    }

    /// <summary>
    /// Duty percentage that measured <paramref name="rpm"/> corresponds to (inverse of the table).
    /// Values below the EC floor map to 0 %, values above the measured maximum map to 100 %.
    /// </summary>
    public static int RpmToDuty(int rpm)
    {
        if (rpm <= FloorRpm) return 0;
        if (rpm >= MaxRpm) return 100;
        for (int i = 1; i < Points.Length; i++)
        {
            if (rpm > Points[i].Rpm) continue;
            var left = Points[i - 1];
            var right = Points[i];
            double span = Math.Max(1, right.Rpm - left.Rpm);
            double fraction = (rpm - left.Rpm) / span;
            return (int)Math.Round(left.Duty + (right.Duty - left.Duty) * fraction, MidpointRounding.AwayFromZero);
        }
        return 100;
    }
}
