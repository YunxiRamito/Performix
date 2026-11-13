using PreySense.Core.Diagnostics;
using PreySense.Gpu;

// PreySenseProbe — read-only sensor self-check for the no-driver fork.
// Optional argument: path of the report file (defaults to %ProgramData%\PreySense\logs\...).
// --gpuoc : read-only NVIDIA clock-offset capability probe (no write).
// --rgb-test : EC keyboard lighting write/readback/restore test (3 EC writes max, restored).

// --rgb-test : exercises the repaired Acer EC keyboard lighting path.
// Captures zone 1 colour, writes RED through AcerEcLightingDevice, reads it back, restores the
// captured colour and verifies the restore. At most 3 EC writes (colour + optional zone enable).
if (args.Length > 0 && args[0] == "--rgb-test")
{
    Console.WriteLine("=== Acer EC keyboard RGB write/readback test ===");
    var devices = PreySense.Rgb.AcerEcLightingDevice.Discover(out string discoverDiagnostic);
    Console.WriteLine($"discover  : {devices.Count} device(s) — {discoverDiagnostic}");
    var keyboard = devices.FirstOrDefault(d => d.IsKeyboard);
    if (keyboard is null)
    {
        Console.WriteLine("VERDICT   : FAIL — no EC keyboard device was discovered.");
        return 3;
    }
    Console.WriteLine($"device    : {keyboard.Name} ({keyboard.LedCount} zones)");

    if (!keyboard.TryReadZoneColor(0, out uint original))
    {
        Console.WriteLine("capture   : FAIL — GetGamingRgbKb zone 1 unreadable");
        return 4;
    }
    keyboard.TryReadBacklightPattern(out byte originalPatternByte);
    Console.WriteLine($"capture   : zone1 = 0x{original:X6}, pattern byte = {originalPatternByte} (0 = static)");

    const uint red = 0x0000FF; // packed 0x00BBGGRR → R=FF
    try
    {
        // Pattern FIRST: while the EC runs a dynamic pattern the per-zone colours are ignored.
        keyboard.ApplyPattern(0, speed: 25, brightnessPercent: 75, direction: 0, red);
        keyboard.ApplyZoneColor(0, red);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"write     : FAIL — {ex.GetType().Name}: {ex.Message}");
        return 5;
    }

    bool patternReadOk = keyboard.TryReadBacklightPattern(out byte afterPatternByte);
    Console.WriteLine($"pattern   : GetGamingKBBacklight byte0 = {afterPatternByte} (expect 0 = static) => " +
                      $"{(patternReadOk && afterPatternByte == 0 ? "OK" : "MISMATCH")}");
    bool readOk = keyboard.TryReadZoneColor(0, out uint afterWrite);
    Console.WriteLine($"readback  : zone1 = 0x{afterWrite:X6} (expect 0x0000FF) => {(readOk && afterWrite == red ? "OK" : "MISMATCH")}");

    try
    {
        // Restore must include the pattern, otherwise the keyboard is left in static mode.
        keyboard.ApplyZoneColor(0, original);
        keyboard.ApplyPattern(Math.Clamp((int)originalPatternByte, 0, 7), speed: 25, brightnessPercent: 75,
            direction: 0, original);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"restore   : FAIL — {ex.GetType().Name}: {ex.Message}");
        return 6;
    }

    bool restorePatternOk = keyboard.TryReadBacklightPattern(out byte restoredPatternByte);
    bool restoreOk = keyboard.TryReadZoneColor(0, out uint afterRestore);
    Console.WriteLine($"restored  : zone1 = 0x{afterRestore:X6} (expected 0x{original:X6}), pattern byte = {restoredPatternByte} => " +
                      $"{(restoreOk && afterRestore == original && restorePatternOk && restoredPatternByte == originalPatternByte ? "OK" : "MISMATCH")}");

    bool passed = readOk && afterWrite == red && patternReadOk && afterPatternByte == 0
                  && restoreOk && afterRestore == original
                  && restorePatternOk && restoredPatternByte == originalPatternByte;
    Console.WriteLine(passed
        ? "VERDICT   : OK — pattern set to static, colour written/read back, pattern + colour restored."
        : "VERDICT   : FAIL — see the steps above.");
    return passed ? 0 : 7;
}

// --rgb-reset : restores the OEM default (static pattern + 4 zones #00AEC7) — 5 EC writes.
if (args.Length > 0 && args[0] == "--rgb-reset")
{
    var devices = PreySense.Rgb.AcerEcLightingDevice.Discover(out string diag);
    var keyboard = devices.FirstOrDefault(d => d.IsKeyboard);
    if (keyboard is null) { Console.WriteLine($"FAIL — no keyboard ({diag})"); return 3; }
    const uint cyan = 0xC7AE00; // 0x00BBGGRR = #00AEC7
    keyboard.ApplyColors(new[] { cyan, cyan, cyan, cyan }, 75);
    bool patternOk = keyboard.TryReadBacklightPattern(out byte pattern);
    Console.WriteLine($"reset: pattern byte = {pattern} (expect 0 = static)");
    bool colorsOk = true;
    for (int zone = 0; zone < 4; zone++)
    {
        bool ok = keyboard.TryReadZoneColor(zone, out uint colour);
        colorsOk &= ok && colour == cyan;
        Console.WriteLine($"zone{zone + 1}: 0x{(ok ? colour : 0):X6} (expect 0xC7AE00) => {(ok && colour == cyan ? "OK" : "MISMATCH")}");
    }
    Console.WriteLine(patternOk && colorsOk ? "VERDICT: static #00AEC7 restored." : "VERDICT: restore incomplete.");
    return patternOk && colorsOk ? 0 : 1;
}

