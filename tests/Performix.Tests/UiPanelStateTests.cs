// Personalisation → Interface: the home-panel switches and the fields of the status bar
// (settings IA v2).
//
// Everything is persisted inside %LOCALAPPDATA%\Predex\appearance.json — the file the appearance
// controls already own — as a "UiPanels" object. These tests prove the write/read round-trip of
// every switch and field, which is the part that cannot be checked by looking at the UI:
//
//   • each row switch and each status-bar field survives a write → read cycle in both directions,
//   • the status-bar fields are independent of each other and of the row switch above them,
//   • the defaults are the ones the UI ships with (rows on; status bar = temperature + fan RPM),
//   • an appearance.json written by an older build reads as those defaults instead of throwing,
//   • the other members of appearance.json (theme / material / accent) survive a panel write.
//
// UiPanelState.cs is linked into this project as source (the same technique the update sources
// use) because it is deliberately free of WinUI: AppearanceService is the only other consumer and
// it cannot be compiled here.

using Predex.WinUI.Services;

namespace PreySense.Tests;

[TestClass]
public class UiPanelStateTests
{
    private static readonly UiPanel[] RowSwitches =
    [
        UiPanel.StatusBar, UiPanel.GpuBar, UiPanel.RefreshRateBar, UiPanel.WidgetBar,
    ];

    private static readonly UiPanel[] StatusFields =
    [
        UiPanel.StatusBarTemperature, UiPanel.StatusBarUsage, UiPanel.StatusBarFrequency, UiPanel.StatusBarFan,
    ];

    [TestMethod]
    [DataRow(UiPanel.StatusBar)]
    [DataRow(UiPanel.GpuBar)]
    [DataRow(UiPanel.RefreshRateBar)]
    [DataRow(UiPanel.WidgetBar)]
    public void EachRowSwitch_RoundTripsThroughAppearanceJson(UiPanel panel)
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
    public void EachRowSwitch_ReadsBackAsVisibleAfterBeingTurnedOnAgain(UiPanel panel)
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
    public void TheFourRowSwitches_AreIndependent()
    {
        var panels = new UiPanelVisibility();
        panels.Set(UiPanel.GpuBar, false);

        UiPanelVisibility read = UiPanelDocument.Read(UiPanelDocument.Write("{}", panels));

        Assert.IsFalse(read.GpuBar);
        Assert.IsTrue(read.StatusBar);
        Assert.IsTrue(read.RefreshRateBar);
        Assert.IsTrue(read.WidgetBar);
    }

    // ── The four status-bar fields ───────────────────────────────────────────────
    [TestMethod]
    [DataRow(UiPanel.StatusBarTemperature)]
    [DataRow(UiPanel.StatusBarUsage)]
    [DataRow(UiPanel.StatusBarFrequency)]
    [DataRow(UiPanel.StatusBarFan)]
    public void EachStatusField_RoundTripsThroughAppearanceJson(UiPanel field)
    {
        var off = new UiPanelVisibility();
        off.Set(field, false);
        Assert.IsFalse(UiPanelDocument.Read(UiPanelDocument.Write("{}", off)).Get(field),
            $"{field} did not stay off through appearance.json");

        // ... and back on again, from the document the previous write produced.
        var on = new UiPanelVisibility();
        on.Set(field, true);
        Assert.IsTrue(UiPanelDocument.Read(UiPanelDocument.Write("{}", on)).Get(field),
            $"{field} did not stay on through appearance.json");
    }

    [TestMethod]
    public void EachStatusField_IsIndependentOfTheOthersAndOfTheRowSwitch()
    {
        foreach (UiPanel field in StatusFields)
        {
            var panels = new UiPanelVisibility();
            panels.Set(field, !panels.Get(field));   // flip exactly one

            UiPanelVisibility read = UiPanelDocument.Read(UiPanelDocument.Write("{}", panels));

            foreach (UiPanel other in StatusFields)
            {
                Assert.AreEqual(panels.Get(other), read.Get(other),
                    $"flipping {field} also changed {other}");
            }

            Assert.IsTrue(read.StatusBar, $"flipping {field} must not hide the status bar row");
            Assert.IsTrue(read.AllVisible, $"flipping a status-bar field must not hide a panel row");
        }
    }

