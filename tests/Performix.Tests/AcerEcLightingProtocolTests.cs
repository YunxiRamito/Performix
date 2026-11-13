using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PreySense.Rgb;

namespace PreySense.Tests;

[TestClass]
public class AcerEcLightingProtocolTests
{
    [TestMethod]
    public void ModeAndBrightnessMatchOemByteOrder()
    {
        CollectionAssert.AreEqual(new byte[] { 5, 2, 80, 0, 1, 0x12, 0x34, 0x56, 3, 1, 0, 0, 0, 0, 0, 0 },
            AcerEcLightingProtocol.CreateKeyboardMode(5, 2, 80, 1, 0x12, 0x34, 0x56));
        var bar = AcerEcLightingProtocol.CreateLightbarMode(5, 2, 80, 1, 0x12, 0x34, 0x56);
        Assert.AreEqual((byte)2, bar[9]);
        CollectionAssert.AreEqual(new byte[] { 0, 0, 100, 0, 0, 0, 0, 0, 3, 2, 0, 0, 0, 0, 0, 0 },
            AcerEcLightingProtocol.CreateLightbarBrightness(100));
    }

    [TestMethod]
    public void ColorWordsKeepSideAndZoneBitsSeparate()
    {
        Assert.AreEqual(0x0000000056341208UL, AcerEcLightingProtocol.CreateKeyboardZoneColor(3, 0x12, 0x34, 0x56));
        Assert.AreEqual(0x0000800456341200UL, AcerEcLightingProtocol.CreateLightbarColor(4, 7, 0x12, 0x34, 0x56));
    }

    [TestMethod]
    public void LedCommandsRespectSmbiosVersionBoundaries()
    {
        Assert.AreEqual(8, AcerEcLightingProtocol.GetLedCommandLength(2, 85));
        Assert.AreEqual(12, AcerEcLightingProtocol.GetLedCommandLength(2, 86));
        Assert.AreEqual(12, AcerEcLightingProtocol.GetLedCommandLength(2, 90));
        Assert.AreEqual(16, AcerEcLightingProtocol.GetLedCommandLength(2, 91));
        Assert.AreEqual(16, AcerEcLightingProtocol.GetLedCommandLength(3, 0));
        CollectionAssert.AreEqual(new byte[] { 8, 0, 0, 0, 0, 5, 0, 0, 0, 0, 0, 0 },
            AcerEcLightingProtocol.CreateKeyboardLedMask(2, 86, [1u, 0u, 1u, 0u]));
        CollectionAssert.AreEqual(new byte[] { 0x10, 0, 0, 0, 0, 0, 0xFF, 0x11, 0xFF, 0xFF, 0, 0 },
            AcerEcLightingProtocol.CreateLightbarLedMask(2, 90, 2, [1u, 0u, 1u, 0u]));
        CollectionAssert.AreEqual(new byte[] { 0x10, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 1, 0x40, 0, 0 },
            AcerEcLightingProtocol.CreateLightbarLedMask(2, 91, 8, [1u, 0u, 0u, 0u, 0u, 0u, 0u, 1u]));
        CollectionAssert.AreEqual(new byte[] { 0x10, 0, 0, 0, 0, 0, 0xF5, 0xFF },
            AcerEcLightingProtocol.CreateLightbarLedMask(2, 85, 1, [1u]));
    }

    [TestMethod]
    public void CapabilityDecodeUsesRawOutputWithoutPrependedReturnByte()
    {
        var old = new byte[10];
        old[5] = 0xF0; // Front two present, two absent.
        Assert.AreEqual(2, AcerEcLightingProtocol.DecodeLightbarLedCount(2, 90, 1, 0, old));
        var current = new byte[15];
        Array.Fill(current, (byte)0xFF);
        current[11] = 0xFC; // Rear first LED present.
        Assert.AreEqual(1, AcerEcLightingProtocol.DecodeLightbarLedCount(2, 91, 8, 0, current));
        Assert.AreEqual(0, AcerEcLightingProtocol.DecodeLightbarLedCount(2, 91, 8, 1, []));
        Assert.Throws<ArgumentException>(() => AcerEcLightingProtocol.DecodeLightbarLedCount(2, 91, 8, 0, []));
    }

