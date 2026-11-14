using System;
using System.Diagnostics;
using System.Management;

namespace PreySense.Gpu
{
    /// <summary>
    /// Which GPU is driving the panel right now.
    ///
    /// This is the one fact the three-mode decision needs that no NVIDIA API provides on a
    /// machine without DDS. Windows already knows it: a display adapter that owns the panel
    /// reports a live mode in <c>Win32_VideoController</c> (CurrentHorizontalResolution), while a
    /// render-only adapter reports none.
    ///
    /// Cross-checked on the reference PH317-55 against nvidia-smi's own fields
    /// (<c>--query-gpu=display_attached,display_active</c> → "No, Disabled"):
    ///   Intel(R) UHD Graphics         2560 x 1440  ← live mode → iGPU drives the panel
    ///   NVIDIA GeForce RTX 3070       (empty)      ← no display attached
    ///   GameViewer Virtual Display    (empty)
    /// </summary>
    public static class GpuDisplayRoute
    {
        private static readonly object Gate = new();
        private static bool? _cached;
        private static long _cachedTicks;
        private static readonly long CacheTicks = Stopwatch.Frequency * 2; // ~2 s

        /// <summary>
        /// true = the integrated GPU (Intel / AMD) drives the panel, false = it does not,
        /// null = no adapter reported a live mode, so nothing can be concluded.
        /// </summary>
        public static bool? IsDisplayDrivenByIgpu()
        {
            long now = Stopwatch.GetTimestamp();
            lock (Gate)
            {
                if (_cachedTicks != 0 && now - _cachedTicks < CacheTicks)
                    return _cached;
            }

            bool? route = Read();
            lock (Gate)
            {
                if (_cached != route)
                    AppLogger.Log($"GpuDisplayRoute: panel is driven by {Describe(route)}.");
                _cached = route;
                _cachedTicks = now;
            }
            return route;
        }

        /// <summary>Drops the cache (called after a display switch or a PnP device change).</summary>
        public static void Invalidate()
        {
            lock (Gate)
            {
                _cached = null;
                _cachedTicks = 0;
            }
        }

        public static string Describe(bool? route) => route switch
        {
            true => "the integrated GPU",
            false => "the discrete GPU",
            _ => "an unknown adapter (no live display mode reported)"
        };

        private static bool? Read()
        {
            try
            {
                bool integratedDrives = false;
                bool discreteDrives = false;
                using var searcher = new ManagementObjectSearcher(
                    "SELECT PNPDeviceID, CurrentHorizontalResolution FROM Win32_VideoController");
                foreach (ManagementObject gpu in searcher.Get())
                {
                    using (gpu)
                    {
                        if (!HasLiveDisplayMode(gpu)) continue;
                        string id = (gpu["PNPDeviceID"] as string ?? string.Empty).ToUpperInvariant();
                        if (id.Contains("VEN_8086") || id.Contains("VEN_1002"))
                            integratedDrives = true;
                        else if (id.Contains("VEN_10DE"))
                            discreteDrives = true;
                    }
                }

                // A dGPU-only boot leaves the iGPU offline, so only one of these is normally set.
                // If both report a mode (clone / extended across two adapters), the dGPU owns the
                // panel — the conservative answer, because that is the state the user would have
                // to leave to get back to hybrid.
                if (discreteDrives) return false;
                if (integratedDrives) return true;
                return null;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"GpuDisplayRoute: reading Win32_VideoController failed: {ex.Message}");
                return null;
            }
        }

        private static bool HasLiveDisplayMode(ManagementBaseObject gpu)
        {
            object? width = gpu["CurrentHorizontalResolution"];
            if (width is null) return false;
            try { return Convert.ToUInt32(width) > 0; }
            catch { return false; }
        }
    }
}
