// Title-bar brand mark, tinted with the user's accent colour.
//
// Why Composition and not a second PNG per accent: the mark has to follow the accent live (the
// appearance page applies every click immediately, AppearanceService.SetAccent), and it has to
// keep its 16×16 anti-aliased edges. Rendering a tinted bitmap per accent means either disk I/O on
// a settings slider or an image-swap flicker, and XAML has no OpacityMask on Image in WinUI 3.
//
// The recipe is WinUI Composition only — no Win2D, no new package:
//
//     CompositionMaskBrush
//         Source = CompositionColorBrush(accent)      ← repainted in place when the accent changes
//         Mask   = CompositionSurfaceBrush(performix-mark-mono.png)  ← white glyph, transparent bg
//
// A mask brush multiplies the source by the mask's *alpha*, so the glyph's anti-aliased edges come
// through as partially transparent accent pixels instead of a hard stair-step, and nothing is ever
// rasterised on the CPU. The colour brush is a live Composition property: setting .Color on an
// accent change repaints the compositor visual without touching the XAML tree, rebuilding the
// surface, or reloading the image.
//
// Readability: the tint is passed through BrandMarkTint so a near-black / near-white custom accent
// still clears 3:1 against the Mica surface of the current theme. The accent reaches this brush
// through AppearanceService.AccentChanged (live, including the colour-picker drag); the light/dark
// answer reaches it through the host element's ActualThemeChanged → RefreshTint(), because
// "follow system" changes the surface without raising AppearanceService.ThemeChanged.

using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Predex.WinUI.Services;
using System;
using Windows.UI;

namespace Predex.WinUI.Shell
{
    /// <summary>
    /// Paints the white-on-transparent Performix glyph in the current accent colour.
    /// Set it as the <c>Fill</c> of a 16×16 shape; the glyph is stretched to the element bounds.
    /// </summary>
    public sealed class AccentGlyphBrush : XamlCompositionBrushBase
    {
        /// <summary>Pure-white glyph with a transparent background, 64×64.</summary>
        public static readonly Uri DefaultGlyph = new("ms-appx:///Assets/performix-mark-mono.png");

        public static readonly DependencyProperty GlyphSourceProperty = DependencyProperty.Register(
            nameof(GlyphSource),
            typeof(Uri),
            typeof(AccentGlyphBrush),
            new PropertyMetadata(DefaultGlyph, OnGlyphSourceChanged));

        private CompositionMaskBrush? _mask;
        private CompositionColorBrush? _tint;
        private LoadedImageSurface? _glyph;
        private bool _hooked;

        public AccentGlyphBrush()
        {
            // Last-resort paint if the compositor refuses the brush (never seen, but a title bar
            // without a mark is worse than a plain accent chip).
            FallbackColor = Color.FromArgb(0xFF, 0x3B, 0x82, 0xF6);
        }

        /// <summary>The white-on-transparent glyph to tint. Defaults to performix-mark-mono.png.</summary>
        public Uri GlyphSource
        {
            get => (Uri)GetValue(GlyphSourceProperty);
            set => SetValue(GlyphSourceProperty, value);
        }

