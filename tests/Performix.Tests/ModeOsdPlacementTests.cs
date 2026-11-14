// Placement maths for the mode-switch overlay (docs\R6-OSD.md §2, TOAST-ANIMATION-SPEC §1).
//
// The bug these cover: the overlay window is created once and reused for the whole process, so
// its work area / DPI used to be captured at one show and reused after a resolution or
// refresh-rate switch. The card then landed on the old work-area bottom — off-screen after
// dropping to a smaller mode, clipped, or at the wrong px size after a DPI change.
//
// ModeOsdPlacement.cs is the fix, extracted as pure functions so the resolution × DPI matrix is
// a test instead of a screenshot nobody re-reads.

using Predex.WinUI.Views;

namespace Performix.Tests;

[TestClass]
public class ModeOsdPlacementTests
{
    private static OsdDisplay Display(int workWidth, int workHeight, int dpi, int workX = 0, int workY = 0)
        => new(workX, workY, workWidth, workHeight, dpi);

    // ── DIP → px ────────────────────────────────────────────────────────────────

    [TestMethod]
    public void ScaleDip_UsesTheMonitorsEffectiveDpi()
    {
        Assert.AreEqual(48, ModeOsdPlacement.ScaleDip(48, 96));
        Assert.AreEqual(60, ModeOsdPlacement.ScaleDip(48, 120));   // 125 %
        Assert.AreEqual(72, ModeOsdPlacement.ScaleDip(48, 144));   // 150 %
        Assert.AreEqual(84, ModeOsdPlacement.ScaleDip(48, 168));   // 175 %
        Assert.AreEqual(96, ModeOsdPlacement.ScaleDip(48, 192));   // 200 %
        Assert.AreEqual(240, ModeOsdPlacement.ScaleDip(160, 144));
        Assert.AreEqual(36, ModeOsdPlacement.ScaleDip(24, 144));
    }

    [TestMethod]
    public void ScaleDip_TreatsAFailedDpiQueryAs96()
    {
        // A 0 DPI (GetDpiForMonitor failed) must not collapse the card to nothing.
        Assert.AreEqual(48, ModeOsdPlacement.ScaleDip(48, 0));
        Assert.AreEqual(160, ModeOsdPlacement.ScaleDip(160, 0));
    }

    // ── Card width ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void CardWidthDip_IsClampedToTheSpecBounds()
    {
        Assert.AreEqual(160d, ModeOsdPlacement.CardWidthDip(0d));      // 32 + 32 + nothing → min
        Assert.AreEqual(160d, ModeOsdPlacement.CardWidthDip(28d));     // two CJK glyphs still → min
        Assert.AreEqual(164d, ModeOsdPlacement.CardWidthDip(100d));    // 64 + 100, inside the range
        Assert.AreEqual(280d, ModeOsdPlacement.CardWidthDip(1000d));   // ellipsis at the 280 cap
    }

    [TestMethod]
    public void CardWidthDip_IgnoresAnUnmeasurableTextWidth()
    {
        Assert.AreEqual(160d, ModeOsdPlacement.CardWidthDip(double.NaN));
        Assert.AreEqual(160d, ModeOsdPlacement.CardWidthDip(double.PositiveInfinity));
        Assert.AreEqual(160d, ModeOsdPlacement.CardWidthDip(-40d));
    }

    // ── Placement: the resolution × DPI matrix ──────────────────────────────────

    [TestMethod]
    public void Compute_1080p_AtEachCommonScale()
    {
        // 1920×1080 with a 40 px taskbar → 1040 px of work area.
        var at100 = ModeOsdPlacement.Compute(0d, Display(1920, 1040, 96));
        Assert.AreEqual(880, at100.X);
        Assert.AreEqual(968, at100.RestY);
        Assert.AreEqual(160, at100.Width);
        Assert.AreEqual(48, at100.Height);

        var at150 = ModeOsdPlacement.Compute(0d, Display(1920, 1040, 144));
        Assert.AreEqual(840, at150.X);          // (1920 - 240) / 2
        Assert.AreEqual(932, at150.RestY);      // 1040 - 36 - 72
        Assert.AreEqual(240, at150.Width);
        Assert.AreEqual(72, at150.Height);

        var at200 = ModeOsdPlacement.Compute(0d, Display(1920, 1040, 192));
        Assert.AreEqual(800, at200.X);          // (1920 - 320) / 2
        Assert.AreEqual(896, at200.RestY);      // 1040 - 48 - 96
        Assert.AreEqual(320, at200.Width);
        Assert.AreEqual(96, at200.Height);
    }

