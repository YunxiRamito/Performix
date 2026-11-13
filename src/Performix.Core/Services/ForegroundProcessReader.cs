using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PreySense.Core.Services;

/// <summary>
/// Reads the executable name of the current foreground window (per-process fan-curve bindings).
/// Read-only Win32 queries; never throws and caches for a second to keep telemetry cheap.
/// </summary>
public static class ForegroundProcessReader
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint flags, StringBuilder buffer, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private static readonly object Gate = new();
    private static string? _cachedName;
    private static long _cachedAtTimestamp;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(1);

    /// <summary>Foreground process file name (e.g. "game.exe"), or null when it cannot be read.</summary>
    public static string? GetForegroundProcessName()
    {
        lock (Gate)
        {
            long now = Stopwatch.GetTimestamp();
            if (now - _cachedAtTimestamp < (long)(Stopwatch.Frequency * CacheDuration.TotalSeconds))
                return _cachedName;

            _cachedAtTimestamp = now;
            _cachedName = Read();
            return _cachedName;
        }
    }

    private static string? Read()
    {
        try
        {
            IntPtr window = GetForegroundWindow();
            if (window == IntPtr.Zero) return null;
            GetWindowThreadProcessId(window, out uint processId);
            if (processId == 0) return null;

            IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (handle == IntPtr.Zero) return null;
            try
            {
                var buffer = new StringBuilder(1024);
                int size = buffer.Capacity;
                if (!QueryFullProcessImageName(handle, 0, buffer, ref size)) return null;
                string path = buffer.ToString(0, size);
                string name = Path.GetFileName(path);
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
            finally
            {
                CloseHandle(handle);
            }
        }
        catch
        {
            return null;
        }
    }
}
