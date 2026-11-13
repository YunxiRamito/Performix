using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace PreySense.Mode;

/// <summary>One SUB_PROCESSOR setting exposed by <see cref="CpuScheduling"/>.</summary>
public sealed record CpuPowerSetting(string Key, Guid Guid, string Name, int Minimum, int Maximum);

/// <summary>
/// Driverless CPU scheduling via the Windows power scheme (SUB_PROCESSOR), ported from
/// reverse/intel-oc/IntelOcProbe.ps1. Values are GUIDs/indices from that probe; nothing here
/// touches the scheme itself — only AC/DC value indices of the currently active scheme.
/// </summary>
public static class CpuScheduling
{
    /// <summary>SUB_PROCESSOR subgroup GUID (IntelOcProbe.ps1:87).</summary>
    public const string SubProcessorGuid = "54533251-82be-4824-96c1-47b60b740d00";

    public static readonly CpuPowerSetting MaxProcessorState =
        new("PROCTHROTTLEMAX", Guid.Parse("bc5038f7-23e0-4960-96da-33abaf5935ec"), "Maximum processor state", 0, 100);

    public static readonly CpuPowerSetting BoostMode =
        new("PERFBOOSTMODE", Guid.Parse("be337238-0d82-4146-a960-4f3749d470c7"), "Processor performance boost mode", 0, 6);

    public static readonly CpuPowerSetting Epp =
        new("PERFEPP", Guid.Parse("36687f9e-e3a5-4dbf-b1dc-15eb381c6863"), "Processor energy performance preference", 0, 100);

    public static readonly CpuPowerSetting CoreParkingMin =
        new("CPMINCORES", Guid.Parse("0cc5b647-c1df-4637-891a-dec35c318583"), "Core parking min cores", 0, 100);

    public static readonly CpuPowerSetting CoreParkingMax =
        new("CPMAXCORES", Guid.Parse("ea062031-0e34-4ff1-9b6d-eb1059334028"), "Core parking max cores", 0, 100);

    public static readonly CpuPowerSetting IdleDisable =
        new("IDLEDISABLE", Guid.Parse("5d76a2ca-e8c0-402f-a133-2158492d58ad"), "Processor idle disable", 0, 1);

    public static readonly CpuPowerSetting ThrottleStates =
        new("THROTTLESTATES", Guid.Parse("3b04d4fd-1cc7-4f23-ab1c-d1337819c4bb"), "Allow throttle states", 0, 1);

    public static readonly CpuPowerSetting SystemCoolingPolicy =
        new("SYSCOOLPOL", Guid.Parse("94d3a615-a899-4ac5-ae2b-e4d8f634367f"), "System cooling policy", 0, 1);

    public static readonly IReadOnlyList<CpuPowerSetting> MainSettings =
        [MaxProcessorState, BoostMode, Epp, CoreParkingMin, CoreParkingMax];

    public static readonly IReadOnlyList<CpuPowerSetting> AdvancedSettings =
        [IdleDisable, ThrottleStates, SystemCoolingPolicy];

    /// <summary>One AC/DC pair of values for a SUB_PROCESSOR setting.</summary>
    public sealed record CpuScheduleValues(
        int MaxProcessorState,
        int BoostMode,
        int Epp,
        int CoreParkingMin,
        int CoreParkingMax)
    {
        public static CpuScheduleValues Default(int maxState, int boost, int epp, int minCores = 100, int maxCores = 100) =>
            new(Math.Clamp(maxState, 0, 100), Math.Clamp(boost, 0, 6), Math.Clamp(epp, 0, 100),
                Math.Clamp(minCores, 0, 100), Math.Clamp(maxCores, 0, 100));

        public CpuScheduleValues Clamp() => new(
            Math.Clamp(MaxProcessorState, 0, 100),
            Math.Clamp(BoostMode, 0, 6),
            Math.Clamp(Epp, 0, 100),
            Math.Clamp(CoreParkingMin, 0, 100),
            Math.Clamp(CoreParkingMax, 0, 100));
    }

    /// <summary>Per-mode AC/DC schedule (Quiet/Eco conservative, Turbo fully open).</summary>
    public sealed class CpuScheduleConfig
    {
        public CpuScheduleValues Ac { get; set; } = CpuScheduleValues.Default(100, 1, 0);
        public CpuScheduleValues Dc { get; set; } = CpuScheduleValues.Default(100, 1, 0);