    [TestMethod]
    public void Compute_1440pAnd4K_StayCentredAndBottomAligned()
    {
        // 2560×1440, no taskbar in the work area, 125 %.
        var qhd = ModeOsdPlacement.Compute(0d, Display(2560, 1440, 120));
        Assert.AreEqual(1180, qhd.X);           // (2560 - 200) / 2
        Assert.AreEqual(1350, qhd.RestY);       // 1440 - 30 - 60
        Assert.AreEqual(200, qhd.Width);
        Assert.AreEqual(60, qhd.Height);

        // 3840×2160 with a 40 px taskbar, 200 %.
        var uhd = ModeOsdPlacement.Compute(0d, Display(3840, 2120, 192));
        Assert.AreEqual(1760, uhd.X);           // (3840 - 320) / 2
        Assert.AreEqual(1976, uhd.RestY);       // 2120 - 48 - 96
        Assert.AreEqual(320, uhd.Width);
        Assert.AreEqual(96, uhd.Height);
    }

    [TestMethod]
    public void Compute_OffsetsTheRectWhenTheWorkAreaDoesNotStartAtTheOrigin()
    {
        // A secondary-style work area: the rect must be absolute, not monitor-relative.
        var placement = ModeOsdPlacement.Compute(0d, Display(1920, 1040, 96, workX: 1920, workY: 40));
        Assert.AreEqual(1920 + 880, placement.X);
        Assert.AreEqual(40 + 968, placement.RestY);
    }

    [TestMethod]
    public void Compute_GivesTheBottomMarginAwayBeforeTheCard()
    {
        // Work area barely taller than the card: the 24 DIP margin shrinks, the card does not.
        var tight = ModeOsdPlacement.Compute(0d, Display(800, 60, 96));
        Assert.AreEqual(48, tight.Height);
        Assert.AreEqual(0, tight.RestY);        // 60 - 12 - 48
        Assert.AreEqual(320, tight.X);

        // Work area shorter than the card: the card shrinks, and stays inside the work area.
        var tiny = ModeOsdPlacement.Compute(0d, Display(800, 40, 96));
        Assert.AreEqual(40, tiny.Height);
        Assert.AreEqual(0, tiny.RestY);
    }

    [TestMethod]
    public void Compute_ShrinksTheCardToANarrowWorkArea()
    {
        var narrow = ModeOsdPlacement.Compute(0d, Display(400, 300, 96));
        Assert.AreEqual(160, narrow.Width);
        Assert.AreEqual(120, narrow.X);
    }

    [TestMethod]
    public void Compute_UsesTheMeasuredTextWidth()
    {
        // 100 DIP of text → 164 DIP card → 164 px at 96 DPI.
        var wide = ModeOsdPlacement.Compute(100d, Display(1920, 1040, 96));
        Assert.AreEqual(164, wide.Width);
        Assert.AreEqual(878, wide.X);           // (1920 - 164) / 2

        // The 280 DIP cap: 280 px at 96 DPI, 420 px at 144 DPI.
        Assert.AreEqual(280, ModeOsdPlacement.Compute(1000d, Display(1920, 1040, 96)).Width);
        Assert.AreEqual(420, ModeOsdPlacement.Compute(1000d, Display(1920, 1040, 144)).Width);
    }

