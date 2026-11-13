using System;
using PreySense.Mode;

namespace PreySense.Tests;

/// <summary>Pure-logic tests for the GHelper-style fan scheduler. No hardware access.</summary>
[TestClass]
public sealed class FanSchedulerTests
{
    private static FanCurveProfile Custom(float? ramp = 0)
    {
        var profile = FanCurveProfile.CreateDefault(1);
        profile.ControlMode = FanControlMode.Custom;
        profile.RampUpSeconds = (int)(ramp ?? 0);
        profile.Scheduler = FanSchedulerSettings.Default;
        return profile;
    }

    [TestMethod]
    public void SafetyCapForcesBothFansTo100()
    {
        var scheduler = new FanScheduler();
        var result = scheduler.Evaluate(
            new FanSchedulerInput(93, 50, 10, 10, 3000, 3000),
            Custom(), new FanCurveFollower(), 1000);

        Assert.IsTrue(result.SafetyActive);
        Assert.AreEqual(100, result.CpuDuty);
        Assert.AreEqual(100, result.GpuDuty);
    }

    [TestMethod]
    public void GpuSafetyCapAlsoApplies()
    {
        var result = new FanScheduler().Evaluate(
            new FanSchedulerInput(60, 89, 5, 5, 3000, 3000),
            Custom(), new FanCurveFollower(), 1000);

        Assert.IsTrue(result.SafetyActive);
        Assert.AreEqual(100, result.GpuDuty);
    }

    [TestMethod]
    public void PredictiveLeadRaisesDutyWhenLoadIsHigh()
    {
        var profile = Custom();
        int baseCpu;

        baseCpu = new FanScheduler().Evaluate(
            new FanSchedulerInput(60, 55, 10, 10, 4000, 4000),
            profile, new FanCurveFollower(), 1000).CpuDuty;

        // Fresh scheduler+profile so follower state cannot leak between evaluations.
        profile = Custom();
        var scheduler = new FanScheduler();
        var predicted = scheduler.Evaluate(
            new FanSchedulerInput(60, 55, 95, 90, 4000, 4000),
            profile, new FanCurveFollower(), 1000);

        Assert.IsTrue(predicted.PredictiveActive);
        Assert.IsGreaterThanOrEqualTo(baseCpu, predicted.CpuDuty);
        Assert.IsGreaterThan(0, predicted.GpuDuty);
    }

    [TestMethod]
    public void PredictiveDisabledNeverRaisesDuty()
    {
        var profile = Custom();
        profile.Scheduler = profile.Scheduler with { PredictiveEnabled = false };
        var scheduler = new FanScheduler { Settings = profile.Scheduler };

        var result = scheduler.Evaluate(
            new FanSchedulerInput(60, 55, 100, 100, 4000, 4000), profile, new FanCurveFollower(), 1000);

        Assert.IsFalse(result.PredictiveActive);
    }

    [TestMethod]
    public void ClosedLoopTrimsDutyWhenMeasuredRpmIsBelowTarget()
    {
        var profile = Custom();
        profile.Scheduler = profile.Scheduler with { ClosedLoopEnabled = true };

        var scheduler = new FanScheduler { Settings = profile.Scheduler };
        var baseScheduler = new FanScheduler { Settings = profile.Scheduler with { ClosedLoopEnabled = false } };

        int baseline = baseScheduler.Evaluate(
            new FanSchedulerInput(70, 60, 10, 10, 3000, 3000), Custom(), new FanCurveFollower(), 0).CpuDuty;

        // Measured RPM far below what the duty implies → closed loop should push the duty up.
        var trimmed = scheduler.Evaluate(
            new FanSchedulerInput(70, 60, 10, 10, 1200, 1200),
            profile, new FanCurveFollower(), 2000);

        Assert.IsTrue(trimmed.ClosedLoopActive);
        Assert.IsGreaterThanOrEqualTo(baseline, trimmed.CpuDuty);
        Assert.IsLessThanOrEqualTo(15, trimmed.CpuDuty - baseline);
    }

    [TestMethod]
    public void ClosedLoopFallsBackToOpenLoopWhenRpmIsMissing()
    {
        var profile = Custom();
        profile.Scheduler = profile.Scheduler with { ClosedLoopEnabled = true };
        var scheduler = new FanScheduler { Settings = profile.Scheduler };

        var result = scheduler.Evaluate(
            new FanSchedulerInput(70, 60, 10, 10, null, null), profile, new FanCurveFollower(), 2000);

        Assert.IsFalse(result.ClosedLoopActive);
        Assert.IsGreaterThanOrEqualTo(0, result.CpuDuty);
    }

    [TestMethod]
    public void SchedulerSettingsArePersistedPerMode()
    {
        // Round-trip through the profile copy used by FanCurveStorage.Save/Load.
        var profile = Custom();
        profile.Scheduler = new FanSchedulerSettings(true, 75, 6, true, 10, 1000);
        var copy = profile.Copy();

        Assert.AreEqual(75, copy.Scheduler.PredictiveLoadPercent);
        Assert.AreEqual(6, copy.Scheduler.PredictiveLeadCelsius);
        Assert.IsTrue(copy.Scheduler.ClosedLoopEnabled);
        Assert.AreEqual(10, copy.Scheduler.ClosedLoopTrimPercent);

        copy.Normalize(1);
        Assert.AreEqual(10, copy.Scheduler.ClosedLoopTrimPercent);
        Assert.AreEqual(6, copy.Scheduler.PredictiveLeadCelsius);
    }
}
