using System;

namespace PreySense.Core.Hardware;

/// <summary>Where a live thermal/fan reading actually came from.</summary>
public enum SensorSource
{
    /// <summary>No source produced a reading.</summary>
    None = 0,

    /// <summary>Predator Sense service named pipe (cmd 15) — the only live source on this machine.</summary>
    AcerPipe,

    /// <summary>CPU MSR/MMIO through the bundled kernel driver (removed in this fork).</summary>
    CpuDriverMsr,

    /// <summary>NVIDIA NVAPI thermal settings (needs an awake dGPU).</summary>
    NvidiaNvapi,

    /// <summary>NVIDIA NVML (<c>nvmlDeviceGetTemperature</c>).</summary>
    NvidiaNvml,

    /// <summary>ACPI thermal zone; known-dead placeholder on this chassis.</summary>
    AcpiThermalZone,

    /// <summary>Acer WMI <c>GetGamingSysInfo</c>; broken on this chassis (returns 0 / 0x10000000000).</summary>
    AcerWmi
}

/// <summary>A reading plus the source that produced it (for the probe/self-test and logs).</summary>
public readonly record struct SensorReadout(int? Value, SensorSource Source)
{
    public bool HasValue => Value.HasValue;

    public static SensorReadout Empty => new(null, SensorSource.None);

    public override string ToString() =>
        Value is int value ? $"{value} ({Source})" : "N/A (None)";
}

/// <summary>
/// The single place that defines the temperature/fan fallback order used by telemetry.
/// </summary>
/// <remarks>
/// <para><b>CPU temperature:</b> service pipe → CPU driver MSR (absent in this fork) → ACPI thermal zone.</para>
/// <para><b>GPU temperature:</b> service pipe → NVAPI (existing telemetry path) → NVML.</para>
/// <para><b>Fan RPM:</b> service pipe → Acer WMI, but the WMI value is only accepted when it is
/// physically plausible, because <c>GetGamingSysInfo</c> is broken on this chassis and returns
/// garbage (0 or 0x10000000000) that must never reach the fan curve.</para>
/// </remarks>
public static class ThermalReadout
{
    /// <summary>Highest fan speed considered physically plausible for a laptop fan (RPM).</summary>
    public const int MaxPlausibleFanRpm = 8000;

    /// <summary>CPU temperature: pipe first, then driver MSR, then the ACPI placeholder.</summary>
    public static SensorReadout CpuTemperature(AcerPipeSensorSource? pipe, Func<int?>? driverMsr = null)
    {
        if (pipe?.ReadCpuTemperature() is int fromPipe)
            return new SensorReadout(fromPipe, SensorSource.AcerPipe);

        int? fromDriver = null;
        try { fromDriver = driverMsr?.Invoke(); }
        catch { fromDriver = null; }
        if (fromDriver is int driverValue && driverValue > 0)
            return new SensorReadout(driverValue, SensorSource.CpuDriverMsr);

        if (AcpiThermalZoneSensor.TryReadCpuTemperature() is int acpiValue)
            return new SensorReadout(acpiValue, SensorSource.AcpiThermalZone);

        return SensorReadout.Empty;
    }

    /// <summary>GPU temperature: pipe first, then NVAPI, then NVML.</summary>
    public static SensorReadout GpuTemperature(
        AcerPipeSensorSource? pipe, int? nvapiCelsius, Func<int?>? nvmlCelsius = null)
    {
        if (pipe?.ReadGpuTemperature() is int fromPipe)
            return new SensorReadout(fromPipe, SensorSource.AcerPipe);

        if (nvapiCelsius is int nvapi && nvapi > 0)
            return new SensorReadout(nvapi, SensorSource.NvidiaNvapi);

        int? fromNvml = null;
        try { fromNvml = nvmlCelsius?.Invoke(); }
        catch { fromNvml = null; }
        if (fromNvml is int nvmlValue && nvmlValue > 0)
            return new SensorReadout(nvmlValue, SensorSource.NvidiaNvml);

        return SensorReadout.Empty;
    }

    /// <summary>Fan RPM: pipe first, then Acer WMI if the answer is plausible.</summary>
    public static SensorReadout FanRpm(
        AcerPipeSensorSource? pipe, int pipeSensorId, Func<int>? acerWmiFallback = null)
    {
        if (pipe?.ReadSensorValue(pipeSensorId) is int fromPipe)
            return new SensorReadout(fromPipe, SensorSource.AcerPipe);

        int? fromWmi = null;
        try { fromWmi = acerWmiFallback?.Invoke(); }
        catch { fromWmi = null; }
        if (fromWmi is int wmiValue && wmiValue > 0 && wmiValue <= MaxPlausibleFanRpm)
            return new SensorReadout(wmiValue, SensorSource.AcerWmi);

        return SensorReadout.Empty;
    }
}