        public static CpuScheduleConfig DefaultForMode(byte mode) => mode switch
        {
            0 => new() { Ac = CpuScheduleValues.Default(70, 0, 60), Dc = CpuScheduleValues.Default(50, 0, 80) },   // Quiet
            6 => new() { Ac = CpuScheduleValues.Default(60, 0, 70), Dc = CpuScheduleValues.Default(40, 0, 90) },   // Eco
            1 => new() { Ac = CpuScheduleValues.Default(90, 1, 30), Dc = CpuScheduleValues.Default(75, 1, 50) },   // Balanced
            4 => new() { Ac = CpuScheduleValues.Default(100, 2, 10), Dc = CpuScheduleValues.Default(90, 1, 25) },  // Performance
            _ => new() { Ac = CpuScheduleValues.Default(100, 2, 0), Dc = CpuScheduleValues.Default(100, 2, 0) }     // Turbo
        };

        public CpuScheduleValues For(bool ac) => (ac ? Ac : Dc).Clamp();
    }

    /// <summary>Builds the three powercfg calls for one AC/DC side (pure; unit-testable).</summary>
    public static IEnumerable<string[]> BuildApplyArguments(CpuScheduleValues values, bool ac)
    {
        var v = values.Clamp();
        string mode = ac ? "/setacvalueindex" : "/setdcvalueindex";
        yield return [mode, "SCHEME_CURRENT", "SUB_PROCESSOR", MaxProcessorState.Guid.ToString(), v.MaxProcessorState.ToString()];
        yield return [mode, "SCHEME_CURRENT", "SUB_PROCESSOR", BoostMode.Guid.ToString(), v.BoostMode.ToString()];
        yield return [mode, "SCHEME_CURRENT", "SUB_PROCESSOR", Epp.Guid.ToString(), v.Epp.ToString()];
        yield return [mode, "SCHEME_CURRENT", "SUB_PROCESSOR", CoreParkingMin.Guid.ToString(), v.CoreParkingMin.ToString()];
        yield return [mode, "SCHEME_CURRENT", "SUB_PROCESSOR", CoreParkingMax.Guid.ToString(), v.CoreParkingMax.ToString()];
        yield return ["/setactive", "SCHEME_CURRENT"];
    }

    public static bool TryApply(CpuScheduleConfig config, out string status)
    {
        try
        {
            foreach (bool ac in new[] { true, false })
            {
                foreach (string[] args in BuildApplyArguments(config.For(ac), ac))
                {
                    if (!RunPowerCfg(args, out string error))
                    {
                        status = $"powercfg 失败（{string.Join(' ', args)}）：{error}";
                        return false;
                    }
                }
            }
            status = "CPU scheduling applied.";
            return true;
        }
        catch (Exception ex)
        {
            status = $"powercfg 调用失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>Reveals hidden SUB_PROCESSOR settings in the Windows power UI (restores them after).</summary>
    public static bool SetHiddenAttributes(bool hidden, out string status)
    {
        string flag = hidden ? "+ATTRIB_HIDE" : "-ATTRIB_HIDE";
        var failures = new List<string>();
        foreach (var setting in MainSettings.Concat(AdvancedSettings))
        {
            if (!RunPowerCfg(["-attributes", "SUB_PROCESSOR", setting.Guid.ToString(), flag], out string error))
                failures.Add($"{setting.Key}: {error}");
        }
        status = failures.Count == 0 ? $"hidden={hidden}" : string.Join("; ", failures);
        return failures.Count == 0;
    }

    private static bool RunPowerCfg(IEnumerable<string> arguments, out string error)
    {
        error = string.Empty;
        try
        {
            var info = new ProcessStartInfo("powercfg")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string arg in arguments) info.ArgumentList.Add(arg);
            using var process = Process.Start(info);
            if (process is null) { error = "could not start powercfg"; return false; }

            // W1: read both streams asynchronously (avoids the classic ReadToEnd/ReadToEnd pipe
            // deadlock) and kill the tree when the timeout expires instead of leaving a zombie.
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(4000))
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception killEx) { error = $"powercfg timed out and could not be killed: {killEx.Message}"; }
                error = string.IsNullOrEmpty(error) ? "powercfg timed out after 4000 ms" : error;
                return false;
            }
            if (!Task.WaitAll(new Task[] { stdoutTask, stderrTask }, 1000))
            {
                error = "powercfg output could not be drained after exit";
                return false;
            }
            string stdout = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : string.Empty;
            string stderr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : string.Empty;
            if (process.ExitCode == 0) return true;
            error = (stderr + stdout).Trim();
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