    [TestMethod]
    public void Defaults_AreTheShippedOnes()
    {
        var panels = new UiPanelVisibility();

        foreach (UiPanel row in RowSwitches)
            Assert.IsTrue(panels.Get(row), $"{row} must start visible");

        Assert.IsTrue(panels.StatusBarTemperature, "temperature must start on");
        Assert.IsTrue(panels.StatusBarFan, "fan speed must start on");
        Assert.IsFalse(panels.StatusBarUsage, "usage must start off (two fields fit one 320 DIP line)");
        Assert.IsFalse(panels.StatusBarFrequency, "frequency must start off");
        Assert.IsTrue(panels.AnyStatusFieldVisible);
    }

    [TestMethod]
    public void EveryStatusFieldOff_HidesTheRowWithoutHidingThePanel()
    {
        var panels = new UiPanelVisibility();
        foreach (UiPanel field in StatusFields) panels.Set(field, false);

        UiPanelVisibility read = UiPanelDocument.Read(UiPanelDocument.Write("{}", panels));

        Assert.IsFalse(read.AnyStatusFieldVisible, "the row has nothing left to print");
        Assert.IsTrue(read.AllVisible, "the other panel rows are unaffected");
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
        panels.Set(UiPanel.StatusBarUsage, true);
        string document = UiPanelDocument.Write(appearance, panels);

        StringAssert.Contains(document, "\"Theme\": \"Light\"");
        StringAssert.Contains(document, "\"Material\": \"Solid\"");
        StringAssert.Contains(document, "\"WindowStyle\": \"Windows10\"");
        StringAssert.Contains(document, "\"AccentColor\": \"#8B5CF6\"");
        StringAssert.Contains(document, "UiPanels");
        Assert.IsFalse(UiPanelDocument.Read(document).WidgetBar);
        Assert.IsTrue(UiPanelDocument.Read(document).StatusBarUsage);
    }

    [TestMethod]
    public void MissingOrDamagedState_ReadsAsAllVisible()
    {
        foreach (string? json in new[] { null, "", "   ", "not json", "[]", "null" })
        {
            UiPanelVisibility panels = UiPanelDocument.Read(json);
            Assert.IsTrue(panels.AllVisible, $"'{json}' must read as the default (all visible)");
            Assert.IsTrue(panels.StatusBarTemperature && panels.StatusBarFan,
                $"'{json}' must read as the default status-bar fields");
            Assert.IsFalse(panels.StatusBarUsage || panels.StatusBarFrequency,
                $"'{json}' must read as the default status-bar fields");
        }
    }

    [TestMethod]
    public void AbsentMemberInAnOlderFile_ReadsAsAllVisible()
    {
        // An appearance.json written before these switches existed has no "UiPanels" member.
        const string older = """{ "Theme": "Dark", "AccentColor": "#3B82F6" }""";
        Assert.IsTrue(UiPanelDocument.Read(older).AllVisible);
    }

    [TestMethod]
    public void OlderFileWithoutTheStatusFields_ReadsThemAsTheDefaults()
    {
        // A file written by the build that only had the four row switches: the four field keys
        // are missing, so they must come back as the shipped defaults, not as "off".
        const string older = """
            {
              "Theme": "Dark",
              "UiPanels": { "StatusBar": true, "GpuBar": false, "RefreshRateBar": true, "WidgetBar": true }
            }
            """;

        UiPanelVisibility panels = UiPanelDocument.Read(older);

        Assert.IsFalse(panels.GpuBar, "the stored row switch must survive");
        Assert.IsTrue(panels.StatusBarTemperature, "temperature reads as its default (on)");
        Assert.IsTrue(panels.StatusBarFan, "fan speed reads as its default (on)");
        Assert.IsFalse(panels.StatusBarUsage, "usage reads as its default (off)");
        Assert.IsFalse(panels.StatusBarFrequency, "frequency reads as its default (off)");
    }
}
