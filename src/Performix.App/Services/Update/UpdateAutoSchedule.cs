// When the automatic update check runs — the timing half of the auto-update feature, kept as
// a pure function so the three questions the brief asks can be answered by a unit test instead
// of by watching a clock:
//
//   • the FIRST check waits out the startup burst (30–60 s, never "on the first frame"),
//   • every later check is one INTERVAL (6 h) after the previous one,
//   • with the switch off nothing is ever due — that is the "完全不联网" promise, and it is
//     decided here rather than by a caller that could forget it.
//
// No clock, no timer, no static state: the caller passes "now", the process start and the last
// completed check, so a test can drive a week of schedules in microseconds.
//
// The 45 s / 6 h numbers are deliberately constants and not settings: the brief fixes the order
// of magnitude (delay tens of seconds, re-check a few times a day), and a free-form interval
// would be a second thing to explain in the UI. They are exposed for the tests and the log line.

using System;

namespace Predex.WinUI.Services.Update
{
    /// <summary>Startup delay + repeat interval for the automatic update check.</summary>
    public static class UpdateAutoSchedule
    {
        /// <summary>Delay before the first automatic check (the brief: 30–60 s).</summary>
        public const double FirstDelaySeconds = 45;

        /// <summary>Delay between two automatic checks (the brief: 6 h).</summary>
        public const double IntervalSeconds = 6 * 60 * 60;

        /// <summary>The brief's lower bound for the startup delay; the tests pin the constant to it.</summary>
        public const double MinimumFirstDelaySeconds = 30;

        /// <summary>The brief's upper bound for the startup delay.</summary>
        public const double MaximumFirstDelaySeconds = 60;

        public static TimeSpan FirstDelay => TimeSpan.FromSeconds(FirstDelaySeconds);

        public static TimeSpan Interval => TimeSpan.FromSeconds(IntervalSeconds);

        /// <summary>
        /// When the next automatic check is due, or <c>null</c> when automatic checks are off.
        ///
        /// <paramref name="lastCheck"/> is null until a check has completed. An overdue stamp —
        /// the app was closed for a day, or the system clock jumped backwards — does NOT mean
        /// "run immediately": the startup delay still applies, because the whole point of the
        /// delay is to keep the network off the launch path. A stamp in the future (clock skew)
        /// simply postpones the next check by one interval instead of firing on every tick.
        /// </summary>
        public static DateTimeOffset? NextDue(bool enabled, DateTimeOffset startedAt, DateTimeOffset? lastCheck)
        {
            if (!enabled) return null;

            if (lastCheck is not { } last) return startedAt + FirstDelay;

            DateTimeOffset next = last + Interval;
            if (next <= startedAt) return startedAt + FirstDelay;
            return next;
        }

        /// <summary>True when the caller should start a check right now.</summary>
        public static bool IsDue(bool enabled, DateTimeOffset now, DateTimeOffset startedAt, DateTimeOffset? lastCheck)
            => NextDue(enabled, startedAt, lastCheck) is { } due && now >= due;

        /// <summary>How long to wait before the next look at the clock (never negative).</summary>
        public static TimeSpan DelayUntil(DateTimeOffset now, DateTimeOffset startedAt, DateTimeOffset? lastCheck, bool enabled)
        {
            if (NextDue(enabled, startedAt, lastCheck) is not { } due) return Interval;
            TimeSpan remaining = due - now;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        /// <summary>"first=45s interval=6h" — the log line names the schedule it is running.</summary>
        public static string Describe() =>
            $"first={FirstDelaySeconds:0}s interval={IntervalSeconds / 3600:0}h";    }
}
