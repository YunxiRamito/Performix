using System;

namespace PreySense.Mode;

/// <summary>
/// GHelper-compatible fan curve: exactly 8 points = 16 bytes (8 temperature °C bytes followed by
/// 8 duty % bytes). Ported from seerge/g-helper:
///   app/AppConfig.cs:301-310 (hex serialization), :323-357 (default curves),
///   app/AsusACPI.cs:723-765 (FixFanCurve normalization).
/// Acer EC has no curve-table write command, so interpolation runs in software instead — the data
/// model, defaults and normalization stay byte-compatible so a future EC with a table writer can
/// send <see cref="ToBytes"/> unchanged.
/// </summary>
public sealed class GHelperFanCurve
{
    public const int PointCount = 8;
    public const int MinimumTemperature = 20;
    public const int MaximumTemperature = 110;

    private static readonly int[] BalancedTemps = [58, 61, 64, 68, 72, 77, 81, 98];
    private static readonly int[] BalancedCpuDuties = [8, 17, 22, 26, 34, 41, 48, 69];
    private static readonly int[] BalancedGpuDuties = [8, 14, 18, 22, 29, 36, 42, 62];
    private static readonly int[] SilentTemps = [30, 49, 59, 66, 71, 80, 90, 100];
    private static readonly int[] SilentCpuDuties = [0, 0, 3, 12, 20, 28, 34, 41];
    private static readonly int[] SilentGpuDuties = [0, 0, 0, 8, 16, 24, 30, 38];
    private static readonly int[] TurboTemps = [30, 63, 68, 72, 76, 80, 84, 98];
    private static readonly int[] TurboCpuDuties = [17, 26, 34, 41, 52, 67, 81, 90];
    private static readonly int[] TurboGpuDuties = [22, 31, 38, 45, 57, 71, 85, 95];

    public int[] Temps { get; set; } = (int[])BalancedTemps.Clone();
    public int[] Duties { get; set; } = (int[])BalancedCpuDuties.Clone();
    public int[] GpuTemps { get; set; } = (int[])BalancedTemps.Clone();
    public int[] GpuDuties { get; set; } = (int[])BalancedGpuDuties.Clone();

    public GHelperFanCurve Copy() => new()
    {
        Temps = (int[])Temps.Clone(),
        Duties = (int[])Duties.Clone(),
        GpuTemps = (int[])GpuTemps.Clone(),
        GpuDuties = (int[])GpuDuties.Clone()
    };

    /// <summary>GHelper default table for an Acer operating mode (Silent / Balanced / Turbo).</summary>
    public static GHelperFanCurve Default(byte mode)
    {
        var curve = new GHelperFanCurve();
        switch (mode)
        {
            case 0:
            case 6: // Quiet / Eco -> GHelper Silent (Eco stays conservative)
                curve.Temps = (int[])SilentTemps.Clone();
                curve.Duties = (int[])SilentCpuDuties.Clone();
                curve.GpuTemps = (int[])SilentTemps.Clone();
                curve.GpuDuties = (int[])SilentGpuDuties.Clone();
                break;
            case 1: // Balanced
                curve.Temps = (int[])BalancedTemps.Clone();
                curve.Duties = (int[])BalancedCpuDuties.Clone();
                curve.GpuTemps = (int[])BalancedTemps.Clone();
                curve.GpuDuties = (int[])BalancedGpuDuties.Clone();
                break;
            default: // Performance / Turbo
                curve.Temps = (int[])TurboTemps.Clone();
                curve.Duties = (int[])TurboCpuDuties.Clone();
                curve.GpuTemps = (int[])TurboTemps.Clone();
                curve.GpuDuties = (int[])TurboGpuDuties.Clone();
                break;
        }
        return curve;
    }

    /// <summary>16-byte form: 8 temps then 8 duties (GHelper AppConfig hex layout).</summary>
    public byte[] ToBytes()
    {
        var bytes = new byte[16];
        for (int i = 0; i < PointCount; i++)
        {
            bytes[i] = (byte)Math.Clamp(Temps[i], 0, 255);
            bytes[8 + i] = (byte)Math.Clamp(Duties[i], 0, 100);
        }
        return bytes;
    }

    /// <summary>Strictly increasing temperatures, at least 6 °C apart (GHelper FixFanCurve).</summary>
    public static int[] FixTemperatures(int[]? source)
    {
        var temps = new int[PointCount];
        var input = source is { Length: PointCount } ? source : BalancedTemps;
        for (int i = 0; i < PointCount; i++) temps[i] = Math.Clamp(input[i], MinimumTemperature, MaximumTemperature);
        for (int i = 1; i < PointCount; i++)
            if (temps[i] <= temps[i - 1])
                temps[i] = Math.Min(MaximumTemperature, temps[i - 1] + 6);

        // A curve that starts hot gets the GHelper (30 °C, 0 %) anchor in front.
        if (temps[0] >= 40)
        {
            for (int i = PointCount - 1; i > 0; i--) temps[i] = temps[i - 1];
            temps[0] = 30;
        }
        return temps;
    }

    public static int[] FixDuties(int[]? source)
    {
        var duties = new int[PointCount];
        var input = source is { Length: PointCount } ? source : BalancedCpuDuties;
        for (int i = 0; i < PointCount; i++) duties[i] = Math.Clamp(input[i], 0, 100);
        return duties;
    }

    public void Normalize()
    {
        Temps = FixTemperatures(Temps);
        Duties = FixDuties(Duties);
        GpuTemps = FixTemperatures(GpuTemps);
        GpuDuties = FixDuties(GpuDuties);
    }

    /// <summary>Software interpolation (this EC cannot interpolate a table itself).</summary>
    public int Interpolate(int temperature, bool gpu = false)
    {
        int[] temps = gpu ? GpuTemps : Temps;
        int[] duties = gpu ? GpuDuties : Duties;
        if (temperature <= temps[0]) return Math.Clamp(duties[0], 0, 100);
        if (temperature >= temps[PointCount - 1]) return Math.Clamp(duties[PointCount - 1], 0, 100);
        for (int i = 1; i < PointCount; i++)
        {
            if (temperature > temps[i]) continue;
            int span = Math.Max(1, temps[i] - temps[i - 1]);
            double fraction = (temperature - temps[i - 1]) / (double)span;
            int value = (int)Math.Round(duties[i - 1] + (duties[i] - duties[i - 1]) * fraction,
                MidpointRounding.AwayFromZero);
            return Math.Clamp(value, Math.Min(duties[i - 1], duties[i]), Math.Max(duties[i - 1], duties[i]));
        }
        return Math.Clamp(duties[PointCount - 1], 0, 100);
    }
}