        // ── Composition wiring ───────────────────────────────────────────────────
        protected override void OnConnected()
        {
            try
            {
                Compositor compositor = CompositionTarget.GetCompositorForCurrentThread();

                // Decode once at the asset's own 64×64: the compositor downsamples it to 16 DIP with
                // linear filtering, which keeps the stroke edges smooth at 100 % and stays sharp at
                // 150 % / 200 % display scaling because the source is 4× the layout size.
                _glyph = LoadedImageSurface.StartLoadFromUri(GlyphSource ?? DefaultGlyph);
                CompositionSurfaceBrush glyph = compositor.CreateSurfaceBrush(_glyph);
                glyph.BitmapInterpolationMode = CompositionBitmapInterpolationMode.Linear;
                glyph.Stretch = CompositionStretch.Uniform;

                Color tint = CurrentTint();
                _tint = compositor.CreateColorBrush(tint);
                _mask = compositor.CreateMaskBrush();
                _mask.Source = _tint;
                _mask.Mask = glyph;

                CompositionBrush = _mask;
                FallbackColor = tint;

                ShellLog.Write("appearance",
                    $"brand mark tinted #{tint.R:X2}{tint.G:X2}{tint.B:X2} " +
                    $"accent=#{AppearanceService.AccentColor.R:X2}{AppearanceService.AccentColor.G:X2}" +
                    $"{AppearanceService.AccentColor.B:X2} light={AppearanceService.IsLightTheme} " +
                    $"glyph={GlyphSource}");

                // LoadCompleted is the only proof the asset actually decoded; see shell.log.
                _glyph.LoadCompleted += (_, _) => ShellLog.Write("appearance",
                    $"brand mark glyph decoded from {GlyphSource}");

                Hook();
            }
            catch (Exception ex)
            {
                ShellLog.Write("appearance", $"brand mark tint failed, using flat fallback: {ex.Message}");
                try { FallbackColor = CurrentTint(); } catch { }
            }
        }

        protected override void OnDisconnected()
        {
            Unhook();

            // Dispose after the tree lets go of the brush; leaving the surfaces alive would keep the
            // decoded bitmap around for the lifetime of the process.
            _mask?.Dispose();
            _tint?.Dispose();
            _glyph?.Dispose();
            _mask = null;
            _tint = null;
            _glyph = null;

            CompositionBrush = null;
        }

        private static void OnGlyphSourceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        {
            if (sender is AccentGlyphBrush brush && brush.CompositionBrush is not null)
            {
                // Already on screen: rebuild against the new asset. Before connection this is a
                // no-op — OnConnected reads the property itself.
                brush.OnDisconnected();
                brush.OnConnected();
            }
        }

        // ── Live accent / theme ──────────────────────────────────────────────────
        private void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            AppearanceService.AccentChanged += OnAppearanceChanged;
            // The theme is covered by the host element's ActualThemeChanged (ShellWindow), not here:
            // "follow system" never raises AppearanceService.ThemeChanged — the app's own theme does
            // not change there, the OS surface under it does — and the element is the only place
            // that notification exists.
        }

        private void Unhook()
        {
            if (!_hooked) return;
            _hooked = false;
            AppearanceService.AccentChanged -= OnAppearanceChanged;
        }

        /// <summary>
        /// Recomputes the tint against the surface the mark is currently drawn on. Called by the
        /// shell when the element's effective theme changes (light/dark, including "follow system").
        /// </summary>
        public void RefreshTint() => OnAppearanceChanged(this, EventArgs.Empty);

        private void OnAppearanceChanged(object? sender, EventArgs args)
        {
            try
            {
                if (_tint is null) return;
                // Live composition property: one write repaints the mark, no XAML invalidation and
                // no surface reload, so dragging the custom-colour slider stays smooth.
                Color tint = CurrentTint();
                if (tint == _tint.Color) return;
                _tint.Color = tint;
                FallbackColor = tint;
                ShellLog.Write("appearance", $"brand mark repainted #{tint.R:X2}{tint.G:X2}{tint.B:X2} " +
                    $"(accent #{AppearanceService.AccentColor.R:X2}{AppearanceService.AccentColor.G:X2}" +
                    $"{AppearanceService.AccentColor.B:X2}, light={AppearanceService.IsLightTheme})");
            }
            catch (Exception ex)
            {
                ShellLog.Write("appearance", $"brand mark tint update failed: {ex.Message}");
            }
        }

        private static Color CurrentTint()
        {
            Color accent = AppearanceService.AccentColor;
            BrandMarkTint.Rgb readable = BrandMarkTint.EnsureContrast(
                new BrandMarkTint.Rgb(accent.R, accent.G, accent.B),
                lightSurface: AppearanceService.IsLightTheme);
            return Color.FromArgb(0xFF, readable.R, readable.G, readable.B);
        }
    }
}
