using PreySense.Core.Hardware;
using PreySense.Helpers;

namespace PreySense.Tests;

/// <summary>
/// Regression guards for the CPU/GPU fan channels.
/// </summary>
/// <remarks>
/// The two fans are separate EC sensors (pipe cmd 15 ids 2 and 6, WMI sensor ids 0x02 and 0x06) and
/// their readings must stay separate end to end. The observed failure mode was two different fan
/// speeds (4054 and 4109 RPM) being reduced to one value on the WMI fallback/diagnostic path, so
/// both fans looked identical even though the EC reported them separately.
/// </remarks>
[TestClass]
public sealed class FanChannelSeparationTests
{
    [TestMethod]
    public void PipeFanSensorIds_AreDistinct_AndSelectDifferentRequestFrames()
    {
        // Compile-time constants are compared on purpose: these ids are the whole CPU/GPU split.
#pragma warning disable MSTEST0025, MSTEST0032
        Assert.AreEqual(2, AcerPipeSensorSource.SensorCpuFanRpm);
        Assert.AreEqual(6, AcerPipeSensorSource.SensorGpuFanRpm);
        Assert.AreNotEqual(AcerPipeSensorSource.SensorCpuFanRpm, AcerPipeSensorSource.SensorGpuFanRpm);
#pragma warning restore MSTEST0025, MSTEST0032

        // The sensor id is the only thing that selects what the service answers, so the CPU and GPU
        // fan requests must never collapse to the same frame (the exact bytes are pinned in
        // AcerPipeSensorSourceTests).
        CollectionAssert.AreNotEqual(
            AcerPipeSensorSource.BuildSensorRequest(AcerPipeSensorSource.SensorCpuFanRpm),
            AcerPipeSensorSource.BuildSensorRequest(AcerPipeSensorSource.SensorGpuFanRpm));
    }

    [TestMethod]
    public void WmiFanSensorIds_AreDistinct()
    {
#pragma warning disable MSTEST0025, MSTEST0032
        Assert.AreEqual(0x02UL, AcerWmi.Sensors.CpuFanRpm);
        Assert.AreEqual(0x06UL, AcerWmi.Sensors.GpuFanRpm);
        Assert.AreNotEqual(AcerWmi.Sensors.CpuFanRpm, AcerWmi.Sensors.GpuFanRpm);
#pragma warning restore MSTEST0025, MSTEST0032
    }

    [TestMethod]
    public void WmiFanReadback_KeepsTheEcsOwnRpmResolution()
    {
        // Captured EC replies for the two fans. The old "round to the nearest 100" mapped both of
        // these to 4100, which made the CPU and GPU fan indistinguishable on the WMI path.
        int cpuFanRpm = WmiController.FanRpmFromSensorReading(4054);
        int gpuFanRpm = WmiController.FanRpmFromSensorReading(4109);

        Assert.AreEqual(4054, cpuFanRpm);
        Assert.AreEqual(4109, gpuFanRpm);
        Assert.AreNotEqual(cpuFanRpm, gpuFanRpm);
    }

    [TestMethod]
    public void WmiFanReadback_KeepsTheNoReadingSentinelAndAcceptsZero()
    {
        Assert.AreEqual(-1, WmiController.FanRpmFromSensorReading(-1));
        Assert.AreEqual(0, WmiController.FanRpmFromSensorReading(0));
    }

    [TestMethod]
    public void FanRpmFallback_KeepsCpuAndGpuChannelsSeparate()
    {
        // pipe: null exercises the Acer WMI fallback branch that ProductionHardwareService wires to
        // WmiController.CpuFanRpm / GpuFanRpm.
        SensorReadout cpu = ThermalReadout.FanRpm(null, AcerPipeSensorSource.SensorCpuFanRpm, () => 4109);
        SensorReadout gpu = ThermalReadout.FanRpm(null, AcerPipeSensorSource.SensorGpuFanRpm, () => 4054);

        Assert.AreEqual(new SensorReadout(4109, SensorSource.AcerWmi), cpu);
        Assert.AreEqual(new SensorReadout(4054, SensorSource.AcerWmi), gpu);
        Assert.AreNotEqual(cpu.Value, gpu.Value);
    }

    [TestMethod]
    public void FanRpmFallback_RejectsImplausibleWmiValues()
    {
        Assert.AreEqual(SensorReadout.Empty,
            ThermalReadout.FanRpm(null, AcerPipeSensorSource.SensorCpuFanRpm, () => 12000));
        Assert.AreEqual(SensorReadout.Empty,
            ThermalReadout.FanRpm(null, AcerPipeSensorSource.SensorGpuFanRpm, () => 0));
    }
}
