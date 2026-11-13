using PreySense.Gpu;

namespace PreySense.Frontend.ViewModels;

public partial class MainViewModel
{
    internal IGpuClockTuning? GpuClockTuningController { get; set; }
    private IGpuClockTuning ClockTuning => GpuClockTuningController ?? NvidiaGpuControl.Shared;

    public GpuClockTuningState? GpuClockTuningState { get; private set; }
    public bool IsGpuClockTuningAvailable => GpuClockTuningState is not null;

    /// <summary>Why GPU clock tuning is unavailable (probe status), or the last read status when it is.</summary>
    public string? GpuClockTuningStatus { get; private set; }

    // No native calls in binding getters and no extra polling timer.
    internal async Task RefreshGpuClockTuningAsync()
    {
        string? status = null;
        GpuClockTuningState = await Task.Run(() =>
        {
            try
            {
                if (ClockTuning.TryReadClockTuning(out var state, out string readStatus))
                {
                    status = readStatus;
                    return state;
                }
                status = readStatus;
                return null;
            }
            catch (Exception ex)
            {
                status = ex.Message;
                AppLogger.Error($"GPU overclocking discovery failed: {ex.Message}");
                return null;
            }
        });
        GpuClockTuningStatus = status;
        OnPropertyChanged(nameof(GpuClockTuningState));
        OnPropertyChanged(nameof(IsGpuClockTuningAvailable));
        OnPropertyChanged(nameof(GpuClockTuningStatus));
        OnPropertyChanged(nameof(GpuClockTuningUnavailableReason));
    }

    /// <summary>
    /// User-facing reason shown when the GPU overclocking block is hidden.
    /// </summary>
    public string GpuClockTuningUnavailableReason =>
        string.IsNullOrWhiteSpace(GpuClockTuningStatus)
            ? "GPU clock offsets are not supported by this GPU/driver."
            : $"GPU clock offsets are unavailable: {GpuClockTuningStatus}";

    /// <summary>
    /// Restores the NVIDIA core/memory clock offsets to 0/0 MHz — pure NVAPI, no kernel driver —
    /// and clears every saved per-mode GPU clock offset so nothing is replayed later.
    /// </summary>
    internal async Task<DriverTuningResult> ResetGpuClockOffsetsAsync()
    {
        var result = await Task.Run(() =>
        {
            try
            {
                if (!ClockTuning.TryReadClockTuning(out var state, out string readStatus) || state is null)
                    return new DriverTuningResult(false, readStatus);
                if (!ClockTuning.TryApplyClockOffsets(new GpuClockOffsets(0, 0), out string applyStatus))
                    return new DriverTuningResult(false, applyStatus);
                return new DriverTuningResult(true, applyStatus);
            }
            catch (Exception ex)
            {
                return new DriverTuningResult(false, ex.Message);
            }
        });

        if (result.Success)
        {
            try
            {
                TuningStore.Update(cfg =>
                {
                    foreach (var profile in new[]
                             {
                                 cfg.Tuning.Eco, cfg.Tuning.Quiet, cfg.Tuning.Balanced,
                                 cfg.Tuning.Performance, cfg.Tuning.Turbo
                             })
                    {
                        profile.ApplyGpuClockOffsets = false;
                        profile.GpuCoreClockOffsetMhz = null;
                        profile.GpuMemoryClockOffsetMhz = null;
                    }
                });
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Could not clear saved GPU clock offsets: {ex.Message}");
            }
        }

        return result;
    }

    private DriverTuningResult ExecuteRequestedTuning(DriverTuningRequest request,
        bool applyClocks, GpuClockOffsets? offsets)
    {
        // Validate and apply clocks first so a clock failure cannot leave watts/CPU already written.
        if (applyClocks)
        {
            if (offsets is null) return new(false, "Set GPU core and memory offsets before applying.");
            if (!ClockTuning.TryReadClockTuning(out var state, out string status)) return new(false, status);
            if (!state!.Allows(offsets.Value)) return new(false, "GPU clock offsets are outside the supported range.");
            if (!ClockTuning.TryApplyClockOffsets(offsets.Value, out string clockStatus))
                return new(false, clockStatus);
        }
        bool hasDriverWrites = request.ApplyCpuLimits || request.ApplyGpuLimits || request.ProchotOffsetCelsius.HasValue;
        if (!hasDriverWrites)
            return applyClocks
                ? new(true, "GPU clock offsets applied.")
                : new(true, "Applied and read back successfully.");
        return ExecuteDriverTuning(request);
    }
}
