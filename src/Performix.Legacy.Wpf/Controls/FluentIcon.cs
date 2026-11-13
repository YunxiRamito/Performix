using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PreySense.Frontend.Controls;

/// <summary>
/// Segoe Fluent Icons glyph rendered as text. One control so no window has to hand-write
/// <c>FontFamily="Segoe Fluent Icons"</c>, and so glyph sizes/colors stay theme-driven.
/// </summary>
public sealed class FluentIcon : TextBlock
{
    public const string IconFontFamilyName = "Segoe Fluent Icons";
    private static readonly FontFamily IconFontFamily = new(IconFontFamilyName);

    public FluentIcon()
    {
        FontFamily = IconFontFamily;
        // Icon glyphs are text, not content: keep them out of the focus/tab order and
        // out of screen-reader text. Callers describe the meaning on the owning control.
        Focusable = false;
        IsHitTestVisible = false;
        TextTrimming = TextTrimming.None;
        TextWrapping = TextWrapping.NoWrap;
        SnapsToDevicePixels = true;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
    }

    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(
            nameof(Glyph),
            typeof(string),
            typeof(FluentIcon),
            new PropertyMetadata(string.Empty, OnGlyphChanged));

    /// <summary>Single character from <see cref="FluentGlyphs"/>.</summary>
    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    /// <summary>Glyph size in DIP; maps to <see cref="TextBlock.FontSize"/>.</summary>
    public static readonly DependencyProperty SizeProperty =
        DependencyProperty.Register(
            nameof(Size),
            typeof(double),
            typeof(FluentIcon),
            new PropertyMetadata(14.0, OnSizeChanged));

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    private static void OnGlyphChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((FluentIcon)d).Text = e.NewValue as string ?? string.Empty;

    private static void OnSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((FluentIcon)d).FontSize = e.NewValue is double size && size > 0 ? size : 14.0;
}

/// <summary>
/// Segoe Fluent Icons code points used by the app. Every entry was checked against the
/// rendered glyph sheet in <c>docs/glyph-sheet.png</c>; do not add a code point from memory.
/// </summary>
public static class FluentGlyphs
{
    // Caption bar
    public const string Minimize = "\uE921";
    public const string Maximize = "\uE922";
    public const string Restore = "\uE923";
    public const string Close = "\uE8BB";

    // Controls
    public const string CheckMark = "\uE73E";
    public const string Dash = "\uE921";
    public const string Add = "\uE710";
    public const string ChevronDown = "\uE96E";
    public const string ChevronLeft = "\uE96F";
    public const string ChevronRight = "\uE970";
    public const string Info = "\uE946";
    public const string Warning = "\uE7BA";
    public const string Exclamation = "\uE783";
    public const string Settings = "\uE713";
    public const string Translate = "\uE775";
    public const string Palette = "\uE790";
    public const string Brightness = "\uE9A9";

    // Sections and pages
    public const string PerformanceMode = "\uE7E8";
    public const string Power = "\uE7E8";
    public const string Eco = "\uE8BE";
    public const string Quiet = "\uE708";
    public const string Balanced = "\uE9E9";
    public const string Performance = "\uE945";
    public const string Turbo = "\uE9E3";
    public const string Gpu = "\uE950";
    public const string Cpu = "\uE950";
    public const string HybridGpu = "\uE977";
    public const string DiscreteGpu = "\uE912";
    public const string AutoGpu = "\uE7E8";
    public const string AdvancedGpu = "\uE895";
    public const string Display = "\uE7F4";
    public const string Overlay = "\uE7C4";
    public const string Lighting = "\uE9A9";
    public const string Tuning = "\uE9F5";
    public const string Fans = "\uE9D9";
    public const string Keyboard = "\uE92E";
    public const string Thermal = "\uE9CA";

    // Settings rows
    public const string Startup = "\uE7E8";
    public const string Services = "\uE9F5";
    public const string Battery = "\uE85B";
    public const string Usb = "\uE851";
    public const string Language = "\uE775";
    public const string Theme = "\uE790";

    // Actions and status
    public const string Reset = "\uE895";
    public const string Refresh = "\uE895";
    public const string Undo = "\uE777";
    public const string Save = "\uE74E";
    public const string Document = "\uE8A5";
    public const string Folder = "\uE887";
    public const string Report = "\uE9F9";
    public const string Person = "\uE8F8";
    public const string Check = "\uE73E";
}
