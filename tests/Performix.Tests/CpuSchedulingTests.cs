using PreySense.Mode;

namespace PreySense.Tests;

/// <summary>Pure-logic tests for the powercfg SUB_PROCESSOR mapping (no powercfg is executed).</summary>
[TestClass]
public sealed class CpuSchedulingTests
{
    [TestMethod]
    public void ApplyArgumentsUseTheProbedGuidsAndSetactive()
    {
        var values = CpuScheduling.CpuScheduleValues.Default(95, 2, 30, 100, 100);
        var ac = CpuScheduling.BuildApplyArguments(values, ac: true).ToArray();

        Assert.HasCount(6, ac);
        CollectionAssert.AreEqual(
            new[] { "/setacvalueindex", "SCHEME_CURRENT", "SUB_PROCESSOR", "bc5038f7-23e0-4960-96da-33abaf5935ec", "95" },
            ac[0]);
        Assert.AreEqual("be337238-0d82-4146-a960-4f3749d470c7", ac[1][3]);
        Assert.AreEqual("2", ac[1][4]);
        Assert.AreEqual("36687f9e-e3a5-4dbf-b1dc-15eb381c6863", ac[2][3]);
        Assert.AreEqual("0cc5b647-c1df-4637-891a-dec35c318583", ac[3][3]);
        Assert.AreEqual("ea062031-0e34-4ff1-9b6d-eb1059334028", ac[4][3]);
        CollectionAssert.AreEqual(new[] { "/setactive", "SCHEME_CURRENT" }, ac[5]);
    }

    [TestMethod]
    public void DcArgumentsUseSetdcvalueindex()
    {
        var values = CpuScheduling.CpuScheduleValues.Default(50, 0, 80);
        var dc = CpuScheduling.BuildApplyArguments(values, ac: false).ToArray();
        Assert.AreEqual("/setdcvalueindex", dc[0][0]);
        Assert.AreEqual("50", dc[0][4]);
    }

    [TestMethod]
    public void ValuesAreClampedToTheOemRanges()
    {
        var values = new CpuScheduling.CpuScheduleValues(150, 9, -5, 200, -10).Clamp();
        Assert.AreEqual(100, values.MaxProcessorState);
        Assert.AreEqual(6, values.BoostMode);
        Assert.AreEqual(0, values.Epp);
        Assert.AreEqual(100, values.CoreParkingMin);
        Assert.AreEqual(0, values.CoreParkingMax);
    }

    [TestMethod]
    public void ModeDefaultsAreConservativeForQuietAndOpenForTurbo()
    {
        var quiet = CpuScheduling.CpuScheduleConfig.DefaultForMode(0);
        var turbo = CpuScheduling.CpuScheduleConfig.DefaultForMode(5);
        Assert.IsLessThan(turbo.Ac.MaxProcessorState, quiet.Ac.MaxProcessorState);
        Assert.AreEqual(100, turbo.Ac.MaxProcessorState);
        Assert.AreEqual(0, quiet.Ac.BoostMode); // Quiet disables Turbo boost
        Assert.IsTrue(quiet.Ac.Epp >= 50);
        Assert.AreEqual(0, turbo.Ac.Epp);
    }

    [TestMethod]
    public void SettingGuidsMatchTheProbeDocument()
    {
        Assert.AreEqual("bc5038f7-23e0-4960-96da-33abaf5935ec", CpuScheduling.MaxProcessorState.Guid.ToString());
        Assert.AreEqual("be337238-0d82-4146-a960-4f3749d470c7", CpuScheduling.BoostMode.Guid.ToString());
        Assert.AreEqual("36687f9e-e3a5-4dbf-b1dc-15eb381c6863", CpuScheduling.Epp.Guid.ToString());
        Assert.AreEqual("54533251-82be-4824-96c1-47b60b740d00", CpuScheduling.SubProcessorGuid);
    }
}
