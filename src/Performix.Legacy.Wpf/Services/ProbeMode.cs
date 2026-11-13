using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PreySense.Core.Diagnostics;

namespace PreySense.Frontend.Services;

/// <summary>
/// <c>--probe</c> command-line bypass: prints the read-only sensor self-check and exits without
/// creating any window, hardware service, tray icon or overlay. Used for acceptance runs.
/// </summary>
internal static class ProbeMode
{
    private const int AttachParentProcess = -1;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareReadWrite = 0x1 | 0x2;
    private const uint OpenExisting = 3;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    public static bool IsProbeRequested(string[] args) =>
        args.Any(a => a.Equals("--probe", StringComparison.OrdinalIgnoreCase)
                      || a.Equals("-probe", StringComparison.OrdinalIgnoreCase)
                      || a.Equals("/probe", StringComparison.OrdinalIgnoreCase));

    /// <summary>Runs the probe and returns the process exit code (0 = fan-curve telemetry ready).</summary>
    public static int Run(string[] args)
    {
        EnsureConsole();

        var (report, pipeOk, fanCurveReady) = AcerSensorProbe.Run();
        Console.Write(report);

        string? outputPath = args.Skip(1)
            .FirstOrDefault(a => !a.StartsWith('-') && !a.StartsWith('/'));
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            outputPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "PreySense", "logs", $"sensor-probe-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            File.WriteAllText(outputPath, report);
            Console.WriteLine();
            Console.WriteLine($"report saved: {outputPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"could not write report to '{outputPath}': {ex.GetType().Name}: {ex.Message}");
        }

        Console.Out.Flush();
        return fanCurveReady ? 0 : (pipeOk ? 3 : 2);
    }

    /// <summary>
    /// Reattaches stdout to the launching console. A WinExe has no console, so without this the
    /// probe report would silently go nowhere when started from a terminal.
    /// </summary>
    private static void EnsureConsole()
    {
        try
        {
            if (!AttachConsole(AttachParentProcess))
                AllocConsole();

            SafeFileHandle handle = CreateFileW(
                "CONOUT$", GenericWrite, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                return;
            }

            var writer = new StreamWriter(new FileStream(handle, FileAccess.Write), new UTF8Encoding(false))
            {
                AutoFlush = true
            };
            Console.SetOut(writer);
        }
        catch
        {
            // No console available (e.g. started by a service/host); the report is still written
            // to the file path printed at the end.
        }
    }
}
