// Personalisation → Interface: the four home-panel switches (settings IA v2).
//
// The four switches are persisted inside %LOCALAPPDATA%\Predex\appearance.json — the file the
// appearance controls already own — as a "UiPanels" object. These tests prove the write/read
// round-trip of each switch, which is the part that cannot be checked by looking at the UI:
//
//   • each flag survives a write → read cycle in both directions,
//   • the four switches are independent (writing one does not reset the others),
//   • the other members of appearance.json (theme / material / accent) survive a panel write,
//   • a missing / damaged file reads as "all visible" instead of throwing (startup path).
//
// UiPanelState.cs is linked into this project as source (the same technique the update sources
// use) because it is deliberately free of WinUI: AppearanceService is the only other consumer and
// it cannot be compiled here.

using Predex.WinUI.Services;

namespace PreySense.Tests;

[TestClass]
public class UiPanelStateTests
{
    [TestMethod]
    [DataRow(UiPanel.StatusBar)]
    [DataRow(UiPanel.GpuBar)]
    [DataRow(UiPanel.RefreshRateBar)]
    [DataRow(UiPanel.WidgetBar)]
    public void EachSwitch_RoundTripsThroughAppearanceJson(UiPanel panel)
    {
        var hidden = new UiPanelVisibility();
        hidden.Set(panel, false);

        string document = UiPanelDocument.Write("{}", hidden);
        UiPanelVisibility read = UiPanelDocument.Read(document);

        Assert.IsFalse(read.Get(panel), $"{panel} did not stay hidden through appearance.json");
        Assert.IsTrue(hidden.AllVisible == false, "the fixture must actually hide something");
    }

    [TestMethod]
    [DataRow(UiPanel.StatusBar)]
    [DataRow(UiPanel.GpuBar)]
    [DataRow(UiPanel.RefreshRateBar)]
    [DataRow(UiPanel.WidgetBar)]
    public void EachSwitch_ReadsBackAsVisibleAfterBeingTurnedOnAgain(UiPanel panel)
    {
        var hidden = new UiPanelVisibility();
        hidden.Set(panel, false);
        string hiddenDocument = UiPanelDocument.Write("{}", hidden);

        // What the page does when the user flips it back on: read, flip, write.
        UiPanelVisibility reloaded = UiPanelDocument.Read(hiddenDocument);
        reloaded.Set(panel, true);
        string visibleDocument = UiPanelDocument.Write(hiddenDocument, reloaded);

        UiPanelVisibility final = UiPanelDocument.Read(visibleDocument);
        Assert.IsTrue(final.Get(panel), $"{panel} did not come back visible");
        Assert.IsTrue(final.AllVisible, "turning the last switch on must restore the full panel");
    }

    [TestMethod]
    public void TheFourSwitches_AreIndependent()
    {
        var panels = new UiPanelVisibility();
        panels.Set(UiPanel.GpuBar, false);

        UiPanelVisibility read = UiPanelDocument.Read(UiPanelDocument.Write("{}", panels));

        Assert.IsFalse(read.GpuBar);
        Assert.IsTrue(read.StatusBar);
        Assert.IsTrue(read.RefreshRateBar);
        Assert.IsTrue(read.WidgetBar);
    }

    [TestMethod]
    public void WritingPanels_KeepsTheAppearanceMembers()
    {
        const string appearance = """
            {
              "Theme": "Light",
              "Material": "Solid",
              "WindowStyle": "Windows10",
              "AccentMode": "Custom",
              "AccentColor": "#8B5CF6"
            }
            """;

        var panels = new UiPanelVisibility();
        panels.Set(UiPanel.WidgetBar, false);
        string document = UiPanelDocument.Write(appearance, panels);

        StringAssert.Contains(document, "\"Theme\": \"Light\"");
        StringAssert.Contains(document, "\"Material\": \"Solid\"");
        StringAssert.Contains(document, "\"WindowStyle\": \"Windows10\"");
        StringAssert.Contains(document, "\"AccentColor\": \"#8B5CF6\"");
        StringAssert.Contains(document, "UiPanels");
        Assert.IsFalse(UiPanelDocument.Read(document).WidgetBar);
    }

    [TestMethod]
    public void MissingOrDamagedState_ReadsAsAllVisible()
    {
        foreach (string? json in new[] { null, "", "   ", "not json", "[]", "null" })
        {
            UiPanelVisibility panels = UiPanelDocument.Read(json);
            Assert.IsTrue(panels.AllVisible, $"'{json}' must read as the default (all visible)");
        }
    }

    [TestMethod]
    public void AbsentMemberInAnOlderFile_ReadsAsAllVisible()
    {
        // An appearance.json written before these switches existed has no "UiPanels" member.
        const string older = """{ "Theme": "Dark", "AccentColor": "#3B82F6" }""";
        Assert.IsTrue(UiPanelDocument.Read(older).AllVisible);
    }
}
