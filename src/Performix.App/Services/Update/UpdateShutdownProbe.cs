// The App-side half of UpdateExitGuard's liveness probe.
//
// UpdateExitGuard is linked into the test project, so it cannot reference App / ShellWindow
// (Microsoft.UI.Xaml). It owns the decision (ShouldForceExit, unit-tested); this file reads the
// two facts the decision needs from the real objects, and nothing else.
//
// Deliberately app-only: it is NOT compiled into Performix.Tests.csproj.

namespace Predex.WinUI.Services.Update
{
    internal static class UpdateShutdownProbe
    {
        /// <summary>
        /// True when the install fallback still has to end the process: no exit was requested and
        /// the shell window is still sitting there. A slow-but-normal close reports false, so the
        /// 10 s grace cannot turn "the window is closing" into a hard kill.
        /// </summary>
        public static bool IsStuck()
        {
            bool exitRequested;
            bool? shellExitStarted;

            try
            {
                exitRequested = Predex.WinUI.App.IsExitRequested;
                shellExitStarted = Predex.WinUI.App.CurrentShell is { } shell ? shell.IsExiting : null;
            }
            catch
            {
                // If the shell cannot even be inspected, assume the hang this fallback exists for.
                return true;
            }

            return UpdateExitGuard.ShouldForceExit(exitRequested, shellExitStarted);
        }
    }
}
