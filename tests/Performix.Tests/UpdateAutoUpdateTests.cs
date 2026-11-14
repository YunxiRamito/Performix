// Automatic updates: the schedule, the quiet-window gate and the persisted switch.
//
// What is worth pinning (the rest of the feature is a UI over these three answers):
//   • the first check is NOT on the launch path (30–60 s after startup),
//   • the interval is 6 h, and an overdue stamp does not mean "run on the first tick",
//   • off means never — no due time at all, so nothing can accidentally reach the network,
//   • the install waits for a quiet machine (no dialog, no mode switch / tuning apply, and real
//     input idle), and refuses when the feature was switched off while waiting,
//   • the switch defaults to ON and round-trips through preysense.json.

using Predex.WinUI.Services.Update;

namespace PreySense.Tests;

[TestClass]
public sealed class UpdateAutoUpdateTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);

    // ── Schedule: first delay ────────────────────────────────────────────────
    [TestMethod]
    public void FirstDelay_IsInsideTheBriefsWindow()
    {
        Assert.IsTrue(UpdateAutoSchedule.FirstDelaySeconds >= UpdateAutoSchedule.MinimumFirstDelaySeconds,
            $"first delay {UpdateAutoSchedule.FirstDelaySeconds}s is shorter than the brief's {UpdateAutoSchedule.MinimumFirstDelaySeconds}s");
        Assert.IsTrue(UpdateAutoSchedule.FirstDelaySeconds <= UpdateAutoSchedule.MaximumFirstDelaySeconds,
            $"first delay {UpdateAutoSchedule.FirstDelaySeconds}s is longer than the brief's {UpdateAutoSchedule.MaximumFirstDelaySeconds}s");
    }

    [TestMethod]
    public void Interval_IsSixHours()
    {
        Assert.AreEqual(6.0, UpdateAutoSchedule.Interval.TotalHours, 0.001);
    }

    [TestMethod]
    public void NoCompletedCheckYet_FirstCheckWaitsOutTheStartupBurst()
    {
        DateTimeOffset? due = UpdateAutoSchedule.NextDue(enabled: true, Start, lastCheck: null);

        Assert.AreEqual(Start + UpdateAutoSchedule.FirstDelay, due);
        Assert.IsFalse(UpdateAutoSchedule.IsDue(true, Start, Start, null),
            "the first check must not be due on the first tick");
        Assert.IsFalse(UpdateAutoSchedule.IsDue(true, Start.AddSeconds(UpdateAutoSchedule.FirstDelaySeconds - 1), Start, null));
        Assert.IsTrue(UpdateAutoSchedule.IsDue(true, Start.AddSeconds(UpdateAutoSchedule.FirstDelaySeconds), Start, null));
    }

    // ── Schedule: interval ──────────────────────────────────────────────────
    [TestMethod]
    public void AfterACheck_TheNextOneIsOneIntervalLater()
    {
        DateTimeOffset last = Start.AddSeconds(UpdateAutoSchedule.FirstDelaySeconds);
        DateTimeOffset? due = UpdateAutoSchedule.NextDue(true, Start, last);

        Assert.AreEqual(last + UpdateAutoSchedule.Interval, due);
        Assert.IsFalse(UpdateAutoSchedule.IsDue(true, last.AddHours(5), Start, last));
        Assert.IsTrue(UpdateAutoSchedule.IsDue(true, last.AddHours(6), Start, last));
    }

    [TestMethod]
    public void AnOverdueStamp_StillRespectsTheStartupDelay()
    {
        // The app was closed for two days: the previous check is long overdue, but "overdue" must
        // not mean "hit the network during launch".
        DateTimeOffset last = Start.AddDays(-2);
        DateTimeOffset? due = UpdateAutoSchedule.NextDue(true, Start, last);

        Assert.AreEqual(Start + UpdateAutoSchedule.FirstDelay, due);
        Assert.IsFalse(UpdateAutoSchedule.IsDue(true, Start, Start, last));
    }

    [TestMethod]
    public void AStampInTheFuture_PostponesInsteadOfFiringEveryTick()
    {
        DateTimeOffset last = Start.AddHours(3);          // clock skew / a file copied forward
        DateTimeOffset? due = UpdateAutoSchedule.NextDue(true, Start, last);

        Assert.AreEqual(last + UpdateAutoSchedule.Interval, due);
        Assert.IsFalse(UpdateAutoSchedule.IsDue(true, Start, Start, last));
    }

    // ── Schedule: off means never ───────────────────────────────────────────
    [TestMethod]
    public void Disabled_IsNeverDue()
    {
        Assert.IsNull(UpdateAutoSchedule.NextDue(false, Start, null));
        Assert.IsNull(UpdateAutoSchedule.NextDue(false, Start, Start.AddDays(-1)));

        for (int hour = 0; hour < 48; hour++)
            Assert.IsFalse(UpdateAutoSchedule.IsDue(false, Start.AddHours(hour), Start, null),
                $"a disabled schedule must never fire (hour {hour})");
    }

    [TestMethod]
    public void DelayUntil_IsNeverNegative_AndCappedWhenOff()
    {
        Assert.AreEqual(TimeSpan.Zero, UpdateAutoSchedule.DelayUntil(Start.AddDays(1), Start, null, enabled: true));
        Assert.AreEqual(UpdateAutoSchedule.Interval, UpdateAutoSchedule.DelayUntil(Start, Start, null, enabled: false));
        Assert.AreEqual(UpdateAutoSchedule.FirstDelay, UpdateAutoSchedule.DelayUntil(Start, Start, null, enabled: true));
    }

    // ── Quiet window ────────────────────────────────────────────────────────
    private static UpdateQuietInputs Quiet(bool enabled = true, bool dialog = false, bool busy = false,
        double idleSeconds = 60)
        => new(enabled, dialog, busy, idleSeconds);

    [TestMethod]
    public void Quiet_WhenNothingIsInTheWay()
    {
        UpdateQuietDecision decision = UpdateQuietWindow.Evaluate(Quiet());

        Assert.IsTrue(decision.Quiet);
        Assert.AreEqual(UpdateQuietWindow.RequiredIdleSeconds, 15.0, 0.001);
    }

    [TestMethod]
    public void NotQuiet_WhenADialogIsOpen()
    {
        UpdateQuietDecision decision = UpdateQuietWindow.Evaluate(Quiet(dialog: true));

        Assert.IsFalse(decision.Quiet);
        StringAssert.Contains(decision.Reason, "dialog");
    }

    [TestMethod]
    public void NotQuiet_WhenAModeSwitchOrTuningApplyIsRunning()
    {
        UpdateQuietDecision decision = UpdateQuietWindow.Evaluate(Quiet(busy: true));

        Assert.IsFalse(decision.Quiet);
        StringAssert.Contains(decision.Reason, "running");
    }

    [TestMethod]
    public void NotQuiet_WhileTheUserIsTypingOrDragging()
    {
        UpdateQuietDecision decision = UpdateQuietWindow.Evaluate(Quiet(idleSeconds: UpdateQuietWindow.RequiredIdleSeconds - 0.1));

        Assert.IsFalse(decision.Quiet);
        StringAssert.Contains(decision.Reason, "active");
    }

    [TestMethod]
    public void Quiet_AtExactlyTheIdleThreshold()
    {
        Assert.IsTrue(UpdateQuietWindow.Evaluate(Quiet(idleSeconds: UpdateQuietWindow.RequiredIdleSeconds)).Quiet);
    }

    [TestMethod]
    public void SwitchingTheFeatureOff_StopsAnInstallThatIsAlreadyWaiting()
    {
        // This is the case the gate exists for: the download finished, the user turned the switch
        // off while the card said "waiting", and nothing may restart the app afterwards.
        UpdateQuietDecision decision = UpdateQuietWindow.Evaluate(Quiet(enabled: false, idleSeconds: 3600));

        Assert.IsFalse(decision.Quiet);
        StringAssert.Contains(decision.Reason, "turned off");
    }

    [TestMethod]
    public void EveryReasonIsASentence_TheLogCanQuote()
    {
        foreach (UpdateQuietInputs inputs in new[]
                 {
                     Quiet(enabled: false), Quiet(dialog: true), Quiet(busy: true), Quiet(idleSeconds: 0),
                 })
        {
            UpdateQuietDecision decision = UpdateQuietWindow.Evaluate(inputs);
            Assert.IsFalse(decision.Quiet);
            Assert.IsFalse(string.IsNullOrWhiteSpace(decision.Reason));
            Assert.IsFalse(decision.Reason.EndsWith('.'), "the reason is a clause, not a sentence");
        }
    }

    // ── Persisted switch ────────────────────────────────────────────────────
    [TestMethod]
    public void AutoUpdate_DefaultsToOn()
    {
        Assert.IsTrue(UpdatePreferences.DefaultAutoUpdate);
        Assert.IsTrue(new PreySense.Core.Configuration.PreySenseConfig().General.AutoUpdate);
    }

    [TestMethod]
    public void AutoUpdate_RoundTripsThroughTheConfigurationFile()
    {
        try
        {
            Assert.IsTrue(UpdatePreferences.PersistedAutoUpdate, "a fresh profile starts with automatic updates on");

            // SetAutoUpdate returns the value that is now STORED, so a successful write echoes it.
            Assert.IsFalse(UpdatePreferences.SetAutoUpdate(false));
            Assert.IsFalse(UpdatePreferences.PersistedAutoUpdate, "the switch must read back as off");

            PreySense.Core.Configuration.PreySenseConfig reloaded =
                PreySense.Core.Configuration.ConfigurationService.Instance.Reload();
            Assert.IsFalse(reloaded.General.AutoUpdate, "the answer must survive a reload (preysense.json)");
        }
        finally
        {
            UpdatePreferences.SetAutoUpdate(UpdatePreferences.DefaultAutoUpdate);
        }

        Assert.IsTrue(UpdatePreferences.PersistedAutoUpdate);
    }
}
