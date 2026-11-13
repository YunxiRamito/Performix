// Contrast math for the title-bar brand mark. Pure: no WinUI, no Color struct, no dispatcher —
// the test project compiles this file directly (same technique as Shell\ShellAnimationGuards.cs)
// so "is this accent readable on the title bar?" is decided by a test, not by eyeballing a
// screenshot on one machine.
//
// Why this exists at all: the mark is now tinted with the user's accent (Shell\AccentGlyphBrush.cs),
// and an accent is user data — %LOCALAPPDATA%\Predex\appearance.json can hold #000000 or #FFFFFF.
// The title bar sits on the Mica surface, which behaves like #FFFFFF in the light theme and like
// about #2B2B2B in the dark one, so a near-black accent on dark Mica (or a near-white one on light
// Mica) would paint an invisible mark. The rules below keep at least a 3:1 contrast ratio — the
// WCAG minimum for non-text graphics — by walking the tint toward the pole that raises contrast,
// and by leaving every readable accent untouched (the #3B82F6 default included).

using System;

namespace Predex.WinUI.Shell
{
    public static class BrandMarkTint
    {
        /// <summary>WCAG 1.4.11 minimum for graphical objects.</summary>
        public const double MinContrastRatio = 3.0;

        /// <summary>
        /// Relative luminance of the dark title-bar surface: Mica BaseAlt reads as roughly #2B2B2B,
        /// so the mark is judged against that, not against pure black (pure black would demand less
        /// adjustment than the real backdrop allows).
        /// </summary>
        public const double DarkSurfaceLuminance = 0.0242;   // #2B2B2B

        /// <summary>Relative luminance of the light title-bar surface (#FFFFFF).</summary>
        public const double LightSurfaceLuminance = 1.0;

        public readonly struct Rgb : IEquatable<Rgb>
        {
            public Rgb(byte r, byte g, byte b)
            {
                R = r;
                G = g;
                B = b;
            }

            public byte R { get; }
            public byte G { get; }
            public byte B { get; }

            public bool Equals(Rgb other) => R == other.R && G == other.G && B == other.B;
            public override bool Equals(object? obj) => obj is Rgb other && Equals(other);
            public override int GetHashCode() => (R << 16) | (G << 8) | B;
            public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
        }

        /// <summary>
        /// Returns <paramref name="color"/> when it already reads on the title bar, otherwise the
        /// nearest tint walking toward white (dark surface) or black (light surface) that reaches
        /// <see cref="MinContrastRatio"/>. Alpha is not part of the decision: the glyph carries its
        /// own per-pixel alpha and the tint is painted through it.
        /// </summary>
        public static Rgb EnsureContrast(Rgb color, bool lightSurface)
        {
            double surface = lightSurface ? LightSurfaceLuminance : DarkSurfaceLuminance;
            if (Contrast(RelativeLuminance(color), surface) >= MinContrastRatio) return color;

            // Blend toward the pole in 5 % steps and stop at the first readable mix: a monotone walk
            // cannot overshoot into "too extreme", and the accent hue is kept as long as possible
            // (a #101010 accent becomes mid grey, not white).
            const int Steps = 20;
            for (int step = 1; step <= Steps; step++)
            {
                double amount = (double)step / Steps;
                Rgb candidate = lightSurface
                    ? Blend(color, new Rgb(0, 0, 0), amount)
                    : Blend(color, new Rgb(255, 255, 255), amount);
                if (Contrast(RelativeLuminance(candidate), surface) >= MinContrastRatio) return candidate;
            }

            return lightSurface ? new Rgb(0, 0, 0) : new Rgb(255, 255, 255);
        }

        /// <summary>WCAG contrast ratio between two relative luminances (1..21).</summary>
        public static double Contrast(double foreground, double background)
        {
            double lighter = Math.Max(foreground, background);
            double darker = Math.Min(foreground, background);
            return (lighter + 0.05) / (darker + 0.05);
        }

        /// <summary>WCAG relative luminance of an sRGB colour (0 = black, 1 = white).</summary>
        public static double RelativeLuminance(Rgb color) =>
            (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));

        private static double Channel(byte value)
        {
            double c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        private static Rgb Blend(Rgb from, Rgb to, double amount)
        {
            amount = Math.Clamp(amount, 0, 1);
            byte Mix(byte a, byte b) => (byte)Math.Clamp(Math.Round(a + ((b - a) * amount)), 0, 255);
            return new Rgb(Mix(from.R, to.R), Mix(from.G, to.G), Mix(from.B, to.B));
        }
    }
}
