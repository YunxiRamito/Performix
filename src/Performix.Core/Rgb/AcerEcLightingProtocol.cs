using System;
using System.Buffers.Binary;

namespace PreySense.Rgb;

/// <summary>Pure packet builders recovered from Acer EC lighting controllers. No hardware access.</summary>
public static class AcerEcLightingProtocol
{
    public const ulong LegacyLightbarLedCommand = 0xFFF5000000000010UL;
    public const uint LightbarCapabilityQuery = 0x10;

    public static byte[] CreateKeyboardMode(byte mode, byte speed, byte brightness, byte direction, byte red, byte green, byte blue)
        => CreateMode(mode, speed, brightness, direction, red, green, blue, 1);

    public static byte[] CreateLightbarMode(byte mode, byte speed, byte brightness, byte direction, byte red, byte green, byte blue)
        => CreateMode(mode, speed, brightness, direction, red, green, blue, 2);

    public static byte[] CreateKeyboardBrightness(byte brightness) => CreateKeyboardMode(0, 0, brightness, 0, 0, 0, 0);
    public static byte[] CreateLightbarBrightness(byte brightness) => CreateLightbarMode(0, 0, brightness, 0, 0, 0, 0);

    private static byte[] CreateMode(byte mode, byte speed, byte brightness, byte direction, byte red, byte green, byte blue, byte target)
    {
        // AcerECKeyboardController 0x63c0; AcerECLightbarController 0x61e0.
        return [mode, speed, brightness, 0, direction, red, green, blue, 3, target, 0, 0, 0, 0, 0, 0];
    }

