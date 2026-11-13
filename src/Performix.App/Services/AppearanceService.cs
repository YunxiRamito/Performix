// Appearance controller for the WinUI shell (theme page, UPDATE-DESIGN.md §3).
//
// One shared state for the whole process — the reference implementation's lesson
// (§3.1.2: "设置窗改一次，托盘菜单、更新浮窗、通知窗全部跟着改，不要各存一份"). It owns:
//
//   1. theme        Dark / Light / Follow system   → root.RequestedTheme + design-token brushes
//   2. window style Windows 11 / Windows 10        → CornerRadiusHelper (DWM + radius tokens)
//   3. material     Mica / Acrylic / Solid         → Window.SystemBackdrop (+ solid fallback)
//   4. accent       Follow Windows / #3B82F6 /自定义 → AccentFillColorDefaultBrush family
//
// Every setter applies immediately, writes one shell.log line, and persists to
// %LOCALAPPDATA%\Predex\appearance.json (preysense.json is a red line: its General.Theme /
// LightTheme fields are reused for Dark/Light, but "follow system", window style, material
// and accent have no Core field and must not grow one from this stage).
//
// Theme reaches the XAML through two paths on purpose:
//   • RequestedTheme drives every Fluent control and every {ThemeResource} (cards, dialogs,
//     combos, title bar text of controls).
//   • The app's own design tokens are StaticResource SolidColorBrush instances
//     (TextPrimaryBrush, SectionBgBrush, …). A StaticResource never re-resolves,
//     so the brush *instances* are recoloured: every page that resolved the resource keeps
//     the same object and repaints with it. That is why the light theme also flips the
//     hand-written dark palette instead of only Fluent's.

