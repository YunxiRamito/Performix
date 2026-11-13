using System;
using System.Text;
using PreySense.Core.Hardware;
using PreySense.Gpu;
using PreySense.Helpers;
using PreySense.Mode;

namespace PreySense.Core.Diagnostics;

/// <summary>
/// Read-only self-check for the live telemetry sources. Used by <c>PreySense.Probe.exe</c> and by
/// the frontend's <c>--probe</c> switch, so acceptance does not need to start the GUI.
/// </summary>
/// <remarks>
/// Only GET-class operations are performed: cmd 15 on the Predator Sense pipe, WMI
/// <c>GetGamingSysInfo</c> reads, NVML temperature and the ACPI thermal zone. No Set command,
/// no service control, no driver, no registry write.
/// </remarks>
public static class AcerSensorProbe
{
    /// <summary>Runs the full self-check and returns a printable report plus a success flag.</summary>
    public static (string Report, bool PipeOk, bool FanCurveReady) Run()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== PreySense sensor self-check (read-only) ===");
        sb.AppendLine($"time    : {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"machine : {Environment.MachineName}  ({Environment.OSVersion})");
        sb.AppendLine($"pipe    : {AcerPipeSensorSource.PipePath}");
        sb.AppendLine($"service : {AcerPipeSensorSource.ServiceName} (StartMode=Manual, started on demand by the OEM app)");
        sb.AppendLine();

        var pipe = new AcerPipeSensorSource(AcerPipeSensorSource.ProbeConnectTimeoutMs);
        var probe = pipe.Probe();

        sb.AppendLine("[1] Predator Sense pipe — cmd 15 (GetGamingSystemInformation), read-only");
        sb.AppendLine($"    pipe reachable : {(probe.PipeReachable ? "YES" : "NO")}   total {probe.TotalElapsedMs} ms");
        foreach (var row in probe.Sensors)
        {
            string state = row.Success
                ? FormatSensorValue(row.SensorId, row.Value!.Value)
                : $"FAILED ({row.Error ?? "unknown"})";
            sb.AppendLine($"    {row.Name,-26}: {state,-22} raw=0x{row.Raw:X16}  {row.ElapsedMs} ms");
        }
        sb.AppendLine();

        // The telemetry chain must mirror ProductionHardwareService exactly.
        int? driverTemp = CpuDriverRuntime.Telemetry?.ReadPackageTemperature();
        var cpu = ThermalReadout.CpuTemperature(pipe, () => driverTemp);
        var gpu = ThermalReadout.GpuTemperature(pipe, nvapiCelsius: null, nvmlCelsius: () => NvmlHelper.GetGpuTemperature());
        var cpuRpm = ThermalReadout.FanRpm(pipe, AcerPipeSensorSource.SensorCpuFanRpm, acerWmiFallback: null);
        var gpuRpm = ThermalReadout.FanRpm(pipe, AcerPipeSensorSource.SensorGpuFanRpm, acerWmiFallback: null);

        sb.AppendLine("[2] Telemetry fallback chain (exact order used by ProductionHardwareService)");
        sb.AppendLine("    (every value here is a fresh read taken a few hundred ms after [1]: the EC tach can");
        sb.AppendLine("     step between reads, so the same fan may legitimately show a different RPM in [1] and [2])");
        sb.AppendLine("    CPU temperature : pipe -> CPU driver MSR (absent in this fork) -> ACPI thermal zone");
        sb.AppendLine($"                    => {Describe(cpu, "°C")}");
        sb.AppendLine("    GPU temperature : pipe -> NVAPI -> NVML");
        sb.AppendLine($"                    => {Describe(gpu, "°C")}");
        sb.AppendLine("    CPU fan RPM     : pipe -> Acer WMI (only if physically plausible)");
        sb.AppendLine($"                    => {Describe(cpuRpm, " RPM")}");
        sb.AppendLine("    GPU fan RPM     : pipe -> Acer WMI (only if physically plausible)");
        sb.AppendLine($"                    => {Describe(gpuRpm, " RPM")}");
        sb.AppendLine();

        AcpiThermalZoneSensor.Invalidate();
        int? acpi = AcpiThermalZoneSensor.TryReadCpuTemperature();
        int? nvml = NvmlHelper.GetGpuTemperature();
        sb.AppendLine("[3] Individual fallback layers");
        sb.AppendLine($"    CPU driver MSR (this fork has no kernel driver): {(driverTemp is int dt ? $"{dt} °C" : "N/A")}");
        sb.AppendLine($"    ACPI thermal zone (placeholder, not for curves)  : {(acpi is int a ? $"{a} °C" : "N/A")}"
                      + (acpi is int a2 && AcpiThermalZoneSensor.IsKnownDeadSensorValue(a2)
                          ? "   <-- known-dead constant on this chassis"
                          : string.Empty));
        sb.AppendLine($"    NVML GPU temperature                             : {(nvml is int n ? $"{n} °C" : "N/A")}");
        sb.AppendLine();

        sb.AppendLine("[4] Acer WMI GetGamingSysInfo reference (known-broken on the PH317-55)");
        try
        {
            using var wmi = new WmiController();
            var snap = wmi.ReadThermalSensors(includeTemperatures: true, readCpuRpm: true, readGpuRpm: true);
            sb.AppendLine($"    WMI CpuTemp={snap.CpuTemp}  GpuTemp={snap.GpuTemp}  CpuRpm={snap.CpuRpm}  GpuRpm={snap.GpuRpm}");
            sb.AppendLine("    -> deliberately NOT used by telemetry; the pipe values above are authoritative.");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"    WMI reference read failed: {ex.GetType().Name}: {ex.Message}");
        }
        sb.AppendLine();

