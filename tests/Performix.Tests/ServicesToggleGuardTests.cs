using Predex.WinUI.Services;

namespace PreySense.Tests;

/// <summary>
/// The Services page switch: one control, one destructive direction.
///
/// UX review item 2 asked for a confirmation before the batch stop. That decision lives in
/// ServicesToggleGuard (dependency-free, linked into this project) so these tests are the
/// evidence for it: the harmless direction stays one click, the destructive direction never
/// runs without an explicit "yes", and a binding echo can never re-trigger the action.
/// </summary>
[TestClass]
public class ServicesToggleGuardTests
{
    [TestMethod]
    public void SwitchingOff_RequiresConfirmation()
    {
        // Services currently enabled, user turns the switch off → stop + disable all.
        Assert.AreEqual(
            ServicesToggleAction.ConfirmThenApply,
            ServicesToggleGuard.Classify(newIsOn: false, currentIsEnabled: true, canManage: true));
    }

    [TestMethod]
    public void SwitchingOn_AppliesDirectly()
    {
        // Restoring the OEM services is the safe direction: no dialog in the way.
        Assert.AreEqual(
            ServicesToggleAction.ApplyDirectly,
            ServicesToggleGuard.Classify(newIsOn: true, currentIsEnabled: false, canManage: true));
    }

    [TestMethod]
    public void BindingEcho_IsIgnored()
    {
        // The OneWay IsOn push raises Toggled too; acting on it would loop.
        Assert.AreEqual(
            ServicesToggleAction.Ignore,
            ServicesToggleGuard.Classify(newIsOn: true, currentIsEnabled: true, canManage: true));
        Assert.AreEqual(
            ServicesToggleAction.Ignore,
            ServicesToggleGuard.Classify(newIsOn: false, currentIsEnabled: false, canManage: true));
    }

    [TestMethod]
    public void UnmanageableVendor_IsIgnored()
    {
        // Only Acer has a backend in this build; every other brand is read only.
        Assert.AreEqual(
            ServicesToggleAction.Ignore,
            ServicesToggleGuard.Classify(newIsOn: false, currentIsEnabled: true, canManage: false));
        Assert.AreEqual(
            ServicesToggleAction.Ignore,
            ServicesToggleGuard.Classify(newIsOn: true, currentIsEnabled: false, canManage: false));
    }

    [TestMethod]
    public void CancelOrUnshownDialog_BlocksTheStop()
    {
        Assert.IsTrue(ServicesToggleGuard.MayProceed(true), "a confirmed dialog must allow the stop");
        Assert.IsFalse(ServicesToggleGuard.MayProceed(false), "cancel must block the stop");
        Assert.IsFalse(ServicesToggleGuard.MayProceed(null),
            "a dialog that could not be shown must not be read as consent");
    }
}
