using System;
using PreySense.Core.Hardware;

namespace PreySense.Tests;

/// <summary>
/// Frame-level tests for the Predator Sense pipe client. These never open a pipe: they pin the
/// exact 11-byte request layout (a malformed frame makes the OEM service exit) and the cmd-15
/// reply decode, using the byte sequences captured in reverse/pipe/PAYLOAD-NOTES.md.
/// </summary>
[TestClass]
public sealed class AcerPipeSensorSourceTests
{
    [TestMethod]
    [DataRow(AcerPipeSensorSource.SensorCpuTemperature, new byte[] { 0x0F, 0x00, 0x01, 0x04, 0x00, 0x00, 0x00, 0x01, 0x01, 0x00, 0x00 })]
    [DataRow(AcerPipeSensorSource.SensorCpuFanRpm, new byte[] { 0x0F, 0x00, 0x01, 0x04, 0x00, 0x00, 0x00, 0x01, 0x02, 0x00, 0x00 })]
    [DataRow(AcerPipeSensorSource.SensorGpuFanRpm, new byte[] { 0x0F, 0x00, 0x01, 0x04, 0x00, 0x00, 0x00, 0x01, 0x06, 0x00, 0x00 })]
    [DataRow(AcerPipeSensorSource.SensorGpuTemperature, new byte[] { 0x0F, 0x00, 0x01, 0x04, 0x00, 0x00, 0x00, 0x01, 0x0A, 0x00, 0x00 })]
    public void BuildSensorRequest_MatchesCapturedOemFrames(int sensorId, byte[] expected)
    {
        byte[] actual = AcerPipeSensorSource.BuildSensorRequest(sensorId);

        Assert.HasCount(11, actual);
        CollectionAssert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void BuildSensorRequest_RejectsOutOfRangeIds_SoMalformedFramesNeverReachTheService()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AcerPipeSensorSource.BuildSensorRequest(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => AcerPipeSensorSource.BuildSensorRequest(256));
    }

    [TestMethod]
    [DataRow(new byte[] { 0x00, 0x4F, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }, 0, 79)]    // id 1  -> 79 °C
    [DataRow(new byte[] { 0x00, 0x3A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }, 0, 58)]    // id 10 -> 58 °C
    [DataRow(new byte[] { 0x00, 0x1E, 0x0E, 0x00, 0x00, 0x00, 0x00, 0x00 }, 0, 3614)]  // id 2  -> 3614 RPM
    [DataRow(new byte[] { 0x00, 0xC9, 0x0D, 0x00, 0x00, 0x00, 0x00, 0x00 }, 0, 3529)]  // id 6  -> 3529 RPM
    public void TryDecodeSensorReply_DecodesCapturedPayloads(byte[] payload, int expectedStatus, int expectedValue)
    {
        Assert.IsTrue(AcerPipeSensorSource.TryDecodeSensorReply(payload, out int status, out int value));
        Assert.AreEqual(expectedStatus, status);
        Assert.AreEqual(expectedValue, value);
    }

    [TestMethod]
    public void TryDecodeSensorReply_ReportsFailureStatus()
    {
        // status 0x02 = unsupported sensor (what root\WMI returns on this chassis for ids 6/10).
        byte[] payload = { 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

        Assert.IsTrue(AcerPipeSensorSource.TryDecodeSensorReply(payload, out int status, out int value));
        Assert.AreEqual(2, status);
        Assert.AreEqual(0, value);
    }

    [TestMethod]
    public void TryDecodeSensorReply_RejectsShortPayloads()
    {
        Assert.IsFalse(AcerPipeSensorSource.TryDecodeSensorReply(new byte[] { 0x00, 0x4F, 0x00 }, out _, out _));
    }

    [TestMethod]
    public void PipeConstants_MatchTheReversedOemService()
    {
        // Compile-time constants: keep the OEM protocol values pinned in one place. The MSTest
        // analyzers flag constant comparisons as "always true/false" — that is exactly the intent.
#pragma warning disable MSTEST0025, MSTEST0032
        Assert.AreEqual("predatorsense_service_namedpipe", AcerPipeSensorSource.PipeName);
        Assert.AreEqual(@"\\.\pipe\predatorsense_service_namedpipe", AcerPipeSensorSource.PipePath);
        Assert.AreEqual((short)15, AcerPipeSensorSource.CmdGetGamingSystemInformation);
        Assert.AreEqual((short)32, AcerPipeSensorSource.CmdSetGamingMiscSetting);
        Assert.AreEqual((short)33, AcerPipeSensorSource.CmdGetGamingMiscSetting);
        Assert.AreEqual(0x0Bu, AcerPipeSensorSource.OperatingModeSelector);
#pragma warning restore MSTEST0025, MSTEST0032
    }

    [TestMethod]
    public void BuildCommandFrame_ModeSet_IsStrictSeventeenByteFrame()
    {
        // cmd 32 + numArg 1 + len 8 + ulong payload (0x0B | mode<<8) for Performance (0x04).
        byte[] argument = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(argument, 0x0BUL | (0x04UL << 8));

        byte[] frame = AcerPipeSensorSource.BuildCommandFrame(AcerPipeSensorSource.CmdSetGamingMiscSetting, argument);

        CollectionAssert.AreEqual(new byte[]
        {
            0x20, 0x00,              // cmd 32
            0x01,                    // numArg
            0x08, 0x00, 0x00, 0x00,  // argLen 8
            0x0B, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        }, frame);
    }

    [TestMethod]
    public void BuildCommandFrame_ModeGet_IsStrictThirteenByteFrame()
    {
        // cmd 33 + numArg 1 + len 4 + uint32 0x0B.
        byte[] frame = AcerPipeSensorSource.BuildCommandFrame(
            AcerPipeSensorSource.CmdGetGamingMiscSetting,
            AcerPipeSensorSource.BuildUIntArgument(AcerPipeSensorSource.OperatingModeSelector));

        CollectionAssert.AreEqual(new byte[]
        {
            0x21, 0x00,              // cmd 33
            0x01,                    // numArg
            0x04, 0x00, 0x00, 0x00,  // argLen 4
            0x0B, 0x00, 0x00, 0x00   // selector 0x0B
        }, frame);
    }
}
