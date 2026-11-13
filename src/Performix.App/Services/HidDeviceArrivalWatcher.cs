// HID 设备热插拔监听 — 注册 WM_DEVICECHANGE 通知到主窗口 HWND。

using System;
using System.Runtime.InteropServices;

namespace PreySense.Frontend.Services
{
    public sealed class HidDeviceArrivalWatcher : IDisposable
    {
        private const int DBT_DEVTYP_DEVICEINTERFACE = 0x00000005;
        private const uint DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEV_BROADCAST_DEVICEINTERFACE
        {
            public int dbcc_size;
            public int dbcc_devicetype;
            public int dbcc_reserved;
            public Guid dbcc_classguid;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string dbcc_name;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr RegisterDeviceNotification(
            IntPtr hRecipient, IntPtr notificationFilter, uint flags);

        [DllImport("user32.dll")]
        private static extern bool UnregisterDeviceNotification(IntPtr handle);

        private IntPtr _notificationHandle = IntPtr.Zero;
        private bool _disposed;

        public HidDeviceArrivalWatcher(IntPtr hWnd)
        {
            RegisterForHidNotifications(hWnd);
        }

        private void RegisterForHidNotifications(IntPtr hWnd)
        {
            // HID GUID: {4D1E55B2-F16F-11CF-88CB-001111000030}
            var hidGuid = new Guid(0x4D1E55B2, 0xF16F, 0x11CF,
                0x88, 0xCB, 0x00, 0x11, 0x11, 0x00, 0x00, 0x30);

            var filter = new DEV_BROADCAST_DEVICEINTERFACE
            {
                dbcc_devicetype = DBT_DEVTYP_DEVICEINTERFACE,
                dbcc_classguid = hidGuid,
                dbcc_name = string.Empty
            };
            filter.dbcc_size = Marshal.SizeOf(filter);

            IntPtr pFilter = Marshal.AllocHGlobal(filter.dbcc_size);
            try
            {
                Marshal.StructureToPtr(filter, pFilter, false);
                _notificationHandle = RegisterDeviceNotification(
                    hWnd, pFilter, DEVICE_NOTIFY_WINDOW_HANDLE);
            }
            finally
            {
                Marshal.FreeHGlobal(pFilter);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_notificationHandle != IntPtr.Zero)
                UnregisterDeviceNotification(_notificationHandle);
        }
    }
}
