// The WM_DEVICECHANGE branch in TrayIconManager's window procedure is what finally connects
// Windows' HID hot-plug notification to HidArrivalHub.NotifyArrived in the WinUI build. The
// window procedure itself needs a live HWND, but the decision it makes is pure — that is what
// these tests pin down.

using System;
using PreySense.Frontend.Services;

namespace PreySense.Tests;

[TestClass]
public class DeviceChangeRouterTests
{
    private const int DbtDevtypVolume = 0x00000002;

    [TestMethod]
    public void HidArrival_NotifiesTheHub()
    {
        Assert.IsTrue(DeviceChangeRouter.IsHidInterfaceChange(
            DeviceChangeRouter.WmDeviceChange,
            DeviceChangeRouter.DbtDeviceArrival,
            DeviceChangeRouter.DbtDevtypDeviceInterface));
    }

    [TestMethod]
    public void HidRemoval_NotifiesTheHub()
    {
        // Unplugging has to refresh the lighting list just like plugging in does.
        Assert.IsTrue(DeviceChangeRouter.IsHidInterfaceChange(
            DeviceChangeRouter.WmDeviceChange,
            DeviceChangeRouter.DbtDeviceRemoveComplete,
            DeviceChangeRouter.DbtDevtypDeviceInterface));
    }

    [TestMethod]
    public void ConfigChange_IsNotAnArrival()
    {
        Assert.IsFalse(DeviceChangeRouter.IsHidInterfaceChange(
            DeviceChangeRouter.WmDeviceChange,
            0x0007 /* DBT_DEVNODES_CHANGED */,
            DeviceChangeRouter.DbtDevtypDeviceInterface));
    }

    [TestMethod]
    public void VolumeEvent_IsIgnored()
    {
        Assert.IsFalse(DeviceChangeRouter.IsHidInterfaceChange(
            DeviceChangeRouter.WmDeviceChange,
            DeviceChangeRouter.DbtDeviceArrival,
            DbtDevtypVolume));
    }

    [TestMethod]
    public void OtherMessages_AreIgnored()
    {
        // WM_TRAY_CALLBACK (WM_USER + 2048) must keep reaching the tray branches only.
        Assert.IsFalse(DeviceChangeRouter.IsHidInterfaceChange(
            0x0400, DeviceChangeRouter.DbtDeviceArrival, DeviceChangeRouter.DbtDevtypDeviceInterface));
    }

    [TestMethod]
    public void MissingBroadcastHeader_ReadsAsNotADeviceInterface()
    {
        Assert.AreEqual(0, DeviceChangeRouter.ReadDeviceType(IntPtr.Zero));
        Assert.IsFalse(DeviceChangeRouter.IsHidInterfaceChange(
            DeviceChangeRouter.WmDeviceChange,
            DeviceChangeRouter.DbtDeviceArrival,
            DeviceChangeRouter.ReadDeviceType(IntPtr.Zero)));
    }
}
