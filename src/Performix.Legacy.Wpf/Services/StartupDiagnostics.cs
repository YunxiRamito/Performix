using System.Runtime.InteropServices;

namespace PreySense.Frontend.Services;

internal static class StartupDiagnostics
{
    internal static string CreateReport(Exception exception)
    {
        AppLogger.Error($"PreySense {typeof(StartupDiagnostics).Assembly.GetName().Version} startup/application failure\n" +
            $"{RuntimeInformation.OSDescription}\n{RuntimeInformation.FrameworkDescription}\n{exception}");
        return "Performix could not start.\n\n" + exception.Message + "\n\nError log: " + AppLogger.LogPath;
    }
}
