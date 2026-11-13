// Segoe Fluent Icons code points used by the WinUI shell.
//
// Provenance: copied from the in-production WPF table
// app\Fork\PreySense.Frontend\Controls\FluentIcon.cs (class FluentGlyphs), which was
// itself verified against docs\glyph-sheet.png. Only the entries the shell actually uses
// are carried over — do not add a code point from memory.
//
// The four caption-bar glyphs were additionally re-checked for S1 by rendering them at
// 48 px from C:\Windows\Fonts\SegoeIcons.ttf and looking at docs\s1-glyphs.png:
//   E921 = dash (minimize), E922 = square (maximize), E923 = two squares (restore),
//   E8BB = X (close), E80F = house (home).

namespace Predex.WinUI
{
    internal static class Glyphs
    {
        // Caption bar
        public const string Minimize = "\uE921";
        public const string Maximize = "\uE922";
        public const string Restore = "\uE923";
        public const string Close = "\uE8BB";

        // Chevrons (expand / collapse)
        public const string ChevronDown = "\uE70D";
        public const string ChevronUp = "\uE70E";

        // Navigation / sections
        public const string Home = "\uE80F";
        public const string Settings = "\uE713";
        public const string PerformanceMode = "\uE7E8";
        public const string Gpu = "\uE950";
        public const string Display = "\uE7F4";
        public const string Lighting = "\uE9A9";
        public const string Overlay = "\uE7C4";
        public const string Fans = "\uE9D9";
        public const string Tuning = "\uE9F5";

        // Performance modes
        public const string Eco = "\uE8BE";
        public const string Quiet = "\uE708";
        public const string Balanced = "\uE9E9";
        public const string Performance = "\uE945";
        public const string Turbo = "\uE9E3";

        // The five glyphs the Home mode tiles actually draw (Pages\HomePage.xaml:82/97/112/127/142,
        // rendered and checked in S1). Two of them are *not* the constants above: the Performance
        // tile draws EC4A and the Beast tile draws the lightning E945, which is also the point
        // TOAST-ANIMATION-SPEC §1 names for 狂暴. The mode-switch OSD uses this exact set so the
        // overlay and the selected tile always show the same icon.
        public const string PerformanceTile = "\uEC4A";
        public const string Beast = "\uE945";

        // GPU modes
        public const string AutoGpu = "\uE7E8";
        public const string HybridGpu = "\uE977";
        public const string AdvancedGpu = "\uE895";
        public const string DiscreteGpu = "\uE912";
    }
}
