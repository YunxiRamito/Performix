// Theme page code-behind (theme-page task).
//
// The page is a thin view over Predex.WinUI.Services.AppearanceService: every selection is
// applied immediately (no Apply button), logged to shell.log by the service, and persisted
// to %LOCALAPPDATA%\Predex\appearance.json. Nothing here touches hardware or preysense.json.
//
// State → UI is pulled on Loaded and after every change (SyncFromState + Bindings.Update),
// because the tile colours are x:Bind *style* bindings over page properties:
//   Style = Converters.BoolToStyle(IsDarkSelected, 'ModeTileButtonSelectedStyle', 'ModeTileButtonStyle')
// The styles carry the colours as {ThemeResource} setters, so they resolve in the element's
// own dark / light / high-contrast scope (a brush-returning converter could not do that).
//
// The material entries that the OS cannot draw are disabled, never removed (requirement:
// grey out + explain why). This box supports both Mica and acrylic, so the disabled path is
// only exercised on older builds / blocked drivers.
//
// Custom accent = Microsoft.UI.Xaml.Controls.Primitives.ColorSpectrum (160 DIP ring) + a hex
// TextBox + eight preset swatches. The primitive is what LightingPage's colour pickers use;
// ColorSpectrum is a real WinUI 3 type (the earlier "absent from the winmd" note was a bad
// search of the winmd string heap, not an API fact). The ColorPicker *control* is the one to
// avoid here: its template pins the inner spectrum to 256 DIP, so 160 clips the ring.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Predex.WinUI.Services;
using PreySense.Frontend.Services;
using System;
using Windows.UI;

namespace Predex.WinUI.Pages
{
    public sealed partial class ThemePage : Page
    {
        private bool _syncing;

        public ThemePage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        // ── Selection state read by the x:Bind function bindings ─────────────────
        public bool IsDarkSelected => AppearanceService.Theme == AppearanceTheme.Dark;
        public bool IsLightSelected => AppearanceService.Theme == AppearanceTheme.Light;
        public bool IsSystemThemeSelected => AppearanceService.Theme == AppearanceTheme.System;
        public bool IsWin11StyleSelected => AppearanceService.WindowStyle == WindowStyleKind.Windows11;
        public bool IsWin10StyleSelected => AppearanceService.WindowStyle == WindowStyleKind.Windows10;

        // ── Lifetime ─────────────────────────────────────────────────────────────
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            LocalizationService.LanguageChanged -= OnLanguageChanged;
            LocalizationService.LanguageChanged += OnLanguageChanged;
            SyncFromState();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
            => LocalizationService.LanguageChanged -= OnLanguageChanged;

        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            SyncFromState();
            Bindings.Update();   // OneTime text and tooltips
        }

        // ── Appearance mode ──────────────────────────────────────────────────────
        private void DarkTile_Click(object sender, RoutedEventArgs e) => SelectTheme(AppearanceTheme.Dark);

        private void LightTile_Click(object sender, RoutedEventArgs e) => SelectTheme(AppearanceTheme.Light);

        private void SystemTile_Click(object sender, RoutedEventArgs e) => SelectTheme(AppearanceTheme.System);

        private void SelectTheme(AppearanceTheme theme)
        {
            if (AppearanceService.Theme != theme) AppearanceService.SetTheme(theme);
            SyncFromState();
        }

        // ── Window style ─────────────────────────────────────────────────────────
        private void Win11Tile_Click(object sender, RoutedEventArgs e) => SelectWindowStyle(WindowStyleKind.Windows11);

        private void Win10Tile_Click(object sender, RoutedEventArgs e) => SelectWindowStyle(WindowStyleKind.Windows10);

        private void SelectWindowStyle(WindowStyleKind style)
        {
            if (AppearanceService.WindowStyle != style) AppearanceService.SetWindowStyle(style);
            SyncFromState();
        }

