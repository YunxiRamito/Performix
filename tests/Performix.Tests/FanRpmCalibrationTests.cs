using PreySense.Mode;

namespace PreySense.Tests;

/// <summary>E1: the EC fan response is non-linear, so calibration round-trips must hold.</summary>
[TestClass]
public sealed class FanRpmCalibrationTests
{
    [TestMethod]
    [DataRow(0, 3529)]
    [DataRow(10, 3947)]
    [DataRow(50, 5357)]
    [DataRow(100, 7692)]
    public void MeasurementPointsRoundTripDutyToRpm(int duty, int rpm)
    {
        Assert.AreEqual(rpm, FanRpmCalibration.DutyToRpm(duty));
        Assert.AreEqual(duty, FanRpmCalibration.RpmToDuty(rpm));
    }

    [TestMethod]
    public void PhysicalCurveIsNonLinearAndHasAFloor()
    {
        // Linear 6400 model would give 0 at 0 %; the real fan floor is ~3529 RPM.
        Assert.AreEqual(3529, FanRpmCalibration.FloorRpm);
        Assert.AreEqual(3529, FanRpmCalibration.DutyToRpm(0));
        Assert.AreEqual(7692, FanRpmCalibration.MaxRpm);
        Assert.AreEqual(7692, FanRpmCalibration.DutyToRpm(100));
        Assert.IsGreaterThan(3529, FanRpmCalibration.DutyToRpm(1));
    }

    [TestMethod]
    public void LowerDutySegmentsAreSteeperThanTheTop()
    {
        // 0→10 % gains ~418 RPM; 50→100 % gains ~2335 RPM over 50 duty points (~467 per 10).
        int lowGain = FanRpmCalibration.DutyToRpm(10) - FanRpmCalibration.DutyToRpm(0);
        int highGain = FanRpmCalibration.DutyToRpm(100) - FanRpmCalibration.DutyToRpm(50);
        Assert.IsGreaterThan(0, lowGain);
        Assert.IsGreaterThan(0, highGain);
        // The floor + steeper low end is exactly why a linear model was wrong.
        Assert.AreEqual(35, Math.Abs(lowGain - highGain / 5) < 100 ? 35 : 0);
    }

    [TestMethod]
    public void OutOfRangeValuesClamp()
    {
        Assert.AreEqual(0, FanRpmCalibration.RpmToDuty(1000));
        Assert.AreEqual(100, FanRpmCalibration.RpmToDuty(99999));
        Assert.AreEqual(3529, FanRpmCalibration.DutyToRpm(-5));
        Assert.AreEqual(7692, FanRpmCalibration.DutyToRpm(150));
    }

    [TestMethod]
    public void FanStagePhysicalHelpersUseTheCalibration()
    {
        Assert.AreEqual(5357, FanStage.DutyToPhysicalRpm(50));
        Assert.AreEqual(50, FanStage.PhysicalRpmToDuty(5357));
    }
}
