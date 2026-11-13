// Pure state behind the shell's two overlapping animations. No WinUI / DispatcherQueue types
// here on purpose: the test project compiles this file directly (same technique as
// Services\WindowVisibilitySession.cs), so the "does this stale callback still own the
// window?" decision is testable without a message loop.
//
// Both hazards are the same shape — a callback from an older animation outliving the
// animation that spawned it — and both are decided by a generation number instead of by a
// timing guess:
//
//   * the hide animation's completion callback survives a show inside its 160 ms and then
//     calls AppWindow.Hide() + ReleasePageTree() on a window the user just summoned
//     (ShellWindow.HideWindow);
//   * a size-animation tick handler survives the round it belongs to, calls timer.Stop() and
//     so kills the *next* round's timer, then writes its own half-way height
//     (ShellWindow.AnimateWindowSize).
//
// Threading: the UI dispatcher serialises the actual work, but ShowAndActivate may be entered
// from the cross-process show thread, so the fields are touched with Interlocked/Volatile.

using System.Threading;

namespace Predex.WinUI.Shell
{
    /// <summary>
    /// Generation token for the hide animation's completion callback: only the newest hide may
    /// finish, and a show cancels whatever hide is outstanding.
    /// </summary>
    public sealed class HideCompletionGate
    {
        private long _generation;

        /// <summary>Starts a hide; the completion callback must present the returned token.</summary>
        public long Arm() => Interlocked.Increment(ref _generation);

        /// <summary>
        /// Makes every outstanding completion callback stale. Called before the window is shown
        /// again: the show owns the window from that point, so a hide that is still playing its
        /// 160 ms must not hide it or drop the page tree afterwards.
        /// </summary>
        public void CancelPending() => Interlocked.Increment(ref _generation);

        /// <summary>True when <paramref name="token"/> is still the newest, un-cancelled hide.</summary>
        public bool IsArmed(long token) => Interlocked.Read(ref _generation) == token;
    }

    /// <summary>
    /// Ownership of the one window-size timer. Each animation round takes a token; only the
    /// round that still owns it may write the window size, stop the timer or unsubscribe.
    /// </summary>
    public sealed class SizeAnimationRound
    {
        private long _generation;
        private long _owner;

        /// <summary>Claims the timer for a new round. The previous round's token is dead.</summary>
        public long Begin()
        {
            long token = Interlocked.Increment(ref _generation);
            Interlocked.Exchange(ref _owner, token);
            return token;
        }

        /// <summary>Marks the current round dead (the caller already detached its handler).</summary>
        public void Cancel() => Interlocked.Exchange(ref _owner, 0);

        /// <summary>True while the round that took <paramref name="token"/> is the live one.</summary>
        public bool IsOwner(long token)
        {
            long owner = Interlocked.Read(ref _owner);
            return owner != 0 && owner == token;
        }

        /// <summary>
        /// Retires <paramref name="token"/>. False when a newer round already owns the timer —
        /// that round, not this one, is who gets to stop the timer.
        /// </summary>
        public bool Release(long token)
        {
            if (!IsOwner(token)) return false;
            Interlocked.Exchange(ref _owner, 0);
            return true;
        }
    }
}
