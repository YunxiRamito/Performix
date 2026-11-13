using System;
using System.ServiceProcess;
using System.Threading;

namespace PreySense.Core.Hardware;

/// <summary>
/// Keeps the Predator Sense named pipe usable. The pipe server is the <c>PSSvc</c> service
/// (DisplayName "Predator Service", StartMode=Manual): once it is stopped the pipe disappears and
/// both sensor telemetry and operating-mode switching stop working.
/// </summary>
/// <remarks>
/// This only starts an existing, OEM-installed service — it never installs, deletes or reconfigures
/// anything. Starting a service requires an elevated process; a failure is reported as a message so
/// the UI can tell the user that PSSvc is needed.
/// </remarks>
public static class PredatorPipeService
{
    public const string ServiceName = "PSSvc";
    public const string DisplayName = "Predator Service";

    /// <summary>Outcome of <see cref="EnsureAvailable"/>.</summary>
    public readonly record struct EnsureResult(bool Available, bool StartedByUs, string Status);

    /// <summary>
    /// Probes the pipe; if it is unreachable, checks <c>PSSvc</c> and starts it, then reconnects.
    /// Blocking — call from a background thread.
    /// </summary>
    public static EnsureResult EnsureAvailable(AcerPipeSensorSource probe, int startWaitMs = 8000)
    {
        probe.Probe(connectTimeoutMs: 1500);
        if (probe.IsPipeAvailable)
            return new EnsureResult(true, false, "The Predator service pipe is available.");

        try
        {
            using var controller = new ServiceController(ServiceName);
            ServiceControllerStatus status;
            try { status = controller.Status; }
            catch (Exception ex)
            {
                return new EnsureResult(false, false,
                    $"The Predator service ({ServiceName}) could not be queried: {ex.Message}");
            }

            if (status != ServiceControllerStatus.Running && status != ServiceControllerStatus.StartPending)
            {
                try
                {
                    controller.Start();
                    controller.WaitForStatus(ServiceControllerStatus.Running,
                        TimeSpan.FromMilliseconds(Math.Max(1000, startWaitMs)));
                }
                catch (Exception ex)
                {
                    return new EnsureResult(false, false,
                        $"The Predator service ({ServiceName}) is {status} and could not be started " +
                        $"({ex.GetType().Name}: {ex.Message}). Run PreySense as Administrator, or start " +
                        $"'{DisplayName}' ({ServiceName}) manually.");
                }
            }
            else if (status == ServiceControllerStatus.StartPending)
            {
                try
                {
                    controller.WaitForStatus(ServiceControllerStatus.Running,
                        TimeSpan.FromMilliseconds(Math.Max(1000, startWaitMs)));
                }
                catch { /* fall through to the pipe re-probe below */ }
            }
        }
        catch (Exception ex)
        {
            return new EnsureResult(false, false,
                $"The Predator service ({ServiceName}) is unavailable: {ex.Message}. " +
                $"Start '{DisplayName}' ({ServiceName}) or run PreySense as Administrator.");
        }

        // The pipe can lag the service by a moment; retry for a few seconds.
        for (int attempt = 0; attempt < 12; attempt++)
        {
            probe.Probe(connectTimeoutMs: 800);
            if (probe.IsPipeAvailable)
                return new EnsureResult(true, true,
                    $"The Predator service ({ServiceName}) was started; the pipe is available now.");
            Thread.Sleep(300);
        }

        return new EnsureResult(false, true,
            $"The Predator service ({ServiceName}) is running but its named pipe is still unreachable.");
    }

    /// <summary>Read-only description of the service state, for diagnostics and the probe.</summary>
    public static string DescribeStatus()
    {
        try
        {
            using var controller = new ServiceController(ServiceName);
            return $"{ServiceName} ({DisplayName}): {controller.Status}";
        }
        catch (Exception ex)
        {
            return $"{ServiceName} ({DisplayName}): unavailable ({ex.GetType().Name}: {ex.Message})";
        }
    }
}
