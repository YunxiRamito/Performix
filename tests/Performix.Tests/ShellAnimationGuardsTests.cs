// Regression tests for the two "stale animation callback" hazards the shell hit:
//   * a hide completion that outlives a show (problem 3), and
//   * a size-animation tick that outlives its own round and stops the next one (problem 4).
//
// The WinUI animation itself is not under test here — the token arithmetic that decides
// "may this callback still touch the window?" is, and that is the part that was wrong.

using Predex.WinUI.Shell;

namespace PreySense.Tests;

[TestClass]
public class ShellAnimationGuardsTests
{
    // ── HideCompletionGate (problem 3) ────────────────────────────────────────────

    [TestMethod]
    public void ArmedHide_IsStillAllowedToHideWhenNothingShowsInBetween()
    {
        var gate = new HideCompletionGate();
        long hide = gate.Arm();

        Assert.IsTrue(gate.IsArmed(hide));
    }

    [TestMethod]
    public void ShowInsideTheHideAnimation_CancelsThePendingHide()
    {
        // Arm() = hide animation started (160 ms), CancelPending() = ShowAndActivate while it
        // is still playing. The completion callback must then be a no-op: it used to call
        // AppWindow.Hide() and ReleasePageTree() on the window the user had just summoned.
        var gate = new HideCompletionGate();
        long hide = gate.Arm();

        gate.CancelPending();

        Assert.IsFalse(gate.IsArmed(hide), "the pending hide must not hide the window again");
    }

    [TestMethod]
    public void CancelWithoutAHideInFlight_DoesNotArmAnything()
    {
        var gate = new HideCompletionGate();
        long first = gate.Arm();
        gate.CancelPending();
        long second = gate.Arm();

        Assert.IsFalse(gate.IsArmed(first));
        Assert.IsTrue(gate.IsArmed(second));
    }

    [TestMethod]
    public void TwoHides_OnlyTheSecondOneMayFinish()
    {
        var gate = new HideCompletionGate();
        long first = gate.Arm();
        long second = gate.Arm();

        Assert.IsFalse(gate.IsArmed(first), "the superseded hide must not release the page tree");
        Assert.AreEqual(first, second - 1, "tokens are strictly increasing generations");
        Assert.IsTrue(gate.IsArmed(second));
    }

    // ── SizeAnimationRound (problem 4) ────────────────────────────────────────────

    [TestMethod]
    public void FirstRound_OwnsTheTimerAndRetiresItselfOnce()
    {
        var round = new SizeAnimationRound();
        long first = round.Begin();

        Assert.IsTrue(round.IsOwner(first));
        Assert.IsTrue(round.Release(first), "the owning round stops the timer");
        Assert.IsFalse(round.IsOwner(first));
        Assert.IsFalse(round.Release(first), "releasing twice must not stop a later round");
    }

    [TestMethod]
    public void OverlappingRound_TakesOwnershipFromTheOldOne()
    {
        // The 220 ms case: navigation B starts while the expand animation of A is mid-flight.
        var round = new SizeAnimationRound();
        long expand = round.Begin();
        long navigate = round.Begin();

        Assert.IsFalse(round.IsOwner(expand), "the old tick must not write the window size");
        Assert.IsFalse(round.Release(expand), "the old tick must not stop the new round's timer");
        Assert.IsTrue(round.IsOwner(navigate));
    }

    [TestMethod]
    public void OldRoundFinishingAfterTheNewOne_LeavesTheNewRoundRunning()
    {
        var round = new SizeAnimationRound();
        long expand = round.Begin();
        long navigate = round.Begin();

        // Old handler's last tick runs after the new round started: it may not stop anything,
        // which is exactly the "window stuck at half height" bug.
        bool oldStopped = round.Release(expand);
        Assert.IsFalse(oldStopped);
        Assert.IsTrue(round.IsOwner(navigate));

        Assert.IsTrue(round.Release(navigate));
        Assert.IsFalse(round.IsOwner(navigate));
    }

    [TestMethod]
    public void Cancel_RetiresTheLiveRound()
    {
        // The instant-resize path (reduced motion / animate:false) cancels before applying the
        // target size, so a leftover tick cannot drag the window back to a half-way height.
        var round = new SizeAnimationRound();
        long expand = round.Begin();

        round.Cancel();

        Assert.IsFalse(round.IsOwner(expand));
        Assert.IsFalse(round.Release(expand));
    }

    [TestMethod]
    public void BeginAfterCancel_IsANewLiveRound()
    {
        var round = new SizeAnimationRound();
        long old = round.Begin();
        round.Cancel();
        long fresh = round.Begin();

        Assert.IsFalse(round.IsOwner(old));
        Assert.AreNotEqual(old, fresh);
        Assert.IsTrue(round.IsOwner(fresh));
    }
}
