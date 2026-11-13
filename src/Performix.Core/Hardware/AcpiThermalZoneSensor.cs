using System;
using System.Diagnostics;
using System.Management;

namespace PreySense.Core.Hardware;

/// <summary>
/// Last-resort CPU temperature from the ACPI thermal zone (<c>root\WMI MSAcpi_ThermalZoneTemperature</c>).
/// </summary>
/// <remarks>
/// On the Predator PH317-55 this sensor is dead: it reports a constant 60.1 °C regardless of load,
/// so it must never drive the fan curve. It is only kept as a non-null placeholder so the UI can
/// show "something" when neither the service pipe nor the (removed) kernel driver is available.
/// </remarks>
public static class AcpiThermalZoneSensor
{
    /// <summary>Known-bad constant read on this chassis; surfaced so callers can flag it.</summary>
    public const double KnownDeadValueCelsius = 60.1;

    private static readonly object Gate = new();
    private static int? _cached;
    private static long _nextRefreshTimestamp;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(5);

    /// <summary>True when the value equals the dead-sensor constant this machine reports.</summary>
    public static bool IsKnownDeadSensorValue(double celsius) =>
        Math.Abs(celsius - KnownDeadValueCelsius) < 0.15;

    /// <summary>
    /// Reads the ACPI thermal-zone temperature in °C. Never throws; caches for 5 s because the
    /// WMI query is comparatively expensive and the sensor barely moves.
    /// </summary>
    public static int? TryReadCpuTemperature()
    {
        lock (Gate)
        {
            long now = Stopwatch.GetTimestamp();
            if (now < _nextRefreshTimestamp)
                return _cached;

            _nextRefreshTimestamp = now + (long)(Stopwatch.Frequency * CacheDuration.TotalSeconds);
            _cached = Read();
            return _cached;
        }
    }

    private static int? Read()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI",
                "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            using var results = searcher.Get();
            double? warmest = null;
            foreach (ManagementObject zone in results)
            {
                using (zone)
                {
                    if (zone["CurrentTemperature"] is not uint tenthKelvin) continue;
                    double celsius = tenthKelvin / 10.0 - 273.15;
                    if (celsius is < -20 or > 150) continue;
                    if (warmest is null || celsius > warmest) warmest = celsius;
                }
            }

            return warmest is double value ? (int)Math.Round(value) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Clears the cache (used by the diagnostics probe to get a fresh read).</summary>
    public static void Invalidate()
    {
        lock (Gate)
        {
            _cached = null;
            _nextRefreshTimestamp = 0;
        }
    }
}