        // ── Material ─────────────────────────────────────────────────────────────
        private void Material_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing) return;
            if (CmbMaterial.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
            if (!Enum.TryParse(tag, ignoreCase: true, out AppearanceMaterial material)) return;

            AppearanceService.SetMaterial(material);
            // The service may have downgraded an unsupported choice to Solid; reflect that.
            SyncFromState();
        }

        // ── Accent colour ────────────────────────────────────────────────────────
        private void Accent_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing) return;
            if (CmbAccent.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
            if (!Enum.TryParse(tag, ignoreCase: true, out AccentMode mode)) return;

            // "Custom" without a fresh pick keeps the current colour (e.g. back from System).
            AppearanceService.SetAccent(mode, AppearanceService.AccentColor);
            SyncFromState();
        }

        /// <summary>Preset swatches are shortcuts for the same custom colour the ring edits.</summary>
        private void PresetAccent_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string hex) return;
            if (!TryParseHex(hex, out Color color)) return;

            AppearanceService.SetAccent(AccentMode.Custom, color);
            SyncFromState();
        }

        /// <summary>
        /// Ring drag / click. Applied live; persisted when the flyout closes so the state file
        /// is not rewritten on every pointer move.
        /// </summary>
        private void AccentSpectrum_ColorChanged(ColorSpectrum sender, ColorChangedEventArgs args)
        {
            if (_syncing) return;

            AppearanceService.SetAccent(AccentMode.Custom, args.NewColor, persist: false);

            _syncing = true;
            try
            {
                CmbAccent.SelectedIndex = (int)AccentMode.Custom;
                if (AccentHexBox is not null) AccentHexBox.Text = ToHex(args.NewColor);
            }
            finally { _syncing = false; }

            UpdateAccentSwatch(ToHex(args.NewColor));
        }

        /// <summary>Hex box: accepts "#RRGGBB" (or RRGGBB) and drives the same custom colour.</summary>
        private void AccentHexBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_syncing || AccentHexBox is null) return;

            string text = (AccentHexBox.Text ?? string.Empty).Trim();
            if (text.Length == 0) return;
            if (!text.StartsWith('#')) text = "#" + text;
            if (!TryParseHex(text, out Color color)) return;

            AppearanceService.SetAccent(AccentMode.Custom, color, persist: false);

            _syncing = true;
            try
            {
                CmbAccent.SelectedIndex = (int)AccentMode.Custom;
                if (AccentSpectrum is not null) AccentSpectrum.Color = color;
            }
            finally { _syncing = false; }

            UpdateAccentSwatch(text);
        }

        /// <summary>Typing garbage leaves the box showing it; restore the real value on blur.</summary>
        private void AccentHexBox_LostFocus(object sender, RoutedEventArgs e) => SyncFromState();

        private void AccentFlyout_Closed(object? sender, object e)
            => AppearanceService.SetAccent(AccentMode.Custom, AppearanceService.AccentColor);

        // ── State → UI ───────────────────────────────────────────────────────────
        private void SyncFromState()
        {
            if (CmbMaterial is null || CmbAccent is null) return;

            _syncing = true;
            try
            {
                CmbMaterial.SelectedIndex = (int)AppearanceService.Material;
                CmbAccent.SelectedIndex = (int)AppearanceService.AccentMode;
                UpdateMaterialAvailability();
            }
            finally { _syncing = false; }

            UpdateAccentPreview();
            Bindings.Update();
        }

        /// <summary>
        /// Grey out (IsEnabled = false) every material the OS cannot draw and explain the
        /// reason in a visible hint plus the item/row tooltip. Entries stay in the list.
        /// </summary>
        private void UpdateMaterialAvailability()
        {
            bool mica = AppearanceService.IsMaterialSupported(AppearanceMaterial.Mica);
            bool acrylic = AppearanceService.IsMaterialSupported(AppearanceMaterial.Acrylic);

            MaterialMicaItem.IsEnabled = mica;
            MaterialAcrylicItem.IsEnabled = acrylic;

            string micaReason = Loc.T("Mica is not available on this system, so the window uses a solid colour.");
            string acrylicReason = Loc.T("Acrylic is not available on this system, so the window uses a solid colour.");

            ToolTipService.SetToolTip(MaterialMicaItem, mica ? null : micaReason);
            ToolTipService.SetToolTip(MaterialAcrylicItem, acrylic ? null : acrylicReason);

            string? blocked = !mica && !acrylic
                ? Loc.T("Neither Mica nor acrylic is available on this system: the window uses a solid colour.")
                : !mica ? micaReason
                : !acrylic ? acrylicReason
                : null;

            TxtMaterialHint.Text = blocked ?? string.Empty;
            TxtMaterialHint.Visibility = blocked is null ? Visibility.Collapsed : Visibility.Visible;
            ToolTipService.SetToolTip(CmbMaterial, blocked);
        }

        /// <summary>Swatch fill + flyout title/hex/ring mirror the effective accent colour.</summary>
        private void UpdateAccentPreview()
        {
            Color color = AppearanceService.AccentColor;
            string hex = ToHex(color);

            _syncing = true;
            try
            {
                if (TxtAccentPickerTitle is not null)
                    TxtAccentPickerTitle.Text = $"{Loc.T("Pick an accent colour.")}  {hex}";
                if (AccentSpectrum is not null) AccentSpectrum.Color = color;
                if (AccentHexBox is not null && !string.Equals(AccentHexBox.Text, hex, StringComparison.OrdinalIgnoreCase))
                    AccentHexBox.Text = hex;
            }
            catch
            {
                // The flyout content is not realised until the flyout is first opened.
            }
            finally { _syncing = false; }

            UpdateAccentSwatch(hex);
        }

        private void UpdateAccentSwatch(string hex)
        {
            if (AccentSwatchFill is null) return;
            AccentSwatchFill.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                TryParseHex(hex, out Color color) ? color : AppearanceService.AccentColor);
        }

        private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

        private static bool TryParseHex(string? hex, out Color color)
        {
            color = AppearanceService.AccentColor;
            if (string.IsNullOrWhiteSpace(hex)) return false;
            string value = hex.Trim().TrimStart('#');
            if (value.Length == 6) value = "FF" + value;
            if (value.Length != 8) return false;
            try
            {
                color = Color.FromArgb(
                    Convert.ToByte(value[0..2], 16),
                    Convert.ToByte(value[2..4], 16),
                    Convert.ToByte(value[4..6], 16),
                    Convert.ToByte(value[6..8], 16));
                return true;
            }
            catch { return false; }
        }
    }
}
