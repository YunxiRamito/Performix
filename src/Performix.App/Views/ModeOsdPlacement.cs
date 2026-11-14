// Placement maths for the mode-switch overlay (TOAST-ANIMATION-SPEC §1, docs\R6-OSD.md).
//
// WHY THIS IS A SEPARATE, DEPENDENCY-FREE FILE
// The overlay window is created once and reused for the whole process (spec §3), but the
// display under it is not fixed: a resolution or refresh-rate switch, a DPI change or a
// monitor hot-plug all move the work area and/or change the DIP→px scale. The numbers that
// decide where the card lands therefore have to be recomputed from live values before every
// show, and that arithmetic has to be checkable without a WinUI host — hence: no WinUI types,
// no HWND, no static state, only plain numbers in and out. tests\Performix.Tests\
// ModeOsdPlacementTests.cs compiles this file in and covers the resolution × DPI matrix.
//
// Spec §1 geometry, all in DIP:
//   • card height fixed at 48;
//   • card width content-driven, clamped to 160–280 (16 DIP padding each side + 20 icon + 12 gap);
//   • horizontally centred on the primary display's work area;
//   • card bottom 24 DIP above the work-area bottom.
//
// ONE DELIBERATE ADDITION TO THE SPEC: the result is clamped into the work area. A work area can
// be smaller than the card (a low resolution, a tall taskbar, a large text scale), and the plain
// arithmetic happily produced a Y below the screen — the card was then clipped by the desktop
// edge instead of being shown. The bottom margin now gives way first and only then the card, so
// "48 DIP card, 24 DIP above the bottom" still holds whenever there is room for it.

using System;

namespace Predex.WinUI.Views
{
    /// <summary>
    /// The primary display as the overlay sees it: work area in virtual-screen pixels plus the
    /// effective DPI of that same monitor. Both values come from one monitor handle, so they can
    /// never describe two different displays.
    /// </summary>
    internal readonly record struct OsdDisplay(int WorkX, int WorkY, int WorkWidth, int WorkHeight, int Dpi)
    {
        public bool IsUsable => WorkWidth > 0 && WorkHeight > 0;

        public string Describe() =>
            $"workArea=({WorkX},{WorkY} {WorkWidth}x{WorkHeight}) dpi={Dpi}";
    }

    /// <summary>The resting window rect (physical px) the overlay animates around.</summary>
    internal readonly record struct OsdPlacement(int X, int RestY, int Width, int Height)
    {
        public string Describe() => $"{Width}x{Height} px at ({X},{RestY})";
    }

    internal static class ModeOsdPlacement
    {
        /// <summary>Spec §1: fixed card height.</summary>
        public const double CardHeightDip = 48;

        /// <summary>Spec §1: card width is content-driven between these two bounds.</summary>
        public const double CardMinWidthDip = 160;
        public const double CardMaxWidthDip = 280;

        /// <summary>Spec §1: 16 DIP padding on each side of the content.</summary>
        public const double CardPaddingDip = 32;

        /// <summary>Spec §1: icon 20×20 plus the 12 DIP gap to the text.</summary>
        public const double IconAndGapDip = 32;

        /// <summary>Spec §1: card sits 24 DIP above the work-area bottom.</summary>
        public const double BottomMarginDip = 24;

        /// <summary>DPI the DIP grid is defined against; also the floor for a failed query.</summary>
        public const int BaseDpi = 96;

        /// <summary>
        /// DIP → physical px for the given monitor DPI. A DPI below 96 (a failed
        /// <c>GetDpiForMonitor</c> reports 0) falls back to 96 rather than shrinking the card.
        /// </summary>
        public static int ScaleDip(double dip, int dpi)
        {
            int effective = dpi >= BaseDpi ? dpi : BaseDpi;
            return (int)Math.Round(dip * effective / BaseDpi);
        }

        /// <summary>Spec §1: 32 padding + 32 icon/gap + the measured text, clamped to 160–280 DIP.</summary>
        public static double CardWidthDip(double textWidthDip)
        {
            if (double.IsNaN(textWidthDip) || double.IsInfinity(textWidthDip) || textWidthDip < 0)
                textWidthDip = 0;
            return Math.Clamp(CardPaddingDip + IconAndGapDip + textWidthDip, CardMinWidthDip, CardMaxWidthDip);
        }

        /// <summary>
        /// Resting rect for a card with the given text width on the given display. Pure: the same
        /// inputs always give the same rect, which is what makes the resolution × DPI matrix
        /// testable and what makes the diagnostic trace line reproducible.
        /// </summary>
        public static OsdPlacement Compute(double textWidthDip, OsdDisplay display)
        {
            int width = ScaleDip(CardWidthDip(textWidthDip), display.Dpi);
            int height = ScaleDip(CardHeightDip, display.Dpi);
            int margin = ScaleDip(BottomMarginDip, display.Dpi);

            int areaWidth = Math.Max(display.WorkWidth, 1);
            int areaHeight = Math.Max(display.WorkHeight, 1);

            // Never hand the window a rect that hangs off the work area: clamp the card to it,
            // and let the 24 DIP bottom margin give way before the card does.
            width = Math.Clamp(width, 1, areaWidth);
            height = Math.Clamp(height, 1, areaHeight);
            margin = Math.Clamp(margin, 0, Math.Max(0, areaHeight - height));

            int x = display.WorkX + ((areaWidth - width) / 2);
            int y = display.WorkY + areaHeight - margin - height;
            return new OsdPlacement(x, y, width, height);
        }
    }
}