    /// <summary>
    /// The invariant behind the bug report: whatever the resolution and whatever the scale, the
    /// card rect is inside the work area. The old arithmetic only knew "work area bottom −
    /// 24 DIP − card height", so a rest position computed for a taller mode ended up below the
    /// screen after the resolution dropped.
    /// </summary>
    [TestMethod]
    public void Compute_AlwaysKeepsTheCardInsideTheWorkArea()
    {
        (int Width, int Height)[] resolutions =
        {
            (800, 600), (1024, 768), (1280, 720), (1366, 768),
            (1600, 900), (1920, 1080), (2560, 1440), (2880, 1800), (3840, 2160),
        };
        int[] dpis = { 96, 120, 144, 168, 192, 240, 288 };
        double[] textWidths = { 0, 28, 100, 1000 };

        foreach ((int width, int height) in resolutions)
        {
            // 0 / 40 / 48 px taskbars: the work area is never the full screen on this machine.
            foreach (int taskbar in new[] { 0, 40, 48 })
            {
                int workHeight = height - taskbar;
                foreach (int dpi in dpis)
                {
                    foreach (double textWidth in textWidths)
                    {
                        var area = Display(width, workHeight, dpi);
                        var placement = ModeOsdPlacement.Compute(textWidth, area);

                        string where = $"{width}x{workHeight} @{dpi}dpi text={textWidth} → {placement.Describe()}";
                        Assert.IsTrue(placement.X >= area.WorkX, $"left edge outside: {where}");
                        Assert.IsTrue(placement.X + placement.Width <= area.WorkX + area.WorkWidth,
                            $"right edge outside: {where}");
                        Assert.IsTrue(placement.RestY >= area.WorkY, $"top edge outside: {where}");
                        Assert.IsTrue(placement.RestY + placement.Height <= area.WorkY + area.WorkHeight,
                            $"bottom edge outside: {where}");
                        Assert.IsTrue(placement.Width > 0 && placement.Height > 0, $"empty rect: {where}");
                    }
                }
            }
        }
    }

    [TestMethod]
    public void Compute_IsHorizontallyCentredWithinOnePixel()
    {
        foreach (int dpi in new[] { 96, 120, 144, 192 })
        {
            foreach (int width in new[] { 1920, 2560, 3840 })
            {
                var area = Display(width, 1000, dpi);
                var placement = ModeOsdPlacement.Compute(0d, area);
                int leftGap = placement.X - area.WorkX;
                int rightGap = (area.WorkX + area.WorkWidth) - (placement.X + placement.Width);
                Assert.IsTrue(Math.Abs(leftGap - rightGap) <= 1,
                    $"{width}@{dpi}: left {leftGap} vs right {rightGap}");
            }
        }
    }

    /// <summary>
    /// The regression itself, expressed as numbers: a 1440p rest position is not valid on a
    /// 1080p work area. Recomputing (what the fix does) must move the card up.
    /// </summary>
    [TestMethod]
    public void Compute_RecomputingAfterASmallerModeMovesTheCardBackOnScreen()
    {
        var before = ModeOsdPlacement.Compute(0d, Display(2560, 1440, 96));
        var after = ModeOsdPlacement.Compute(0d, Display(1920, 1040, 96));

        // The stale rest position would have put the card's bottom at 1440 - 24 = 1416 px,
        // i.e. completely below a 1040 px work area.
        Assert.IsTrue(before.RestY + before.Height > 1040,
            "expected the 1440p rest position to be off a 1080p work area");
        Assert.AreEqual(968, after.RestY);
        Assert.IsTrue(after.RestY + after.Height <= 1040);
    }

    [TestMethod]
    public void Compute_KeepsTheCardHeightWhenOnlyTheDpiChanges()
    {
        var at100 = ModeOsdPlacement.Compute(0d, Display(1920, 1040, 96));
        var at150 = ModeOsdPlacement.Compute(0d, Display(1920, 1040, 144));

        // Same DIP, different px: the card stays 48 DIP tall at every scale.
        Assert.AreEqual(48, at100.Height);
        Assert.AreEqual(72, at150.Height);
        Assert.AreEqual(48d, at100.Height * 96.0 / 96);
        Assert.AreEqual(48d, at150.Height * 96.0 / 144);
    }
}
