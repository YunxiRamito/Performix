// "Is now a bad moment to restart the app?" — the decision that keeps an automatic install from
// interrupting the user. Pure on purpose: every input is passed in, so the four ways this can be
// wrong (a dialog is open, a mode switch / tuning apply is running, the user is typing or
// dragging right now, the user just turned the feature off) are unit-testable without a shell,
// a dispatcher or a mouse.
//
// WHY A TIME WINDOW AND NOT JUST FLAGS: the shell has no "the user is editing a fan curve" flag
// to read — the curve editor writes straight into the ViewModel on every pointer move and only
// commits through the debounced Auto Apply. System-wide idle time (GetLastInputInfo, read by the
// caller) is the honest signal for that case: while the user is dragging a curve point, moving
// the mouse or typing, the machine is not idle, so the install keeps waiting. A dedicated flag
// would have been a second source of truth that only some pages set.
//
// NOT QUIET means "ask again shortly", never "give up": the caller polls, and only a bounded
// overall wait turns into a reported failure.

using System;

namespace Predex.WinUI.Services.Update
{
    /// <summary>Everything the quiet-window decision needs, captured at one instant.</summary>
    public readonly record struct UpdateQuietInputs(
        bool AutoUpdateEnabled,
        bool DialogOpen,
        bool ShellBusy,
        double IdleSeconds);

    /// <summary>The answer plus a stable English reason for the log line.</summary>
    public readonly record struct UpdateQuietDecision(bool Quiet, string Reason)
    {
        public static UpdateQuietDecision QuietNow { get; } = new(true, "quiet: nothing is in the way");
    }

    /// <summary>Decides whether an automatic install may take the app down right now.</summary>
    public static class UpdateQuietWindow
    {
        /// <summary>
        /// How long the machine has to have been idle. 15 s is longer than any single drag /
        /// keypress gap and much shorter than "the user stepped away", so a deliberate install
        /// still happens within one poll cycle of the user stopping.
        /// </summary>
        public const double RequiredIdleSeconds = 15;

        public static UpdateQuietDecision Evaluate(UpdateQuietInputs inputs)
        {
            // The switch wins over everything: turning it off mid-download must stop the install
            // that is already waiting, not just the next check.
            if (!inputs.AutoUpdateEnabled)
                return new(false, "automatic updates were turned off while waiting to install");

            if (inputs.DialogOpen)
                return new(false, "a dialog is open (the user is answering something)");

            if (inputs.ShellBusy)
                return new(false, "a mode switch / tuning apply is running");

            if (inputs.IdleSeconds < RequiredIdleSeconds)
                return new(false, $"the user is active (idle {inputs.IdleSeconds:0.0}s < {RequiredIdleSeconds:0}s)");

            return UpdateQuietDecision.QuietNow;
        }

        /// <summary>"requires 15s idle, no dialog, no shell work" — for the log line.</summary>
        public static string Describe() =>
            $"requires {RequiredIdleSeconds:0}s idle, no dialog, no shell work";
    }
}
