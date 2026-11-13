namespace PreySense.Core.Hardware;

/// <summary>Owns the application's single bundled-driver connection.</summary>
public static class CpuDriverRuntime
{
    /// <summary>Message surfaced by the UI when the CPU driver layer is not compiled in.</summary>
    public const string KernelDriverRemovedNotice =
        "本机不支持：本分支已移除内核驱动（CPU PL1/PL2、GPU 功率、PROCHOT 调参不可用）";

#if PREYSENSE_NO_DRIVER
    /// <summary>False in the no-driver fork (compile-time decision, not a runtime probe).</summary>
    public static bool IsKernelDriverSupported => false;
#else
    /// <summary>True when the build was compiled with the bundled kernel-driver layer.</summary>
    public static bool IsKernelDriverSupported => true;
#endif

    private static readonly object Gate = new();
    private static PreySenseDriverBackend? _backend;
    private static CpuTelemetryReader? _telemetry;
    private static CpuPowerLimitController? _powerLimits;
    private static bool _attempted;
    public static string Status { get; private set; } = "CPU driver has not been initialized.";

    public static CpuTelemetryReader? Telemetry { get { Initialize(); return _telemetry; } }
    public static CpuPowerLimitController? PowerLimits { get { Initialize(); return _powerLimits; } }
    public static PreySenseDriverBackend? Backend { get { Initialize(); return _backend; } }

    private static void Initialize()
    {
        lock (Gate)
        {
            if (_attempted) return;
            _attempted = true;
#if PREYSENSE_NO_DRIVER
            // No-driver fork: never construct the backend, service or device operations.
            Status = KernelDriverRemovedNotice;
            return;
#else
            var backend = new PreySenseDriverBackend();
            try
            {
                if (!backend.Initialize(out var error, attemptStartService: true))
                {
                    Status = error ?? "CPU driver is unavailable.";
                    backend.Dispose();
                    return;
                }
                _telemetry = new CpuTelemetryReader(backend);
                _powerLimits = new CpuPowerLimitController(backend);
                _backend = backend;
                Status = "CPU driver is ready.";
            }
            catch (Exception ex)
            {
                backend.Dispose();
                Status = $"CPU driver initialization failed: {ex.Message}";
            }
#endif
        }
    }

    public static void Dispose()
    {
        lock (Gate)
        {
            _telemetry?.Dispose();
            _backend?.Dispose();
            _telemetry = null;
            _powerLimits = null;
            _backend = null;
            _attempted = true;
            Status = "CPU driver is closed.";
        }
    }
}
