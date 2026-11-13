using PreySense.Mode;

namespace PreySense.Tests;

/// <summary>Pure-logic tests for the GHelper-compatible continuous fan curve.</summary>
[TestClass]
public sealed class ContinuousFanCurveTests
{
    private static GHelperFanCurve Curve(int[] temps, int[] duties)
    {
        var curve = GHelperFanCurve.Default(1);
        curve.Temps = temps;
        curve.Duties = duties;
        curve.GpuTemps = (int[])temps.Clone();
        curve.GpuDuties = (int[])duties.Clone();
        curve.Normalize();
        return curve;
    }

    private static ContinuousFanCurve Continuous(GHelperFanCurve curve) => new()
    {
        CpuCurve = curve.Copy(),
        GpuCurve = curve.Copy(),
        RampUpPercentPerSecond = 8,
        RampDownPercentPerSecond = 4,
        HysteresisCelsius = 0
    };

    [TestMethod]
    public void InterpolatesLinearlyBetweenGHelperPoints()
    {
        // Default Balanced table: 58,61,64,68,72,77,81,98 °C / 8,17,22,26,34,41,48,69 %.
        var curve = GHelperFanCurve.Default(1);
        Assert.AreEqual(8, curve.Interpolate(58));
        Assert.AreEqual(17, curve.Interpolate(61));
        Assert.AreEqual(14, curve.Interpolate(60));
        Assert.AreEqual(69, curve.Interpolate(98));
    }

    [TestMethod]
    public void ClampsOutsideTheCurveRange()
    {
        var curve = GHelperFanCurve.Default(1);
        Assert.AreEqual(8, curve.Interpolate(0));
        Assert.AreEqual(69, curve.Interpolate(125));
    }

    [TestMethod]
    public void FixFanCurve_MakesTemperaturesStrictlyIncreasingBySix()
    {
        // Port of GHelper FixFanCurve: curve[i] <= previous -> previous + 6.
        int[] fixedTemps = GHelperFanCurve.FixTemperatures([60, 60, 61, 62, 63, 64, 65, 66]);
        for (int i = 1; i < fixedTemps.Length; i++)
            Assert.IsGreaterThanOrEqualTo(6, fixedTemps[i] - fixedTemps[i - 1]);
    }

    [TestMethod]
    public void FixFanCurve_PrependsThirtyDegreeAnchorWhenCurveStartsHot()
    {
        // Balanced default starts at 58 °C -> GHelper prepends the (30 °C, 0 %) anchor.
        int[] fixedTemps = GHelperFanCurve.FixTemperatures([58, 61, 64, 68, 72, 77, 81, 98]);
        Assert.AreEqual(30, fixedTemps[0]);
        Assert.AreEqual(58, fixedTemps[1]);
        Assert.AreEqual(81, fixedTemps[^1]); // the last (98) point is pushed out by the prepended anchor
    }

    [TestMethod]
    public void FixFanCurve_ClampsDutiesToZeroHundred()
    {
        int[] duties = GHelperFanCurve.FixDuties([-10, 0, 25, 50, 75, 120, 150, 200]);
        CollectionAssert.AreEqual(new[] { 0, 0, 25, 50, 75, 100, 100, 100 }, duties);
    }

    [TestMethod]
    public void DefaultCurvesMatchGHelperTables()
    {
        var balanced = GHelperFanCurve.Default(1);
        CollectionAssert.AreEqual(new[] { 58, 61, 64, 68, 72, 77, 81, 98 }, balanced.Temps);
        CollectionAssert.AreEqual(new[] { 8, 17, 22, 26, 34, 41, 48, 69 }, balanced.Duties);

        var turbo = GHelperFanCurve.Default(4);
        CollectionAssert.AreEqual(new[] { 30, 63, 68, 72, 76, 80, 84, 98 }, turbo.Temps);
        CollectionAssert.AreEqual(new[] { 17, 26, 34, 41, 52, 67, 81, 90 }, turbo.Duties);

        var silent = GHelperFanCurve.Default(0);
        CollectionAssert.AreEqual(new[] { 30, 49, 59, 66, 71, 80, 90, 100 }, silent.Temps);
        CollectionAssert.AreEqual(new[] { 0, 0, 3, 12, 20, 28, 34, 41 }, silent.Duties);
    }

    [TestMethod]
    public void BytesRoundTripToTheGHelperSixteenByteForm()
    {
        var curve = Curve([30, 63, 68, 72, 76, 80, 84, 98], [17, 26, 34, 41, 52, 67, 81, 90]);
        byte[] bytes = curve.ToBytes();
        Assert.HasCount(16, bytes);
        Assert.AreEqual(30, bytes[0]);
        Assert.AreEqual(17, bytes[8]);
        var restored = new GHelperFanCurve
        {
            Temps = bytes[..8].Select(b => (int)b).ToArray(),
            Duties = bytes[8..].Select(b => (int)b).ToArray()
        };
        restored.Normalize();
        CollectionAssert.AreEqual(curve.Temps, restored.Temps);
        CollectionAssert.AreEqual(curve.Duties, restored.Duties);
    }

    [TestMethod]
    public void RampsUpFasterThanItFalls()
    {
        var curve = Continuous(Curve([30, 40, 46, 52, 60, 70, 80, 90], [0, 10, 25, 40, 60, 75, 90, 100]));

        var first = curve.Evaluate(100, 0, 1000);
        Assert.AreEqual(100, first.CpuDuty);

        var falling = curve.Evaluate(40, 0, 2000);
        Assert.AreEqual(96, falling.CpuDuty); // 4 %/s fall limit from 100

        var rising = Continuous(Curve([30, 40, 46, 52, 60, 70, 80, 90], [0, 10, 25, 40, 60, 75, 90, 100]));
        Assert.AreEqual(10, rising.Evaluate(40, 0, 1000).CpuDuty);
        Assert.AreEqual(18, rising.Evaluate(100, 0, 2000).CpuDuty); // 8 %/s rise limit
    }

    [TestMethod]
    public void HysteresisIgnoresSmallTemperatureWiggles()
    {
        var curve = Continuous(Curve([30, 40, 46, 52, 60, 70, 80, 90], [0, 10, 25, 40, 60, 75, 90, 100]));
        curve.HysteresisCelsius = 2;
        curve.RampUpPercentPerSecond = 50;
        curve.RampDownPercentPerSecond = 50;

        Assert.AreEqual(40, curve.Evaluate(52, 0, 1000).CpuDuty);
        Assert.AreEqual(40, curve.Evaluate(53, 0, 2000).CpuDuty); // inside the 2 °C band
        Assert.IsGreaterThan(40, curve.Evaluate(55, 0, 3000).CpuDuty);
    }

    [TestMethod]
    public void SlewRateIsClampedToTheConfiguredMaximum()
    {
        var curve = Continuous(Curve([30, 40, 46, 52, 60, 70, 80, 90], [0, 10, 25, 40, 60, 75, 90, 100]));
        curve.Evaluate(40, 0, 1000);
        Assert.IsLessThanOrEqualTo(14, curve.Evaluate(100, 0, 1400).CpuDuty);
    }
}
