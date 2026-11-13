// WM_DEVICECHANGE decoding for the shell window procedure.
//
// HidDeviceArrivalWatcher registers the HID interface GUID on the shell HWND, so Windows posts
// WM_DEVICECHANGE there — but until now nothing in the WinUI app read the message: the only
// window procedure hook (TrayIconManager's subclass) handled tray callbacks only, and
// HidArrivalHub.NotifyArrived was only ever called by the WPF watcher, which is not compiled
// into this assembly. The practical effect was that a keyboard/USB-RGB device plugged in while
// the app was running never refreshed the lighting page and never reached the power-on lighting
// apply.
//
// The decision ("is this message a HID interface arrival/removal?") is pure and lives here so
// it can be tested without a window or a device.

using System;
using System.Runtime.InteropServices;

namespace PreySense.Frontend.Services
{
    internal static class DeviceChangeRouter
    {
        internal const uint WmDeviceChange = 0x0219;
        internal const int DbtDeviceArrival = 0x8000;
        internal const int DbtDeviceRemoveComplete = 0x8004;
        internal const int DbtDevtypDeviceInterface = 0x00000005;

        /// <summary>
        /// True for a device-interface arrival/removal. Config changes and volume events carry
        /// the same message number but are not what the lighting code waits for, and only
        /// DBT_DEVTYP_DEVICEINTERFACE payloads have a class GUID at all.
        /// </summary>
        internal static bool IsHidInterfaceChange(uint message, int eventType, int deviceType)
            => message == WmDeviceChange
               && (eventType == DbtDeviceArrival || eventType == DbtDeviceRemoveComplete)
               && deviceType == DbtDevtypDeviceInterface;

        /// <summary>
        /// dbcc_devicetype from a DEV_BROADCAST_* header. Offset 4 is common to every
        /// DEV_BROADCAST_* struct (size, devicetype); a null lParam is "no header", not a match.
        /// </summary>
        internal static int ReadDeviceType(IntPtr lParam)
            => lParam == IntPtr.Zero ? 0 : Marshal.ReadInt32(lParam, 4);
    }
}