        sb.AppendLine("[5] Fan write path");
        sb.AppendLine("    WmiController.Fans.cs (SetGamingFanBehavior / SetGamingFanSpeed) — unchanged and verified");
        sb.AppendLine("    separately; this probe never issues a Set command.");
        sb.AppendLine();

        // [6] Pure-logic dry run of the fan curve with the live temperatures: proves the curve can
        // actually select a duty on this machine without writing anything to the EC.
        bool curveValid = false;
        try
        {
            byte mode = 1;
            string modeNote = string.Empty;
            try
            {
                using var wmi = new WmiController();
                if (!wmi.TryGetPowerProfile(out mode))
                {
                    mode = 1;
                    modeNote = " (Acer WMI mode unreadable; assuming Balanced)";
                }
            }
            catch
            {
                mode = 1;
                modeNote = " (Acer WMI unavailable; assuming Balanced)";
            }

            var profile = FanCurveStorage.Load(mode);
            int cpuTemp = cpu.Value ?? -1;
            int gpuTemp = gpu.Value ?? -1;
            curveValid = FanCurveProfile.ValidTemperatures(cpuTemp, gpuTemp);
            double cooling = FanCurveProfile.CoolingTemperature(cpuTemp, gpuTemp);
            var follower = new FanCurveFollower();
            var target = follower.Evaluate(cpuTemp, gpuTemp, profile.Stages, delaySeconds: 0, nowMs: 0);

            sb.AppendLine("[6] Fan-curve dry run (pure logic, no hardware writes)");
            sb.AppendLine($"    power mode      : 0x{mode:X2}{modeNote}");
            sb.AppendLine($"    curve control   : {profile.ControlMode}   stages={profile.Stages.Length}");
            sb.AppendLine("    config source   : existing PreySense JSON fan profile (read-only load)");
            sb.AppendLine($"    cooling temp    : {(cooling > 0 ? $"{cooling:0} °C" : "N/A")}   (max of CPU/GPU — what the curve uses)");
            sb.AppendLine($"    valid for curve : {(curveValid ? "YES" : "NO")}");
            sb.AppendLine($"    target          : stage {target.Stage} -> CPU {target.CpuPercent}% / GPU {target.GpuPercent}%");
            sb.AppendLine("    the app would call SetFanSpeeds(cpu%, gpu%) here; this probe does not.");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"[6] Fan-curve dry run failed: {ex.GetType().Name}: {ex.Message}");
        }
        sb.AppendLine();

        sb.AppendLine();
        sb.AppendLine("[7] Operating mode (pipe cmd 33, read-only; the app switches modes with cmd 32)");
        if (pipe.TryGetPowerMode(out byte liveMode, out string modeStatus))
            sb.AppendLine($"    live mode : 0x{liveMode:X2}  {ModeName(liveMode)}   [{modeStatus}]");
        else
            sb.AppendLine($"    live mode : unavailable ({modeStatus})");
        sb.AppendLine($"    service   : {PredatorPipeService.DescribeStatus()}");
        sb.AppendLine();

        bool fanCurveReady = cpu.Source == SensorSource.AcerPipe
                             && gpu.Source == SensorSource.AcerPipe
                             && curveValid;
        sb.AppendLine(fanCurveReady
            ? $"VERDICT: OK — the fan curve gets live pipe telemetry (CPU {cpu.Value} °C / GPU {gpu.Value} °C) and selects a duty."
            : $"VERDICT: NOT READY — pipe telemetry missing or unusable (cpu={cpu}, gpu={gpu}, curveValid={curveValid}).");

        return (sb.ToString(), probe.PipeReachable, fanCurveReady);
    }

    private static string Describe(SensorReadout readout, string unit) =>
        readout.Value is int value
            ? $"{value}{unit}  [{readout.Source}]"
            : $"N/A  [{readout.Source}]";

    private static string ModeName(byte mode) => mode switch
    {
        0x00 => "Quiet",
        0x01 => "Balanced",
        0x04 => "Performance",
        0x05 => "Turbo",
        0x06 => "Eco",
        _ => "Unknown"
    };

    private static string FormatSensorValue(int sensorId, int value) => sensorId switch
    {
        AcerPipeSensorSource.SensorCpuTemperature or
        AcerPipeSensorSource.SensorGpuTemperature or
        AcerPipeSensorSource.SensorSystemTemperature => $"{value} °C",
        _ => $"{value} RPM"
    };
}
