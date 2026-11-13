// The title-bar mark is painted in the user's accent colour, and the accent is user data: it can
// be #000000 or #FFFFFF. BrandMarkTint is the rule that decides when that would make the mark
// invisible on the Mica title bar and how far to walk the tint back into view. It is pure math
// (no WinUI, no dispatcher), so the rule is pinned here instead of being judged on a screenshot
// from one machine in one theme.

using Predex.WinUI.Shell;

namespace Performix.Tests;

[TestClass]
public sealed class BrandMarkTintTests
{
    private static readonly BrandMarkTint.Rgb DefaultAccent = new(0x3B, 0x82, 0xF6);

    [TestMethod]
    public void DefaultAccent_IsLeftAlone_InBothThemes()
    {
        Assert.AreEqual(DefaultAccent, BrandMarkTint.EnsureContrast(DefaultAccent, lightSurface: false));
        Assert.AreEqual(DefaultAccent, BrandMarkTint.EnsureContrast(DefaultAccent, lightSurface: true));
    }

    [TestMethod]
    public void BlackAccent_OnDarkTitleBar_IsLiftedIntoView()
    {
        BrandMarkTint.Rgb result = BrandMarkTint.EnsureContrast(new BrandMarkTint.Rgb(0, 0, 0), lightSurface: false);

        Assert.AreNotEqual(new BrandMarkTint.Rgb(0, 0, 0), result, "a black mark on #2B2B2B would be invisible");
        Assert.IsTrue(
            BrandMarkTint.Contrast(BrandMarkTint.RelativeLuminance(result), BrandMarkTint.DarkSurfaceLuminance)
                >= BrandMarkTint.MinContrastRatio,
            $"{result} must clear {BrandMarkTint.MinContrastRatio}:1 on the dark title bar");
    }

    [TestMethod]
    public void WhiteAccent_OnLightTitleBar_IsDarkenedIntoView()
    {
        BrandMarkTint.Rgb result = BrandMarkTint.EnsureContrast(new BrandMarkTint.Rgb(255, 255, 255), lightSurface: true);

        Assert.AreNotEqual(new BrandMarkTint.Rgb(255, 255, 255), result, "a white mark on a white title bar would be invisible");
        Assert.IsTrue(
            BrandMarkTint.Contrast(BrandMarkTint.RelativeLuminance(result), BrandMarkTint.LightSurfaceLuminance)
                >= BrandMarkTint.MinContrastRatio,
            $"{result} must clear {BrandMarkTint.MinContrastRatio}:1 on the light title bar");
    }

    [TestMethod]
    public void ReadableAccents_AreNotAdjusted()
    {
        BrandMarkTint.Rgb red = new(0xFF, 0x40, 0x40);   // the app's Brand token, 4.1:1 on dark Mica
        BrandMarkTint.Rgb green = new(0x1A, 0x7F, 0x37); // 5.1:1 on the light title bar

        Assert.AreEqual(red, BrandMarkTint.EnsureContrast(red, lightSurface: false));
        Assert.AreEqual(green, BrandMarkTint.EnsureContrast(green, lightSurface: true));
    }

    [TestMethod]
    public void AccentThatIsOnlyBorderlineReadable_IsStillAdjusted()
    {
        // #2ECC71 (the Eco token) is 2.1:1 on white: readable enough inside a labelled card, but not
        // as a 16x16 glyph on the title bar. The rule has to fire here too, not only for the extremes.
        BrandMarkTint.Rgb eco = new(0x2E, 0xCC, 0x71);
        BrandMarkTint.Rgb adjusted = BrandMarkTint.EnsureContrast(eco, lightSurface: true);

        Assert.AreNotEqual(eco, adjusted, "#2ECC71 is below 3:1 on white");
        Assert.IsTrue(
            BrandMarkTint.Contrast(BrandMarkTint.RelativeLuminance(adjusted), BrandMarkTint.LightSurfaceLuminance)
                >= BrandMarkTint.MinContrastRatio);
    }

    [TestMethod]
    public void EveryAccent_EndsReadable_InBothThemes()
    {
        // Coarse sRGB grid: the extremes are the interesting cases, the grid proves the walk always
        // terminates at a readable colour rather than at "close enough".
        for (int r = 0; r <= 255; r += 51)
        {
            for (int g = 0; g <= 255; g += 51)
            {
                for (int b = 0; b <= 255; b += 51)
                {
                    var accent = new BrandMarkTint.Rgb((byte)r, (byte)g, (byte)b);

                    foreach (bool light in new[] { false, true })
                    {
                        BrandMarkTint.Rgb tint = BrandMarkTint.EnsureContrast(accent, light);
                        double surface = light
                            ? BrandMarkTint.LightSurfaceLuminance
                            : BrandMarkTint.DarkSurfaceLuminance;
                        double ratio = BrandMarkTint.Contrast(
                            BrandMarkTint.RelativeLuminance(tint), surface);

                        Assert.IsTrue(
                            ratio >= BrandMarkTint.MinContrastRatio,
                            $"{{{r},{g},{b}}} on light={light} became {tint} at {ratio:F2}:1");
                    }
                }
            }
        }
    }

    [TestMethod]
    public void AdjustedTint_KeepsTheAccentHueRatherThanJumpingToTheExtreme()
    {
        // #000080 (navy) is unreadable on the dark title bar; walking to pure white would satisfy the
        // contrast rule but throw the user's colour away. The result must still be recognisably blue.
        BrandMarkTint.Rgb navy = new(0x00, 0x00, 0x80);
        BrandMarkTint.Rgb result = BrandMarkTint.EnsureContrast(navy, lightSurface: false);

        Assert.IsTrue(result.B >= result.R, $"{result} should still read as blue");
        Assert.IsTrue(result.B >= result.G, $"{result} should still read as blue");
        Assert.IsTrue(result.B > navy.B, "the tint is lifted, not flattened to white");
    }

    [TestMethod]
    public void ContrastRatio_MatchesTheWcagReferenceValues()
    {
        double black = BrandMarkTint.RelativeLuminance(new BrandMarkTint.Rgb(0, 0, 0));
        double white = BrandMarkTint.RelativeLuminance(new BrandMarkTint.Rgb(255, 255, 255));

        Assert.AreEqual(0.0, black, 1e-9);
        Assert.AreEqual(1.0, white, 1e-9);
        Assert.AreEqual(21.0, BrandMarkTint.Contrast(black, white), 1e-9);
    }
}