    [TestMethod]
    public void Ph31755UInt32ZoneVariantMatchesMeasuredPayloads()
    {
        // Measured on PH317-55: SetGamingRgbKb accepts ONE zone bit per call, UInt32 payload
        // zoneBit | R<<8 | G<<16 | B<<24.  zone1 #00AEC7 -> 0xC7AE0001 (accepted).
        Assert.AreEqual(0xC7AE0001U, AcerEcLightingProtocol.CreateKeyboardZoneColor32(0, 0x00, 0xAE, 0xC7));
        Assert.AreEqual(0xC7AE0008U, AcerEcLightingProtocol.CreateKeyboardZoneColor32(3, 0x00, 0xAE, 0xC7));
        // Combined four-zone mask must never be produced by one call:
        Assert.Throws<ArgumentOutOfRangeException>(() => AcerEcLightingProtocol.CreateKeyboardZoneColor32(4, 0, 0, 0));

        // GetGamingRgbKb(1) -> 0x00000000C7AE0000 reads back #00AEC7 (low byte 0 = OK).
        Assert.IsTrue(AcerEcLightingProtocol.TryDecodeKeyboardZoneColor(0x00000000C7AE0000UL,
            out byte red, out byte green, out byte blue));
        Assert.AreEqual(0x00, red);
        Assert.AreEqual(0xAE, green);
        Assert.AreEqual(0xC7, blue);
        Assert.IsFalse(AcerEcLightingProtocol.TryDecodeKeyboardZoneColor(0x0000000000000002UL, out _, out _, out _));
    }

    [TestMethod]
    public void Ph31755ZoneEnableAndBacklightPacketsMatchOemFormula()
    {
        // LightingPage_Clubman.cs:870-874 -> 8 | zoneMask << 40 (all four zones measured 0xF<<40).
        Assert.AreEqual(0x00000F0000000008UL, AcerEcLightingProtocol.CreateKeyboardZoneEnable(0xF));
        Assert.AreEqual(0x0000000000000008UL, AcerEcLightingProtocol.CreateKeyboardZoneEnable(0));
        Assert.AreEqual(0xF, AcerEcLightingProtocol.DecodeKeyboardZoneEnable(0x00000F0000000008UL));

        // LightingPage_Clubman.cs:943-955 -> byte[10] { 0, 0, (level-1)*25, 0,...,0, 1 }.
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 },
            AcerEcLightingProtocol.CreateKeyboardBacklightPacket10(1));
        CollectionAssert.AreEqual(new byte[] { 0, 0, 25, 0, 0, 0, 0, 0, 0, 1 },
            AcerEcLightingProtocol.CreateKeyboardBacklightPacket10(2));
        CollectionAssert.AreEqual(new byte[] { 0, 0, 100, 0, 0, 0, 0, 0, 0, 1 },
            AcerEcLightingProtocol.CreateKeyboardBacklightPacket10(5));
        Assert.HasCount(10, AcerEcLightingProtocol.CreateKeyboardBacklightPacket10(3));
        Assert.AreEqual(1, AcerEcLightingProtocol.BrightnessLevelFromPercent(0));
        Assert.AreEqual(2, AcerEcLightingProtocol.BrightnessLevelFromPercent(25));
        Assert.AreEqual(5, AcerEcLightingProtocol.BrightnessLevelFromPercent(100));
    }

    [TestMethod]
    public void Ph31755PatternWordMatchesOemDynamicKeyboardTable()
    {
        // Static = pattern 0 (NOT 1). Measured: pipe cmd30 brightness 100 -> 0x640000 -> readback byte0 = 0.
        ulong staticLevel4 = AcerEcLightingProtocol.CreateKeyboardPatternWord(0, 0, 100, 0, 0, 0, 0);
        Assert.AreEqual(0x0000000000640000UL, staticLevel4);
        Assert.AreEqual(0, AcerEcLightingProtocol.CreateKeyboardPatternPacket16(staticLevel4)[0]);

        // Dynamic effect n=1 = breathing (low byte 1) and carries the colour.
        ulong breathing = AcerEcLightingProtocol.CreateKeyboardPatternWord(1, 25, 75, 0, 0xFF, 0x00, 0x00);
        Assert.AreEqual(1, (int)(breathing & 0xFF));
        Assert.AreEqual(0xFFUL, (breathing >> 40) & 0xFF);

        // n=3 = neon: bit 27 flag + direction in bits 32..33, no colour bits.
        ulong neon = AcerEcLightingProtocol.CreateKeyboardPatternWord(3, 25, 50, 1, 0xFF, 0xFF, 0xFF);
        Assert.AreEqual(3, (int)(neon & 0xFF));
        Assert.AreEqual(0x8000000UL, neon & 0x8000000UL);
        Assert.AreEqual(1UL, (neon >> 32) & 0xFF);
        Assert.AreEqual(0UL, (neon >> 40) & 0xFF);

        // n=7 uses the 9-byte packet {7, speed, level, 0,0, R,G,B,3}, padded to the 16-byte WMI array.
        byte[] nine = AcerEcLightingProtocol.CreateKeyboardPatternPacket9(25, 75, 0xFF, 0, 0);
        CollectionAssert.AreEqual(new byte[] { 7, 25, 75, 0, 0, 0xFF, 0, 0, 3 }, nine);
        Assert.HasCount(16, AcerEcLightingProtocol.PadPatternPacket16(nine));
    }
}
