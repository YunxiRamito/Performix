using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.Versioning;

namespace PreySense.Rgb;

/// <summary>Direct WMI EC lighting, gated by SMBIOS declarations and firmware capability replies.</summary>
[SupportedOSPlatform("windows")]
public sealed class AcerEcLightingDevice : IDisposable
{
    private static readonly object TransportLock = new();
    private readonly ManagementObject _instance;
    private readonly AcerEcLightingCapabilities _capabilities;
    private readonly bool _rgbKbUsesUInt32;
    private readonly bool _ledBehaviorVariant;
    private readonly bool _backlightPacket10;
    private bool _disposed;
    public string Id { get; }
    public string Name { get; }
    public bool IsKeyboard => SideSelector == 0;
    public int SideSelector { get; }
    public int LedCount { get; }
    public bool ModesAffectAllLightbars => !IsKeyboard;

    private AcerEcLightingDevice(string path, AcerEcLightingCapabilities capabilities, int sideSelector, int count,
        bool rgbKbUsesUInt32, bool ledBehaviorVariant, bool backlightPacket10)
    {
        _instance = new ManagementObject(path);
        _capabilities = capabilities;
        _rgbKbUsesUInt32 = rgbKbUsesUInt32;
        _ledBehaviorVariant = ledBehaviorVariant;
        _backlightPacket10 = backlightPacket10;
        SideSelector = sideSelector;
        LedCount = count;
        string target = sideSelector switch { 0 => "Keyboard", 1 => "Front", 2 => "Left", 4 => "Right", 8 => "Rear", _ => throw new ArgumentOutOfRangeException(nameof(sideSelector)) };
        Id = "acer-ec-" + target.ToLowerInvariant();
        Name = sideSelector == 0 ? $"Keyboard ({count} zones)" : target + " lightbar";
    }

    /// <summary>Only reads SMBIOS, WMI method metadata and GetGamingLED. No lighting writes.</summary>
    public static IReadOnlyList<AcerEcLightingDevice> Discover(out string diagnostic)
    {
        var devices = new List<AcerEcLightingDevice>();
        diagnostic = "No EC lighting declared by firmware.";
        try
        {
            AcerEcLightingCapabilities? capabilities = null;
            using (var tables = new ManagementObjectSearcher(@"root\wmi", "SELECT SMBiosData FROM MSSmBios_RawSMBiosTables"))
            using (var rows = tables.Get())
            {
                foreach (ManagementObject row in rows)
                {
                    using (row)
                        if (row["SMBiosData"] is byte[] bytes && AcerEcLightingCapabilities.FromSmbios(bytes) is { } found)
                        { capabilities = found; break; }
                }
            }
            if (capabilities is null || (!capabilities.Keyboard && !capabilities.Lightbars)) return devices;
            using var metadata = new ManagementClass(@"root\wmi:AcerGamingFunction");
            metadata.Get();

            // Firmware shape matrix — several EC generations expose the same lighting feature with
            // different WMI signatures (e.g. PH317-55 uses UInt32 colour + SetGamingLEDBehavior).
            // gmOutput types differ per generation (UInt32 or UInt64), so accept either.
            bool backlightArray = MatchesShape(metadata, "SetGamingKBBacklight", CimType.UInt8, true, CimType.UInt32);
            bool rgbKbUInt64 = MatchesShape(metadata, "SetGamingRgbKb", CimType.UInt64, false, CimType.UInt32, CimType.UInt64);
            bool rgbKbUInt32 = MatchesShape(metadata, "SetGamingRgbKb", CimType.UInt32, false, CimType.UInt32, CimType.UInt64);
            bool ledArray = MatchesShape(metadata, "SetGamingLED", CimType.UInt8, true, CimType.UInt32, CimType.UInt64);
            bool ledBehavior = MatchesShape(metadata, "SetGamingLEDBehavior", CimType.UInt64, false, CimType.UInt32, CimType.UInt64);
            bool getRgbKb = MatchesShape(metadata, "GetGamingRgbKb", CimType.UInt32, false, CimType.UInt64, CimType.UInt32);

            if (!backlightArray || (!rgbKbUInt64 && !rgbKbUInt32) || (!ledArray && !ledBehavior))
            {
                diagnostic = $"Acer EC lighting method schema is unsupported (backlightArray={backlightArray}, " +
                             $"rgbKb64={rgbKbUInt64}, rgbKb32={rgbKbUInt32}, ledArray={ledArray}, ledBehavior={ledBehavior}).";
                return devices;
            }

            bool u32Colour = !rgbKbUInt64 && rgbKbUInt32;
            bool useLedBehavior = !ledArray && ledBehavior;
            bool backlightPacket10 = backlightArray && u32Colour;
            diagnostic = $"EC lighting schema: colour={(u32Colour ? "UInt32" : "UInt64")}, " +
                         $"zone enable={(useLedBehavior ? "SetGamingLEDBehavior" : "SetGamingLED")}, " +
                         $"readback={(getRgbKb ? "GetGamingRgbKb" : "none")}.";

            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM AcerGamingFunction");
            using var instances = searcher.Get();
            using var instance = instances.Cast<ManagementObject>().FirstOrDefault();
            if (instance is null) { diagnostic = "Acer gaming WMI instance is unavailable."; return devices; }
            string path = instance.Path.Path;
            if (capabilities.Keyboard)
                devices.Add(new(path, capabilities, 0, capabilities.KeyboardZoneCount, u32Colour, useLedBehavior, backlightPacket10));
            if (capabilities.Lightbars && AcerEcLightingProtocol.GetLedCommandLength(capabilities.Major, capabilities.Minor) >= 12 &&
                Matches(metadata, "GetGamingLED", CimType.UInt32, false, CimType.UInt8, true, true))
            {
                using var input = instance.GetMethodParameters("GetGamingLED");
                input["gmInput"] = AcerEcLightingProtocol.LightbarCapabilityQuery;
                using var output = instance.InvokeMethod("GetGamingLED", input, null);
                if (output is not null && output["gmReturn"] is byte status && status == 0 && output["gmOutput"] is byte[] bytes)
                {
                    foreach (int side in new[] { 1, 2, 4, 8 })
                    {
                        int count = AcerEcLightingProtocol.DecodeLightbarLedCount(capabilities.Major, capabilities.Minor, side, status, bytes);
                        if (count > 0) devices.Add(new(path, capabilities, side, count, u32Colour, useLedBehavior, backlightPacket10));
                    }
                }
            }
            diagnostic = devices.Count == 0 ? "No supported EC lighting targets confirmed." : $"Confirmed {devices.Count} EC lighting target(s), Acer SMBIOS {capabilities.Major}.{capabilities.Minor}.";
        }
        catch (Exception ex)
        {
            // Previously confirmed keyboard can remain usable when an optional bar query fails.
            diagnostic = "EC lighting discovery: " + ex.Message;
        }
        return devices;
    }