if (args.Length > 0 && (args[0] == "--gpuoc" || args[0] == "--gpu-oc"))
{
    Console.WriteLine("=== NVIDIA clock-offset capability probe (read-only) ===");
    NvidiaGpuControl gpu;
    try { gpu = new NvidiaGpuControl(); }
    catch (Exception ex) { Console.WriteLine($"ctor failed: {ex.GetType().Name}: {ex.Message}"); return 4; }

    Console.WriteLine($"IsValid={gpu.IsValid}  IsNvidia={gpu.IsNvidia}");
    if (!gpu.IsValid) return 4;

    bool ocOk = gpu.TryReadClockTuning(out var ocState, out var ocStatus);
    Console.WriteLine($"TryReadClockTuning => {ocOk}");
    Console.WriteLine($"status  : {ocStatus}");
    if (ocState is not null)
    {
        Console.WriteLine($"core    : {ocState.Core.MinimumMhz} .. {ocState.Core.MaximumMhz} MHz (step {GpuClockTuningState.CoreStepMhz})");
        Console.WriteLine($"memory  : {ocState.Memory.MinimumMhz} .. {ocState.Memory.MaximumMhz} MHz (step {GpuClockTuningState.MemoryStepMhz})");
        Console.WriteLine($"current : core {ocState.Current.CoreMhz} MHz / mem {ocState.Current.MemoryMhz} MHz");
        Console.WriteLine("VERDICT : GPU overclocking available (NVAPI PerformanceStates20 editable)");
        return 0;
    }
    Console.WriteLine("VERDICT : GPU overclocking NOT available on this GPU/driver");
    return 1;
}

if (args.Length > 0 && (args[0] == "--gpuoc-set"))
{
    // Reversible write test: --gpuoc-set <coreMhz> <memMhz>   (use 0 0 to reset)
    int core = args.Length > 1 ? int.Parse(args[1]) : 0;
    int mem = args.Length > 2 ? int.Parse(args[2]) : 0;
    Console.WriteLine($"=== NVIDIA clock-offset WRITE test (core={core} mem={mem} MHz) ===");
    var gpu2 = new NvidiaGpuControl();
    if (!gpu2.IsValid) { Console.WriteLine("no NVIDIA GPU"); return 4; }
    if (!gpu2.TryReadClockTuning(out var before, out var s0)) { Console.WriteLine($"read failed: {s0}"); return 1; }
    Console.WriteLine($"before: core {before!.Current.CoreMhz} / mem {before.Current.MemoryMhz} MHz");
    bool applied = gpu2.TryApplyClockOffsets(new GpuClockOffsets(core, mem), out var s1);
    Console.WriteLine($"TryApplyClockOffsets => {applied}  ({s1})");
    if (gpu2.TryReadClockTuning(out var after, out var s2))
        Console.WriteLine($"after : core {after!.Current.CoreMhz} / mem {after.Current.MemoryMhz} MHz  ({s2})");
    return applied ? 0 : 1;
}

// --set-mode <hex> [restoreHex] : hardware acceptance test for the pipe operating-mode channel
// (cmd 32 Set + cmd 33 readback). The optional third argument restores the original mode first.
if (args.Length > 0 && args[0] == "--set-mode")
{
    var pipe = new PreySense.Core.Hardware.AcerPipeSensorSource();
    byte target = Convert.ToByte(args[1], 16);
    Console.WriteLine($"=== pipe operating-mode WRITE test (target 0x{target:X2}) ===");
    if (pipe.TryGetPowerMode(out byte before, out string s0))
        Console.WriteLine($"before : 0x{before:X2}  ({s0})");
    else
        Console.WriteLine($"before : unavailable ({s0})");

    bool ok = pipe.TrySetPowerMode(target, out string s1);
    Console.WriteLine($"TrySetPowerMode => {ok}  ({s1})");
    if (pipe.TryGetPowerMode(out byte after, out string s2))
        Console.WriteLine($"after  : 0x{after:X2}  ({s2})");

    if (args.Length > 2 && byte.TryParse(args[2], System.Globalization.NumberStyles.HexNumber, null, out byte restore))
    {
        bool restored = pipe.TrySetPowerMode(restore, out string s3);
        Console.WriteLine($"restore 0x{restore:X2} => {restored}  ({s3})");
        if (pipe.TryGetPowerMode(out byte final, out _))
            Console.WriteLine($"final  : 0x{final:X2}");
    }
    return ok ? 0 : 1;
}

var (report, pipeOk, fanCurveReady) = AcerSensorProbe.Run();

Console.Write(report);

string logPath = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0])
    ? Path.GetFullPath(args[0])
    : Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "PreySense", "logs", $"sensor-probe-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

try
{
    Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
    File.WriteAllText(logPath, report);
    Console.WriteLine();
    Console.WriteLine($"report saved: {logPath}");
}
catch (Exception ex)
{
    Console.WriteLine();
    Console.WriteLine($"could not write report to '{logPath}': {ex.GetType().Name}: {ex.Message}");
}

// 0 = pipe telemetry OK (fan curve can run), 2 = pipe unreachable, 3 = pipe up but no live data.
return fanCurveReady ? 0 : (pipeOk ? 3 : 2);
