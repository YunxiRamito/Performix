// Static helpers called via x:Bind function syntax in MainWindow.xaml.
// WinUI 3 supports: {x:Bind local:Converters.BoolToVis(ViewModel.SomeBool), Mode=OneWay}

using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Predex.WinUI
{
    public static class Converters
    {
        // ── Telemetry placeholder ────────────────────────────────────────────────
        /// <summary>
        /// The ViewModel reports "N/A" when a reading is missing. The design rules want an
        /// em dash instead of a latin token (and no empty row): "—".
        /// </summary>
        public static string Telemetry(string? value) =>
            string.IsNullOrWhiteSpace(value) || value == "N/A" ? "—" : value;

        // ── Visibility ───────────────────────────────────────────────────────────
        public static Visibility BoolToVis(bool value) =>
            value ? Visibility.Visible : Visibility.Collapsed;

        public static Visibility InvertBoolToVis(bool value) =>
            value ? Visibility.Collapsed : Visibility.Visible;

        // ── SolidColorBrush from active/inactive hex strings ─────────────────────
        // Used for the display AUTO chip border/background color.
        public static SolidColorBrush BoolToBrush(bool active, string activeHex, string inactiveHex)
        {
            string hex = active ? activeHex : inactiveHex;
            return HexToBrush(hex);
        }

        // ── Style, by resource key (theme / accent aware) ────────────────────────
        /// <summary>
        /// Returns the Style stored under one of two keys, e.g. selected / idle tile styles.
        /// The styles carry the colours as {ThemeResource} setters, so they resolve against
        /// the *element's* theme (dark / light / high contrast) and re-resolve on a theme
        /// change. Returning a brush here would not work: a static converter has no element,
        /// so an Application-level ThemeResource lookup would ignore the page's RequestedTheme.
        /// </summary>
        public static Style? BoolToStyle(bool active, string trueKey, string falseKey)
        {
            string key = active ? trueKey : falseKey;
            try
            {
                if (Application.Current?.Resources.TryGetValue(key, out object? resource) == true &&
                    resource is Style style)
                {
                    return style;
                }
            }
            catch { /* fall through to no style */ }
            return null;
        }

        private static SolidColorBrush HexToBrush(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return new SolidColorBrush(Colors.Transparent);

            if (hex.Equals("Transparent", StringComparison.OrdinalIgnoreCase))
                return new SolidColorBrush(Colors.Transparent);

            hex = hex.TrimStart('#');
            if (hex.Length == 6) hex = "FF" + hex;
            if (hex.Length != 8) return new SolidColorBrush(Colors.Transparent);

            byte a = System.Convert.ToByte(hex[0..2], 16);
            byte r = System.Convert.ToByte(hex[2..4], 16);
            byte g = System.Convert.ToByte(hex[4..6], 16);
            byte b = System.Convert.ToByte(hex[6..8], 16);
            return new SolidColorBrush(Color.FromArgb(a, r, g, b));
        }

        // ── Temperature color (green → yellow → red) ────────────────────────────
        public static SolidColorBrush TempToForeground(string tempText)        {
            // Parse "55°C" → 55
            int i = tempText.IndexOf('°');
            if (i <= 0 || !int.TryParse(tempText[..i], out int t))
                return new SolidColorBrush(Color.FromArgb(0xFF, 0x98, 0x98, 0xB0));

            if (t >= 90) return new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x40, 0x40));
            if (t >= 75) return new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xBB, 0x33));
            return new SolidColorBrush(Color.FromArgb(0xFF, 0x98, 0x98, 0xB0));
        }
    }
}