    private static bool MatchesShape(ManagementClass metadata, string name, CimType inputType, bool inputArray, params CimType[] outputTypes)
    {
        var method = metadata.Methods.Cast<MethodData>().FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (method?.InParameters is null || method.OutParameters is null) return false;
        bool inputOk = method.InParameters.Properties.Cast<PropertyData>().Any(p =>
            p.Name.Equals("gmInput", StringComparison.OrdinalIgnoreCase) && p.Type == inputType && p.IsArray == inputArray);
        if (!inputOk) return false;
        return method.OutParameters.Properties.Cast<PropertyData>().Any(p =>
            p.Name.Equals("gmOutput", StringComparison.OrdinalIgnoreCase) && !p.IsArray && outputTypes.Contains(p.Type));
    }

    private static bool Matches(ManagementClass metadata, string name, CimType inputType, bool inputArray, CimType outputType, bool outputArray, bool hasStatus = false)
    {
        var method = metadata.Methods.Cast<MethodData>().FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (method?.InParameters is null || method.OutParameters is null) return false;
        bool Property(ManagementBaseObject parameters, string property, CimType type, bool array) =>
            parameters.Properties.Cast<PropertyData>().Any(p => p.Name.Equals(property, StringComparison.OrdinalIgnoreCase) && p.Type == type && p.IsArray == array);
        return Property(method.InParameters, "gmInput", inputType, inputArray) &&
               Property(method.OutParameters, "gmOutput", outputType, outputArray) &&
               (!hasStatus || Property(method.OutParameters, "gmReturn", CimType.UInt8, false));
    }

