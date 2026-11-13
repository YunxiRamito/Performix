using Predex.WinUI.Services.Update;

namespace Performix.Tests;

/// <summary>
/// The install fallback: the replacement script waits for this process to disappear, so when the
/// graceful quit does not end it something else must. These tests never call the real killing
/// action — it is injected.
/// </summary>
[TestClass]
public sealed class UpdateExitGuardTests
{
    [TestMethod]
    public async Task ForceExitIfStillRunning_ForcesWhenTheProcessIsStillAlive()
    {
        int forced = 0;

        bool didForce = await UpdateExitGuard.ForceExitIfStillRunningAsync(
            TimeSpan.Zero, static () => true, () => forced++);

        Assert.IsTrue(didForce);
        Assert.AreEqual(1, forced);
    }

    [TestMethod]
    public async Task ForceExitIfStillRunning_DoesNothingWhenTheGracefulExitWorked()
    {
        int forced = 0;

        bool didForce = await UpdateExitGuard.ForceExitIfStillRunningAsync(
            TimeSpan.Zero, static () => false, () => forced++);

        Assert.IsFalse(didForce);
        Assert.AreEqual(0, forced, "the normal exit path must never be overridden");
    }

    [TestMethod]
    public void ShouldForceExit_NormalCloseThatIsMerelySlow_IsNotForced()
    {
        // App.RequestExit already ran; the process just needs longer than the grace. The old
        // "static () => true" probe killed it here — the case this fix exists for.
        Assert.IsFalse(UpdateExitGuard.ShouldForceExit(exitRequested: true, shellExitStarted: false));
        Assert.IsFalse(UpdateExitGuard.ShouldForceExit(exitRequested: true, shellExitStarted: true));
        Assert.IsFalse(UpdateExitGuard.ShouldForceExit(exitRequested: true, shellExitStarted: null));
        // QuitApplication set its flag but the app-level exit has not been observed yet.
        Assert.IsFalse(UpdateExitGuard.ShouldForceExit(exitRequested: false, shellExitStarted: true));
    }

    [TestMethod]
    public void ShouldForceExit_ReallyHungShell_IsForced()
    {
        // The shell window is still open and nobody ever asked it to close: the hang the fallback
        // is for.
        Assert.IsTrue(UpdateExitGuard.ShouldForceExit(exitRequested: false, shellExitStarted: false));
        // No shell at all: nothing will close this process, so the script would wait forever.
        Assert.IsTrue(UpdateExitGuard.ShouldForceExit(exitRequested: false, shellExitStarted: null));
    }

    [TestMethod]
    public async Task SlowNormalClose_DoesNotForceExit()
    {
        int forced = 0;
        Func<bool> probe = () => UpdateExitGuard.ShouldForceExit(
            exitRequested: true, shellExitStarted: true);

        bool didForce = await UpdateExitGuard.ForceExitIfStillRunningAsync(
            TimeSpan.Zero, probe, () => forced++);

        Assert.IsFalse(didForce, "a closing window must be allowed to finish its own shutdown");
        Assert.AreEqual(0, forced);
    }

    [TestMethod]
    public async Task HungShell_ForcesExit()
    {
        int forced = 0;
        Func<bool> probe = () => UpdateExitGuard.ShouldForceExit(
            exitRequested: false, shellExitStarted: false);

        bool didForce = await UpdateExitGuard.ForceExitIfStillRunningAsync(
            TimeSpan.Zero, probe, () => forced++);

        Assert.IsTrue(didForce, "an exit request that never ran must not leave the script waiting");
        Assert.AreEqual(1, forced);
    }

    [TestMethod]
    public void DefaultGrace_IsLongEnoughToQuitAndShortEnoughNotToHang()
    {
        Assert.IsTrue(UpdateExitGuard.DefaultGrace > TimeSpan.FromSeconds(1));
        Assert.IsTrue(UpdateExitGuard.DefaultGrace <= TimeSpan.FromMinutes(1));
    }

    [TestMethod]
    public async Task ForceExitIfStillRunning_RejectsMissingArguments()
    {
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() =>
            UpdateExitGuard.ForceExitIfStillRunningAsync(TimeSpan.Zero, null!, () => { }));
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() =>
            UpdateExitGuard.ForceExitIfStillRunningAsync(TimeSpan.Zero, static () => true, null!));
    }
}
