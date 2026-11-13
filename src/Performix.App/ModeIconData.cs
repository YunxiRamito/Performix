// Mode icon geometry strings (SVG path mini-language).
// These work directly as Path.Data in both WPF and WinUI 3 XAML,
// so they're exposed as plain string constants — no Geometry.Parse needed.

namespace Predex.WinUI
{
    public static class ModeIconData
    {
        public const string Eco         = "M19 3C11 3 5 7 5 13C5 17 8 20 12 20C18 20 21 12 21 3C20 5 16 5 12 6C7 7 4 11 4 15C4 17 5 19 7 20C8 13 12 8 19 3Z";
        public const string Quiet       = "M20 15C18 16 16 17 14 17C9 17 6 13 6 8C6 6 7 4 8 2C4 4 2 8 2 12C2 18 7 22 13 22C17 22 20 19 22 16C21 16 21 15 20 15Z";
        public const string Balanced    = "M3 5H14V7H3ZM18 3H20V9H18ZM3 11H8V13H3ZM12 9H14V15H12ZM3 17H16V19H3ZM20 15H22V21H20Z";
        public const string Performance = "M13 2L3 14H11L10 22L21 9H13L13 2Z";
        public const string Turbo       = "M13 2C14 7 20 9 20 15C20 20 16 22 12 22C7 22 4 19 4 15C4 11 7 8 10 5C10 8 11 10 13 11C15 9 15 6 13 2ZM12 13C10 15 9 16 9 18C9 20 10 21 12 21C14 21 16 20 16 18C16 16 14 14 12 13Z";
        public const string HybridGpu   = "M3 4H21V17H3ZM1 20H23M8 9H17M14 6L17 9L14 12M16 13H7M10 10L7 13L10 16";

        // Icons for footer nav buttons (simple line-art, 24-grid).
        public const string Lighting    = "M12 2C13 2 14 3 14 4V5C16 6 17 8 17 10C17 13 15 15 12 15C9 15 7 13 7 10C7 8 8 6 10 5V4C10 3 11 2 12 2M12 17V22M9 22H15";
        public const string Overlay      = "M3 3H21V21H3ZM3 9H21M9 9V21";
        public const string Fan          = "M12 2C12 6 12 6 12 12C12 12 6 12 6 12C6 16 9 18 12 18C12 14 12 14 12 12C12 12 18 12 18 12C18 8 15 6 12 6Z";
        public const string Tuning       = "M12 8C14 8 16 10 16 12C16 14 14 16 12 16C10 16 8 14 8 12C8 10 10 8 12 8M12 2V5M12 19V22M2 12H5M19 12H22M5 5L7 7M17 17L19 19M5 19L7 17M17 7L19 5";
        public const string Settings     = "M19 14V18C19 19 18 20 17 20H7C6 20 5 19 5 18V14M12 3V15M12 3L7 8M12 3L17 8";
        public const string Quit         = "M9 21H5C4 21 3 20 3 19V5C3 4 4 3 5 3H9M16 17L21 12L16 7M21 12H9";
    }
}
