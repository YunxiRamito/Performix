using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PreySense.Core.Configuration;
using PreySense.Mode;

namespace PreySense.Frontend.ViewModels;

public partial class MainViewModel
{
    private static readonly string[] ModeNames = ["Quiet", "Eco", "Balanced", "Performance", "Turbo"];

    private static string ModeName(byte mode) => mode switch
    {
        0 => "Quiet",
        6 => "Eco",
        4 => "Performance",
        5 => "Turbo",
        _ => "Balanced"
    };

    /// <summary>Saved CPU scheduling profile for a mode (falls back to the mode defaults).</summary>
    internal CpuScheduling.CpuScheduleConfig CpuScheduleForMode(byte mode)
    {
        var tuning = (TuningConfigurationReader?.Invoke() ?? TuningStore.GetCurrent()).Tuning;
        return tuning.CpuSchedules.TryGetValue(ModeName(mode), out var saved) && saved is not null
            ? saved
            : CpuScheduling.CpuScheduleConfig.DefaultForMode(mode);
    }

    internal void SaveCpuScheduleForMode(byte mode, CpuScheduling.CpuScheduleConfig schedule)
    {
        TuningStore.Update(cfg => cfg.Tuning.CpuSchedules[ModeName(mode)] = schedule);
    }

    /// <summary>
    /// Applies the mode's AC/DC SUB_PROCESSOR values to the current scheme. Only value indices of the
    /// active scheme change — the scheme itself is never created, deleted or replaced.
    /// </summary>
    internal async Task<(bool Success, string Status)> ApplyCpuScheduleForModeAsync(byte mode)
    {
        var schedule = CpuScheduleForMode(mode);
        return await Task.Run(() =>
        {
            bool ok = CpuScheduling.TryApply(schedule, out string status);
            if (ok) AppLogger.Log($"[CpuSchedule] mode {ModeName(mode)} applied");
            else AppLogger.Error($"[CpuSchedule] {status}");
            return (ok, status);
        });
    }

    /// <summary>Restores Windows defaults for the settings this page owns and clears saved schedules.</summary>
    internal async Task<(bool Success, string Status)> ResetCpuScheduleToWindowsDefaultsAsync()
    {
        const int windowsMaxState = 100, windowsBoost = 1, windowsEpp = 0, windowsCores = 100;
        var defaults = new CpuScheduling.CpuScheduleConfig
        {
            Ac = CpuScheduling.CpuScheduleValues.Default(windowsMaxState, windowsBoost, windowsEpp, windowsCores, windowsCores),
            Dc = CpuScheduling.CpuScheduleValues.Default(windowsMaxState, windowsBoost, windowsEpp, windowsCores, windowsCores)
        };
        var result = await Task.Run(() =>
        {
            bool ok = CpuScheduling.TryApply(defaults, out string status);
            return (ok, status);
        });
        if (result.ok)
        {
            TuningStore.Update(cfg =>
            {
                foreach (string name in ModeNames) cfg.Tuning.CpuSchedules.Remove(name);
                cfg.Tuning.CpuScheduleBaselineAc = null;
                cfg.Tuning.CpuScheduleBaselineDc = null;
            });
        }
        return (result.ok, result.status);
    }

    /// <summary>True when the mode has an explicitly saved CPU schedule (switching modes applies it).</summary>
    private bool HasCpuSchedule(byte mode) =>
        (TuningConfigurationReader?.Invoke() ?? TuningStore.GetCurrent()).Tuning.CpuSchedules.ContainsKey(ModeName(mode));
}