using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PreySense.Core.Configuration;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Predex.WinUI.Services
{
    internal enum AppearanceTheme
    {
        System,
        Dark,
        Light,
    }

    internal enum AppearanceMaterial
    {
        Mica,
        Acrylic,
        Solid,
    }

    internal enum AccentMode
    {
        System,
        Fixed,
        Custom,
    }

    internal static class AppearanceService
    {
        private sealed class Registration
        {
            public required Window Window { get; init; }
            public required FrameworkElement Root { get; init; }
        }

        private sealed class PersistedAppearance
        {
            public string? Theme { get; set; }
            public string? Material { get; set; }
            public string? WindowStyle { get; set; }
            public string? AccentMode { get; set; }
            public string? AccentColor { get; set; }
        }

        private static readonly Color DefaultAccent = Color.FromArgb(0xFF, 0x3B, 0x82, 0xF6);
        private static readonly object Gate = new();
        private static readonly List<Registration> Windows = new();

        private static bool _initialized;

        public static AppearanceTheme Theme { get; private set; } = AppearanceTheme.Dark;
        public static AppearanceMaterial Material { get; private set; } = AppearanceMaterial.Mica;
        public static WindowStyleKind WindowStyle { get; private set; } = WindowStyleKind.Windows11;
        public static AccentMode AccentMode { get; private set; } = AccentMode.Fixed;
        public static Color AccentColor { get; private set; } = DefaultAccent;

        /// <summary>
        /// True when %LOCALAPPDATA%\Predex\appearance.json existed at startup. It outranks the
        /// legacy preysense.json fields, so a user who picked "follow system" is not reset to
        /// dark on the next launch.
        /// </summary>
        public static bool HasPersistedState { get; private set; }

        /// <summary>Raised after a theme change so tray menu colours etc. can follow.</summary>
        public static event EventHandler? ThemeChanged;

        // ── Derived values ───────────────────────────────────────────────────────
        public static bool IsLightTheme =>
            Theme switch
            {
                AppearanceTheme.Light => true,
                AppearanceTheme.Dark => false,
                _ => SystemPrefersLight(),
            };

        public static bool IsMicaSupported() => IsMaterialSupported(AppearanceMaterial.Mica);

        public static bool IsMaterialSupported(AppearanceMaterial material)
        {
            try
            {
                return material switch
                {
                    AppearanceMaterial.Mica => MicaController.IsSupported(),
                    AppearanceMaterial.Acrylic => DesktopAcrylicController.IsSupported(),
                    _ => true,
                };
            }
            catch
            {
                return false;
            }
        }

        private static bool SystemPrefersLight()
        {
            try
            {
                // Same test as the reference: the OS background colour's red channel.
                return new UISettings().GetColorValue(UIColorType.Background).R >= 128;
            }
            catch { return false; }
        }

        // ── Startup ──────────────────────────────────────────────────────────────
        /// <summary>
        /// Loads the persisted appearance and writes the design tokens once. Called from
        /// ThemeManager.LoadAndApply (MainViewModel constructor) and/or the first Register.
        /// </summary>
        public static void EnsureInitialized()
        {
            if (_initialized) return;
            _initialized = true;

            WindowStyle = CornerRadiusHelper.DetectDefaultStyle();
            Theme = AppearanceTheme.Dark;
            Material = AppearanceMaterial.Mica;
            AccentMode = AccentMode.Fixed;
            AccentColor = DefaultAccent;

            LoadPersisted();

            // A material the OS cannot draw must not be the active choice.
            if (Material != AppearanceMaterial.Solid && !IsMaterialSupported(Material))
                Material = AppearanceMaterial.Solid;

            ApplyThemeTokens();
            CornerRadiusHelper.ApplyApplicationResources(WindowStyle);
            ApplyAccentBrushes();

            ShellLog.Write("appearance",
                $"init theme={Theme} material={Material} style={WindowStyle} accent={AccentMode} " +
                $"color=#{ToHex(AccentColor)} mica={IsMaterialSupported(AppearanceMaterial.Mica)} " +
                $"acrylic={IsMaterialSupported(AppearanceMaterial.Acrylic)} light={IsLightTheme}");
        }

        /// <summary>
        /// Registers a window root with the shared appearance. The shell calls it once, right
        /// after InitializeComponent (the root has to exist before RequestedTheme can be set).
        /// </summary>
        public static void Register(Window window, FrameworkElement root)
        {
            EnsureInitialized();

            bool alreadyRegistered = false;
            lock (Gate)
            {
                foreach (Registration existing in Windows)
                {
                    if (ReferenceEquals(existing.Window, window)) { alreadyRegistered = true; break; }
                }
                if (!alreadyRegistered) Windows.Add(new Registration { Window = window, Root = root });
            }
            if (alreadyRegistered) return;

            // Follow-system mode has to react to an OS light/dark switch while the app runs.
            root.ActualThemeChanged += (_, _) =>
            {
                if (Theme == AppearanceTheme.System) ApplyThemeTokens();
            };

            ApplyAll();
        }

        // ── Setters (the theme page calls these; each one is immediate + logged) ──
        /// <summary>
        /// First-run only: derive the theme from the two fields preysense.json already has.
        /// Once appearance.json exists it owns the state, so this is a no-op.
        /// </summary>
        public static void LoadConfigThemeIfNeeded()
        {
            if (HasPersistedState) return;
            try
            {
                GeneralConfig general = PreySense.Core.Configuration.ConfigurationService.Instance
                    .GetCurrent().General;                AppearanceTheme theme = general.Theme switch
                {
                    PreySense.Core.Configuration.ApplicationTheme.Light => AppearanceTheme.Light,
                    PreySense.Core.Configuration.ApplicationTheme.Dark => AppearanceTheme.Dark,
                    _ => general.LightTheme ? AppearanceTheme.Light : AppearanceTheme.Dark,
                };
                if (theme == Theme) return;
                Theme = theme;
                ApplyThemeTokens();
                ShellLog.Write("appearance",
                    $"theme from preysense.json: {theme} (Theme={general.Theme?.ToString() ?? "null"}, LightTheme={general.LightTheme})");
            }
            catch (Exception ex)
            {
                ShellLog.Write("appearance", $"config theme read failed: {ex.Message}");
            }
        }

        public static void SetTheme(AppearanceTheme theme, bool persist = true)
        {
            EnsureInitialized();
            Theme = theme;
            ApplyAll();
            ShellLog.Write("appearance",
                $"theme={theme} requested={RequestedThemeName()} effectiveLight={IsLightTheme} (immediate, no apply button)");
            PersistCoreTheme(theme);
            RaiseThemeChanged();
            if (persist) Persist();
        }

        public static void SetMaterial(AppearanceMaterial material, bool persist = true)
        {
            EnsureInitialized();
            if (material != AppearanceMaterial.Solid && !IsMaterialSupported(material))
            {
                ShellLog.Write("appearance",
                    $"material={material} requested but not supported on this system → solid");
                material = AppearanceMaterial.Solid;
            }
            Material = material;
            ApplyMaterial();
            ShellLog.Write("appearance",
                $"material={material} backdrop={BackdropName()} solidFallback={Material == AppearanceMaterial.Solid} (immediate)");
            if (persist) Persist();
        }

        public static void SetWindowStyle(WindowStyleKind style, bool persist = true)
        {
            EnsureInitialized();
            WindowStyle = style;
            CornerRadiusHelper.ApplyApplicationResources(style);
            foreach (Registration registration in Snapshot())
            {
                CornerRadiusHelper.ApplyWindowFrame(HwndOf(registration.Window), style);
                CornerRadiusHelper.RefreshControlCorners(registration.Root, style);
            }
            ShellLog.Write("appearance",
                $"window style={style} controlRadius={CornerRadiusHelper.ControlToken(style)} " +
                $"overlayRadius={CornerRadiusHelper.OverlayToken(style)} dwm={CornerRadiusHelper.CornerPreference(style)} (immediate)");
            if (persist) Persist();
        }

        public static void SetAccent(AccentMode mode, Color color, bool persist = true)
        {
            EnsureInitialized();
            AccentMode = mode;
            AccentColor = mode switch
            {
                AccentMode.System => ReadSystemAccent(),
                AccentMode.Fixed => DefaultAccent,
                _ => color,
            };
            ApplyAccentBrushes();
            ShellLog.Write("appearance",
                $"accent mode={mode} color=#{ToHex(AccentColor)} " +
                $"AccentFillColorDefaultBrush/ToggleSwitchFillOn/SliderTrackValueFill updated (immediate)");
            if (persist) Persist();
        }

        // ── Apply paths ──────────────────────────────────────────────────────────
        public static void ApplyAll()
        {
            foreach (Registration registration in Snapshot())
            {
                ApplyRequestedTheme(registration);
                ApplyMaterial(registration);
            }
            ApplyThemeTokens();
        }

        private static void ApplyRequestedTheme(Registration registration)
        {
            try
            {
                registration.Root.RequestedTheme = Theme switch
                {
                    AppearanceTheme.Light => ElementTheme.Light,
                    AppearanceTheme.Dark => ElementTheme.Dark,
                    _ => ElementTheme.Default,
                };
            }
            catch (Exception ex)
            {
                ShellLog.Write("appearance", $"RequestedTheme failed: {ex.Message}");
            }
        }

        private static void ApplyMaterial()
        {
            foreach (Registration registration in Snapshot()) ApplyMaterial(registration);
        }

        private static void ApplyMaterial(Registration registration)
        {
            AppearanceMaterial material = Material;
            if (material != AppearanceMaterial.Solid && !IsMaterialSupported(material))
                material = AppearanceMaterial.Solid;

            try
            {
                switch (material)
                {
                    case AppearanceMaterial.Mica:
                        registration.Window.SystemBackdrop = new MicaBackdrop
                        {
                            Kind = MicaKind.BaseAlt,
                        };
                        SetSurface(registration.Root, transparent: true);
                        break;
                    case AppearanceMaterial.Acrylic:
                        registration.Window.SystemBackdrop = new DesktopAcrylicBackdrop();
                        SetSurface(registration.Root, transparent: true);
                        break;
                    default:
                        registration.Window.SystemBackdrop = null;
                        SetSurface(registration.Root, transparent: false);
                        break;
                }
            }
            catch (Exception ex)
            {
                // §3.1.4: any failure must end in a readable solid window, never a black one.
                ShellLog.Write("appearance", $"material apply failed ({material}): {ex.Message} → solid");
                try { registration.Window.SystemBackdrop = null; } catch { }
                SetSurface(registration.Root, transparent: false);
            }
        }

        private static void SetSurface(FrameworkElement root, bool transparent)
        {
            try
            {
                Brush surface;
                if (transparent)
                {
                    surface = Application.Current?.Resources.TryGetValue("TransparentBrush", out object? value) == true &&
                              value is Brush transparentBrush
                        ? transparentBrush
                        : new SolidColorBrush(Colors.Transparent);
                }
                else
                {
                    // Reference values (§3.1.4): dark #202020 / light #F3F3F3.
                    surface = new SolidColorBrush(IsLightTheme
                        ? Color.FromArgb(0xFF, 0xF3, 0xF3, 0xF3)
                        : Color.FromArgb(0xFF, 0x20, 0x20, 0x20));
                }

                switch (root)
                {
                    case Panel panel: panel.Background = surface; break;
                    case Control control: control.Background = surface; break;
                }
            }
            catch (Exception ex)
            {
                ShellLog.Write("appearance", $"surface brush failed: {ex.Message}");
            }
        }

        // ── Design tokens (the app's own StaticResource palette) ─────────────────
        private static readonly (string Key, Color Dark, Color Light)[] TokenBrushes =
        {
            ("WindowBgBrush",       Hex("#0A0A12"), Hex("#F3F3F3")),
            ("SectionBgBrush",      Hex("#14FFFFFF"), Hex("#0F000000")),
            ("SectionBgHoverBrush", Hex("#1FFFFFFF"), Hex("#1A000000")),
            ("BorderSubtleBrush",   Hex("#20FFFFFF"), Hex("#1A000000")),
            ("BorderStrongBrush",   Hex("#33FFFFFF"), Hex("#33000000")),
            ("TextPrimaryBrush",    Hex("#F2F2F2"), Hex("#1A1A1E")),
            ("TextSecondaryBrush",  Hex("#A8A8BC"), Hex("#3F3F46")),
            ("TextMutedBrush",      Hex("#7C7C90"), Hex("#6B6B76")),
        };

        private static void ApplyThemeTokens()
        {
            bool light = IsLightTheme;
            foreach ((string key, Color dark, Color lightColor) in TokenBrushes)
                SetBrushColor(key, light ? lightColor : dark);
        }

        // ── Accent family ────────────────────────────────────────────────────────
        private static void ApplyAccentBrushes()
        {
            Color accent = AccentColor;
            SetColorResource("Accent", accent);
            SetBrushColor("AccentBrush", accent);
            // The selected tile fill is the same 15 % accent tint the home tiles use.
            SetBrushColor("TileSelectedFillBrush", WithAlpha(accent, 0x26));

            ResourceDictionary? design = DesignDictionary();
            if (design is null) return;

            if (design.ThemeDictionaries.Count == 0)
            {
                ApplyAccentToDictionary(design, accent, IsLightTheme);
                return;
            }

            foreach (KeyValuePair<object, object> entry in design.ThemeDictionaries)
            {
                if (entry.Value is not ResourceDictionary themeDictionary) continue;
                bool light = string.Equals(entry.Key as string, "Light", StringComparison.OrdinalIgnoreCase);
                ApplyAccentToDictionary(themeDictionary, accent, light);
            }
        }

        /// <summary>
        /// Re-colours the WinUI accent brush family (the same keys Resources.xaml pins to
        /// #3B82F6) from one base colour. WinUI has no public "set accent" API, so the
        /// reference implementation's approach is followed: overwrite the accent-derived
        /// resource keys instead of drawing a private accent system.
        /// </summary>
        private static void ApplyAccentToDictionary(ResourceDictionary dictionary, Color accent, bool lightTheme)
        {
            Color light1 = Blend(accent, Colors.White, 0.22);
            Color light2 = Blend(accent, Colors.White, 0.45);
            Color light3 = Blend(accent, Colors.White, 0.70);
            Color dark1 = Blend(accent, Colors.Black, 0.22);
            Color dark2 = Blend(accent, Colors.Black, 0.40);
            Color dark3 = Blend(accent, Colors.Black, 0.55);

            SetColorIn(dictionary, "SystemAccentColor", accent);
            SetColorIn(dictionary, "SystemAccentColorLight1", light1);
            SetColorIn(dictionary, "SystemAccentColorLight2", light2);
            SetColorIn(dictionary, "SystemAccentColorLight3", light3);
            SetColorIn(dictionary, "SystemAccentColorDark1", dark1);
            SetColorIn(dictionary, "SystemAccentColorDark2", dark2);
            SetColorIn(dictionary, "SystemAccentColorDark3", dark3);

            SetBrushIn(dictionary, "AccentFillColorDefaultBrush", accent);
            SetBrushIn(dictionary, "AccentFillColorSecondaryBrush", WithAlpha(accent, 0xE6));
            SetBrushIn(dictionary, "AccentFillColorTertiaryBrush", WithAlpha(accent, 0x4D));

            Color accentText = lightTheme ? dark1 : accent;
            SetBrushIn(dictionary, "AccentTextFillColorPrimaryBrush", accentText);
            SetBrushIn(dictionary, "AccentTextFillColorSecondaryBrush", accentText);
            SetBrushIn(dictionary, "AccentTextFillColorTertiaryBrush", accentText);

            SetBrushIn(dictionary, "AccentButtonBackground", accent);
            SetBrushIn(dictionary, "AccentButtonBackgroundPointerOver", light1);
            SetBrushIn(dictionary, "AccentButtonBackgroundPressed", dark1);

            SetBrushIn(dictionary, "ToggleSwitchFillOn", accent);
            SetBrushIn(dictionary, "ToggleSwitchFillOnPointerOver", light1);
            SetBrushIn(dictionary, "ToggleSwitchFillOnPressed", dark1);
            SetBrushIn(dictionary, "ToggleSwitchStrokeOn", accent);
            SetBrushIn(dictionary, "ToggleSwitchStrokeOnPointerOver", light1);
            SetBrushIn(dictionary, "ToggleSwitchStrokeOnPressed", dark1);

            SetBrushIn(dictionary, "SliderTrackValueFill", accent);
            SetBrushIn(dictionary, "SliderTrackValueFillPointerOver", light1);
            SetBrushIn(dictionary, "SliderTrackValueFillPressed", dark1);
            SetBrushIn(dictionary, "SliderThumbBackground", accent);
            SetBrushIn(dictionary, "SliderThumbBackgroundPointerOver", light1);
            SetBrushIn(dictionary, "SliderThumbBackgroundPressed", dark1);
            SetBrushIn(dictionary, "ProgressBarForeground", accent);
        }

        private static Color ReadSystemAccent()
        {
            try { return new UISettings().GetColorValue(UIColorType.Accent); }
            catch { return DefaultAccent; }
        }

        // ── Resource helpers ─────────────────────────────────────────────────────
        private static ResourceDictionary? DesignDictionary()
        {
            try
            {
                ResourceDictionary? resources = Application.Current?.Resources;
                if (resources is null) return null;
                foreach (ResourceDictionary dictionary in resources.MergedDictionaries)
                {
                    if (dictionary.ContainsKey("AccentBrush")) return dictionary;
                }
            }
            catch (Exception ex)
            {
                ShellLog.Write("appearance", $"design dictionary lookup failed: {ex.Message}");
            }
            return null;
        }

        private static void SetBrushColor(string key, Color color)
        {
            try
            {
                if (Application.Current?.Resources.TryGetValue(key, out object? value) == true &&
                    value is SolidColorBrush brush && !brush.Color.Equals(color))
                {
                    brush.Color = color;
                }
            }
            catch (Exception ex)
            {
                ShellLog.Write("appearance", $"token brush '{key}' failed: {ex.Message}");
            }
        }

        private static void SetColorResource(string key, Color color)
        {
            try
            {
                if (Application.Current?.Resources.TryGetValue(key, out object? value) == true &&
                    value is Color existing && !existing.Equals(color))
                {
                    Application.Current.Resources[key] = color;
                }
            }
            catch { }
        }

        private static void SetBrushIn(ResourceDictionary dictionary, string key, Color color)
        {
            if (dictionary.TryGetValue(key, out object? value) && value is SolidColorBrush brush)
                brush.Color = color;
        }

        private static void SetColorIn(ResourceDictionary dictionary, string key, Color color) =>
            dictionary[key] = color;

        // ── Persistence (%LOCALAPPDATA%\Predex\appearance.json, not preysense.json) ─
        private static string StatePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Predex", "appearance.json");

        private static void LoadPersisted()
        {
            try
            {
                if (!File.Exists(StatePath)) return;
                HasPersistedState = true;
                PersistedAppearance? state = JsonSerializer.Deserialize<PersistedAppearance>(
                    File.ReadAllText(StatePath));
                if (state is null) return;

                if (Enum.TryParse(state.Theme, ignoreCase: true, out AppearanceTheme theme)) Theme = theme;
                if (Enum.TryParse(state.Material, ignoreCase: true, out AppearanceMaterial material)) Material = material;
                if (Enum.TryParse(state.WindowStyle, ignoreCase: true, out WindowStyleKind style)) WindowStyle = style;
                if (Enum.TryParse(state.AccentMode, ignoreCase: true, out AccentMode mode)) AccentMode = mode;
                if (TryParseHex(state.AccentColor, out Color color)) AccentColor = color;
            }
            catch (Exception ex)
            {
                ShellLog.Write("appearance", $"state load failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Dark / Light are written back through the existing preysense.json field
        /// (General.Theme) so the WPF build and a config edit stay in sync. "Follow system"
        /// has no Core field and is deliberately NOT invented here — it lives in
        /// appearance.json and leaves General.Theme untouched (UPDATE-DESIGN §3.2.2 rule:
        /// read compatibility, no schema growth from this stage).
        /// </summary>
        private static void PersistCoreTheme(AppearanceTheme theme)
        {
            if (theme == AppearanceTheme.System) return;
            try
            {
                PreySense.Core.Configuration.ApplicationTheme coreTheme = theme == AppearanceTheme.Light
                    ? PreySense.Core.Configuration.ApplicationTheme.Light
                    : PreySense.Core.Configuration.ApplicationTheme.Dark;
                PreySense.Core.Configuration.ConfigurationService.Instance.Update(cfg =>
                {
                    cfg.General.Theme = coreTheme;
                    cfg.General.LightTheme = coreTheme == PreySense.Core.Configuration.ApplicationTheme.Light;
                });
            }
            catch (Exception ex)
            {
                ShellLog.Write("appearance", $"config theme write failed: {ex.Message}");
            }
        }

        private static void Persist()
        {            try
            {
                string directory = Path.GetDirectoryName(StatePath)!;
                Directory.CreateDirectory(directory);
                string json = JsonSerializer.Serialize(new PersistedAppearance
                {
                    Theme = Theme.ToString(),
                    Material = Material.ToString(),
                    WindowStyle = WindowStyle.ToString(),
                    AccentMode = AccentMode.ToString(),
                    AccentColor = "#" + ToHex(AccentColor),
                }, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(StatePath, json);
                ShellLog.Write("appearance", $"state saved → {StatePath}");
            }
            catch (Exception ex)
            {
                ShellLog.Write("appearance", $"state save failed: {ex.Message}");
            }
        }

        // ── Small utilities ──────────────────────────────────────────────────────
        private static List<Registration> Snapshot()
        {
            lock (Gate) return new List<Registration>(Windows);
        }

        private static string RequestedThemeName() =>
            Theme switch
            {
                AppearanceTheme.Light => "Light",
                AppearanceTheme.Dark => "Dark",
                _ => "Default(follow system)",
            };

        private static string BackdropName() =>
            Material switch
            {
                AppearanceMaterial.Mica => "MicaBackdrop(BaseAlt)",
                AppearanceMaterial.Acrylic => "DesktopAcrylicBackdrop",
                _ => "none(solid)",
            };

        private static IntPtr HwndOf(Window window)
        {
            try { return WinRT.Interop.WindowNative.GetWindowHandle(window); }
            catch { return IntPtr.Zero; }
        }

        private static void RaiseThemeChanged() => ThemeChanged?.Invoke(null, EventArgs.Empty);

        private static Color Blend(Color from, Color to, double amount)
        {
            amount = Math.Clamp(amount, 0, 1);
            byte Mix(byte a, byte b) => (byte)Math.Clamp(Math.Round(a + ((b - a) * amount)), 0, 255);
            return Color.FromArgb(from.A, Mix(from.R, to.R), Mix(from.G, to.G), Mix(from.B, to.B));
        }

        private static Color WithAlpha(Color color, byte alpha) =>
            Color.FromArgb(alpha, color.R, color.G, color.B);

        private static Color Hex(string hex) =>
            TryParseHex(hex, out Color color) ? color : DefaultAccent;

        private static bool TryParseHex(string? hex, out Color color)
        {
            color = DefaultAccent;
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

        private static string ToHex(Color color) =>
            $"{color.R:X2}{color.G:X2}{color.B:X2}";
    }
}