    /// <param name="colors">Exactly LedCount packed colors, 0x00BBGGRR.</param>
    public void ApplyColors(ReadOnlySpan<uint> colors, byte brightness)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (colors.Length != LedCount) throw new ArgumentException("Supply exactly one color per confirmed zone or LED.", nameof(colors));
        lock (TransportLock)
        {
            if (IsKeyboard && _ledBehaviorVariant)
            {
                // PH317-55 variant: zone enable goes through SetGamingLEDBehavior(8 | mask<<40).
                int zoneMask = 0;
                for (int i = 0; i < colors.Length; i++)
                    if ((colors[i] & 0x00FFFFFF) != 0) zoneMask |= 1 << i;
                InvokeUInt64("SetGamingLEDBehavior", AcerEcLightingProtocol.CreateKeyboardZoneEnable(zoneMask));
                // Pattern first: while the EC runs a dynamic pattern, per-zone colours are ignored.
                // Force static (pattern 0) with the first colour and the requested brightness.
                uint first = colors.Length > 0 ? colors[0] : 0u;
                ApplyPatternCore(0, speed: 25, brightness, direction: 0,
                    (byte)first, (byte)(first >> 8), (byte)(first >> 16));
            }
            else
            {
                var mask = IsKeyboard
                    ? AcerEcLightingProtocol.CreateKeyboardLedMask(_capabilities.Major, _capabilities.Minor, colors)
                    : AcerEcLightingProtocol.CreateLightbarLedMask(_capabilities.Major, _capabilities.Minor, SideSelector, colors);
                Invoke("SetGamingLED", mask);
                Invoke("SetGamingKBBacklight", IsKeyboard ? AcerEcLightingProtocol.CreateKeyboardBrightness(brightness) : AcerEcLightingProtocol.CreateLightbarBrightness(brightness));
            }

            for (int i = 0; i < colors.Length; i++)
            {
                uint color = colors[i];
                byte red = (byte)color, green = (byte)(color >> 8), blue = (byte)(color >> 16);
                if (IsKeyboard && _rgbKbUsesUInt32)
                {
                    // One zone per call: a combined 0xF mask is rejected with status 2 on PH317-55.
                    Invoke("SetGamingRgbKb", AcerEcLightingProtocol.CreateKeyboardZoneColor32(i, red, green, blue));
                    continue;
                }
                ulong value = IsKeyboard ? AcerEcLightingProtocol.CreateKeyboardZoneColor(i, red, green, blue)
                    : AcerEcLightingProtocol.CreateLightbarColor(SideSelector, i, red, green, blue);
                // WMI Invoke is synchronous; no artificial sleep — pacing held the shared HID/EC
                // lock and made software-effect frames stall on the fallback path.
                Invoke("SetGamingRgbKb", value);
            }
        }
    }

    public void ApplyMode(byte mode, byte speed, byte brightness, byte direction, byte red, byte green, byte blue)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (TransportLock)
        {
            if (IsKeyboard && _ledBehaviorVariant)
            {
                // Hardware patterns ARE supported on this EC; the entry point is SetGamingKBBacklight
                // (pattern word), not SetGamingLEDBehavior. UI index 0 = static, 1..7 = dynamic.
                InvokeUInt64("SetGamingLEDBehavior", AcerEcLightingProtocol.CreateKeyboardZoneEnable(0xF));
                ApplyPatternCore(mode, speed, brightness, direction, red, green, blue);
                for (int zone = 0; zone < LedCount; zone++)
                    Invoke("SetGamingRgbKb", AcerEcLightingProtocol.CreateKeyboardZoneColor32(zone, red, green, blue));
                return;
            }
            Invoke("SetGamingKBBacklight", IsKeyboard
                ? AcerEcLightingProtocol.CreateKeyboardMode(mode, speed, brightness, direction, red, green, blue)
                : AcerEcLightingProtocol.CreateLightbarMode(mode, speed, brightness, direction, red, green, blue));
        }
    }

    /// <summary>
    /// Sets a keyboard pattern (0 = static, 1..6 = the OEM dynamic patterns) with its speed,
    /// brightness, direction and colour. This is the register that actually decides whether the
    /// per-zone colours are displayed.
    /// </summary>
    public void ApplyPattern(int patternSelect, int speed, byte brightnessPercent, int direction, uint packedColor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsKeyboard) return;
        lock (TransportLock)
        {
            byte red = (byte)packedColor, green = (byte)(packedColor >> 8), blue = (byte)(packedColor >> 16);
            ApplyPatternCore(patternSelect, speed, brightnessPercent, direction, red, green, blue);
        }
    }

    private void ApplyPatternCore(int pattern, int speed, byte brightnessPercent, int direction,
        byte red, byte green, byte blue)
    {
        byte[] packet = pattern >= 7
            ? AcerEcLightingProtocol.PadPatternPacket16(
                AcerEcLightingProtocol.CreateKeyboardPatternPacket9(speed, brightnessPercent, red, green, blue))
            : AcerEcLightingProtocol.CreateKeyboardPatternPacket16(
                AcerEcLightingProtocol.CreateKeyboardPatternWord(pattern, speed, brightnessPercent, direction, red, green, blue));
        Invoke("SetGamingKBBacklight", packet);
    }

    /// <summary>
    /// Reads the raw backlight/pattern byte from the EC (<c>GetGamingKBBacklight</c>).
    /// Static pattern reports byte0 = 1; dynamic patterns report 2..7.
    /// </summary>
    public bool TryReadBacklightPattern(out byte rawPatternByte)
    {
        rawPatternByte = 0;
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsKeyboard) return false;
        lock (TransportLock)
        {
            try
            {
                using var input = _instance.GetMethodParameters("GetGamingKBBacklight");
                using var result = _instance.InvokeMethod("GetGamingKBBacklight", input, null);
                if (result?["gmOutput"] is not byte[] bytes || bytes.Length == 0) return false;
                rawPatternByte = bytes[0];
                return true;
            }
            catch { return false; }
        }
    }

    // ---- Readback (verified working on PH317-55) --------------------------------------

    /// <summary>
    /// Writes exactly one keyboard zone (one <c>SetGamingRgbKb</c> call) through the same WMI path
    /// the lighting UI uses. Used by the self-test so a zone write costs one EC write, not four.
    /// </summary>
    public void ApplyZoneColor(int zeroBasedZone, uint packedColor, byte? brightnessPercent = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsKeyboard || zeroBasedZone < 0 || zeroBasedZone >= LedCount)
            throw new ArgumentOutOfRangeException(nameof(zeroBasedZone));
        lock (TransportLock)
        {
            byte red = (byte)packedColor, green = (byte)(packedColor >> 8), blue = (byte)(packedColor >> 16);
            if (_ledBehaviorVariant)
            {
                if (!TryReadZoneMask(out int mask)) mask = 0xF;
                if ((mask & (1 << zeroBasedZone)) == 0)
                    InvokeUInt64("SetGamingLEDBehavior",
                        AcerEcLightingProtocol.CreateKeyboardZoneEnable(mask | (1 << zeroBasedZone)));
            }
            if (_rgbKbUsesUInt32)
                Invoke("SetGamingRgbKb", AcerEcLightingProtocol.CreateKeyboardZoneColor32(zeroBasedZone, red, green, blue));
            else
                Invoke("SetGamingRgbKb", AcerEcLightingProtocol.CreateKeyboardZoneColor(zeroBasedZone, red, green, blue));
            if (brightnessPercent is byte brightness && _backlightPacket10)
                Invoke("SetGamingKBBacklight",
                    AcerEcLightingProtocol.CreateKeyboardBacklightPacket10(
                        AcerEcLightingProtocol.BrightnessLevelFromPercent(brightness)));
        }
    }

    /// <summary>True when this device can read live colours back from the EC instead of trusting XML.</summary>
    public bool SupportsReadback => IsKeyboard;

    /// <summary>Reads one zone colour from the EC (<c>GetGamingRgbKb(zoneBit)</c>).</summary>
    public bool TryReadZoneColor(int zeroBasedZone, out uint packedColor)
    {
        packedColor = 0;
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsKeyboard || zeroBasedZone < 0 || zeroBasedZone >= LedCount) return false;
        lock (TransportLock)
        {
            try
            {
                if (!TryInvokeGet("GetGamingRgbKb", (uint)(1u << zeroBasedZone), out ulong raw)) return false;
                if (!AcerEcLightingProtocol.TryDecodeKeyboardZoneColor(raw, out byte red, out byte green, out byte blue))
                    return false;
                packedColor = (uint)(red | (green << 8) | (blue << 16));
                return true;
            }
            catch { return false; }
        }
    }

    /// <summary>Reads the zone enable mask from the EC (<c>GetGamingLED(8)</c>).</summary>
    public bool TryReadZoneMask(out int zoneMask)
    {
        zoneMask = 0;
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsKeyboard) return false;
        lock (TransportLock)
        {
            try
            {
                if (!TryInvokeGet("GetGamingLED", 8u, out ulong raw)) return false;
                zoneMask = AcerEcLightingProtocol.DecodeKeyboardZoneEnable(raw);
                return true;
            }
            catch { return false; }
        }
    }

    private bool TryInvokeGet(string method, uint selector, out ulong output)
    {
        output = 0;
        using var input = _instance.GetMethodParameters(method);
        input["gmInput"] = selector;
        using var result = _instance.InvokeMethod(method, input, null);
        if (result?["gmOutput"] is not { } raw) return false;
        output = Convert.ToUInt64(raw);
        return true;
    }

    private void Invoke(string method, object value)
    {
        using var input = _instance.GetMethodParameters(method);
        input["gmInput"] = value;
        using var output = _instance.InvokeMethod(method, input, null);
        if (output is null || output["gmOutput"] is null || (Convert.ToUInt64(output["gmOutput"]) & 0xFF) != 0)
            throw new InvalidOperationException($"Acer EC {method} rejected the lighting update.");
    }

    private void InvokeUInt64(string method, ulong value)
    {
        using var input = _instance.GetMethodParameters(method);
        input["gmInput"] = value;
        using var output = _instance.InvokeMethod(method, input, null);
        // SetGamingLEDBehavior answers gmOutput=1 on this firmware even when the zone mask is
        // already correct, so only a missing reply is treated as a failure here.
        if (output is null || output["gmOutput"] is null)
            throw new InvalidOperationException($"Acer EC {method} returned no reply.");
    }

    public void Dispose()
    {
        lock (TransportLock)
        {
            if (_disposed) return;
            _disposed = true;
            _instance.Dispose();
        }
    }
}