    public static ulong CreateKeyboardZoneColor(int zeroBasedZone, byte red, byte green, byte blue)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(zeroBasedZone);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(zeroBasedZone, 3);
        return (1UL << zeroBasedZone) | ((ulong)red << 8) | ((ulong)green << 16) | ((ulong)blue << 24);
    }

    /// <summary>
    /// PH317-55 firmware variant: <c>SetGamingRgbKb</c> takes a UInt32 and only accepts ONE zone bit
    /// per call. Payload: <c>zoneBit | R&lt;&lt;8 | G&lt;&lt;16 | B&lt;&lt;24</c>.
    /// </summary>
    public static uint CreateKeyboardZoneColor32(int zeroBasedZone, byte red, byte green, byte blue)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(zeroBasedZone);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(zeroBasedZone, 3);
        return (1u << zeroBasedZone) | ((uint)red << 8) | ((uint)green << 16) | ((uint)blue << 24);
    }

    /// <summary>Decodes a <c>GetGamingRgbKb</c> reply: bits 8..15 R, 16..23 G, 24..31 B.</summary>
    public static bool TryDecodeKeyboardZoneColor(ulong raw, out byte red, out byte green, out byte blue)
    {
        red = (byte)((raw >> 8) & 0xFF);
        green = (byte)((raw >> 16) & 0xFF);
        blue = (byte)((raw >> 24) & 0xFF);
        return (raw & 0xFF) == 0;
    }

    /// <summary>
    /// PH317-55 firmware variant zone enable: <c>SetGamingLEDBehavior(8 | zoneMask &lt;&lt; 40)</c>
    /// where bit 0 of <paramref name="zoneMask"/> = zone 1. Mode 8 = static.
    /// </summary>
    public static ulong CreateKeyboardZoneEnable(int zoneMask) =>
        8UL | ((ulong)(zoneMask & 0xF) << 40);

    /// <summary>Decodes <c>GetGamingLED(8)</c>: returns the 4-bit zone enable mask (bits 40..43).</summary>
    public static int DecodeKeyboardZoneEnable(ulong raw) => (int)((raw >> 40) & 0xF);

    /// <summary>
    /// PH317-55 firmware brightness packet: 10 bytes <c>{0,0,(level-1)*25,0,...,0,1}</c>.
    /// </summary>
    public static byte[] CreateKeyboardBacklightPacket10(int level)
    {
        int clamped = Math.Clamp(level, 1, 5);
        return [0, 0, (byte)((clamped - 1) * 25), 0, 0, 0, 0, 0, 0, 1];
    }

    /// <summary>Maps a 0..100 % brightness to the firmware's 1..5 static levels.</summary>
    public static int BrightnessLevelFromPercent(int percent) =>
        Math.Clamp(percent / 25 + 1, 1, 5);

    /// <summary>
    /// PH317-55 (SMBIOS 2.83) keyboard pattern word for <c>SetGamingKBBacklight</c>.
    /// <paramref name="pattern"/>: <b>0 = static</b>, 1..6 = dynamic effects (1 = breathing,
    /// 3 = neon with direction, ...). Bit layout: low byte = pattern, speed&lt;&lt;8,
    /// (level-1)*25&lt;&lt;16, direction&lt;&lt;32, colour&lt;&lt;40.
    /// </summary>
    public static ulong CreateKeyboardPatternWord(int pattern, int speed, int brightnessPercent, int direction,
        byte red, byte green, byte blue)
    {
        int speedField = Math.Clamp(speed, 0, 100);
        int brightnessField = (BrightnessLevelFromPercent(brightnessPercent) - 1) * 25;
        int directionField = Math.Clamp(direction, 0, 1);

        ulong colour = ((ulong)red << 40) | ((ulong)green << 48) | ((ulong)blue << 56);
        int n = Math.Clamp(pattern, 0, 6);
        int low = n;
        ulong extra = 0;
        bool useColour = n is 1 or 4 or 5 or 6;
        if (n == 3)
            extra = 0x8000000UL | ((ulong)directionField << 32);
        else if (n == 4)
            extra = (ulong)directionField << 32;
        return (ulong)(uint)low
               | ((ulong)speedField << 8)
               | ((ulong)brightnessField << 16)
               | extra
               | (useColour ? colour : 0UL);
    }

    /// <summary>Pattern 7 uses this explicit 9-byte packet (padded to 16 by the WMI array form).</summary>
    public static byte[] CreateKeyboardPatternPacket9(int speed, int brightnessPercent, byte red, byte green, byte blue) =>
    [
        7,
        (byte)Math.Clamp(speed, 0, 100),
        (byte)((BrightnessLevelFromPercent(brightnessPercent) - 1) * 25),
        0, 0, red, green, blue, 3
    ];

    /// <summary>
    /// WMI <c>SetGamingKBBacklight</c> takes a UInt8Array; PreySense's other EC generations send
    /// 16 bytes. The pattern word occupies the first 8 bytes (little endian).
    /// </summary>
    public static byte[] CreateKeyboardPatternPacket16(ulong patternWord)
    {
        byte[] packet = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(packet, patternWord);
        return packet;
    }

    /// <summary>Pads the 9-byte pattern packet to the 16-byte WMI array form.</summary>
    public static byte[] PadPatternPacket16(byte[] packet)
    {
        byte[] padded = new byte[16];
        packet.AsSpan(0, Math.Min(packet.Length, 16)).CopyTo(padded);
        return padded;
    }


    /// <param name="sideSelector">Front=1, Left=2, Right=4, Rear=8.</param>
    public static ulong CreateLightbarColor(int sideSelector, int zeroBasedLed, byte red, byte green, byte blue)
    {
        SideIndex(sideSelector);
        ArgumentOutOfRangeException.ThrowIfNegative(zeroBasedLed);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(zeroBasedLed, 7);
        return ((ulong)sideSelector << 32) | (1UL << (40 + zeroBasedLed)) |
               ((ulong)red << 8) | ((ulong)green << 16) | ((ulong)blue << 24);
    }

    public static int GetLedCommandLength(int smbiosMajor, int smbiosMinor)
    {
        ValidateVersion(smbiosMajor, smbiosMinor);
        return Is291OrLater(smbiosMajor, smbiosMinor) ? 16 : smbiosMajor == 2 && smbiosMinor >= 86 ? 12 : 8;
    }

    /// <param name="rgbColors">Packed RGB values, 0x00BBGGRR; zero means off.</param>
    public static byte[] CreateKeyboardLedMask(int smbiosMajor, int smbiosMinor, ReadOnlySpan<uint> rgbColors)
    {
        if (rgbColors.Length > 4) throw new ArgumentOutOfRangeException(nameof(rgbColors));
        var packet = new byte[GetLedCommandLength(smbiosMajor, smbiosMinor)];
        packet[0] = 8;
        for (int i = 0; i < rgbColors.Length; i++)
            if (rgbColors[i] != 0) packet[5] |= (byte)(1 << i);
        return packet;
    }

    public static byte[] CreateLightbarLedMask(int smbiosMajor, int smbiosMinor, int sideSelector, ReadOnlySpan<uint> rgbColors)
    {
        int side = SideIndex(sideSelector);
        int length = GetLedCommandLength(smbiosMajor, smbiosMinor);
        int maxLeds = length == 16 ? 8 : 4;
        if (rgbColors.Length > maxLeds) throw new ArgumentOutOfRangeException(nameof(rgbColors));
        var packet = new byte[length];
        if (length == 8)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(packet, LegacyLightbarLedCommand);
            return packet;
        }
        packet[0] = 0x10;
        packet.AsSpan(6, length == 16 ? 8 : 4).Fill(0xFF);
        ushort mask = 0;
        for (int i = 0; i < rgbColors.Length; i++)
            if (rgbColors[i] != 0) mask |= (ushort)(1 << (2 * i));
        if (length == 16) BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6 + side * 2), mask);
        else packet[6 + side] = (byte)mask;
        return packet;
    }

    /// <summary>Counts present LEDs from GetGamingLED(gmInput=0x10). gmOutput is the raw WMI byte array, excluding gmReturn.</summary>
    public static int DecodeLightbarLedCount(int smbiosMajor, int smbiosMinor, int sideSelector, byte gmReturn, ReadOnlySpan<byte> gmOutput)
    {
        ValidateVersion(smbiosMajor, smbiosMinor);
        int side = SideIndex(sideSelector);
        if (gmReturn != 0) return 0;
        bool extended = Is291OrLater(smbiosMajor, smbiosMinor);
        int offset = 5 + side * (extended ? 2 : 1);
        int width = extended ? 2 : 1;
        if (gmOutput.Length < offset + width) throw new ArgumentException("Truncated GetGamingLED output.", nameof(gmOutput));
        int mask = extended ? BinaryPrimitives.ReadUInt16LittleEndian(gmOutput[offset..]) : gmOutput[offset];
        int count = 0;
        for (int i = 0; i < (extended ? 8 : 4); i++)
            if (((mask >> (2 * i)) & 3) != 3) count++;
        return count;
    }

    private static bool Is291OrLater(int major, int minor) => major > 2 || major == 2 && minor >= 91;
    private static void ValidateVersion(int major, int minor)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(major);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);
    }
    private static int SideIndex(int selector) => selector switch
    {
        1 => 0, 2 => 1, 4 => 2, 8 => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(selector), "Use Front=1, Left=2, Right=4, or Rear=8.")
    };
}
