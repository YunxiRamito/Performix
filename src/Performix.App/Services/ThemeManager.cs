// WinUI 3 ThemeManager — kept as the thin compatibility layer the linked WPF ViewModel
// still compiles against (MainViewModel.Theme.cs calls LoadAndApply / Apply / IsLightTheme).
//
// All state and every side effect now lives in Predex.WinUI.Services.AppearanceService
// (theme page, UPDATE-DESIGN.md §3.2.3: "保留为薄适配层（ThemeChanged 事件、IsLightTheme 等
// 既有调用点不动），内部转发到 AppearanceService"). The Core enum ApplicationTheme
// { Dark, Light, Glass } is unchanged: Glass is the legacy "material" value and maps onto
// the Mica material (UPDATE-DESIGN §3.2.2 asks for read compatibility, no config migration).

using Microsoft.UI.Xaml;
using Predex.WinUI.Services;
using PreySense.Core.Configuration;
using System;

// Make the Core enum accessible under the Services namespace so linked code that
// writes `ApplicationTheme.Dark` without a fully-qualified name compiles.
using ApplicationTheme = PreySense.Core.Configuration.ApplicationTheme;

namespace PreySense.Frontend.Services
{
    public static class ThemeManager
    {
        public static ApplicationTheme CurrentTheme =>
            AppearanceService.IsLightTheme ? ApplicationTheme.Light : ApplicationTheme.Dark;

        public static bool IsLightTheme => AppearanceService.IsLightTheme;

        /// <summary>Legacy: Glass used to mean "acrylic on the whole window"; it is the material now.</summary>
        public static bool IsGlassTheme =>
            AppearanceService.Material != AppearanceMaterial.Solid && IsGlassSupported;

        /// <summary>True when the OS can draw at least one of the backdrop materials.</summary>
        public static bool IsGlassSupported =>
            AppearanceService.IsMicaSupported() ||
            AppearanceService.IsMaterialSupported(AppearanceMaterial.Acrylic);

        // Called from ShellWindow right after InitializeComponent.
        internal static void Initialize(Window hostWindow)
        {
            if (hostWindow.Content is FrameworkElement root)
                AppearanceService.Register(hostWindow, root);
        }

        // ── LoadAndApply ─────────────────────────────────────────────────────────
        // Called from MainViewModel constructor (before the shell window exists).
        public static ApplicationTheme LoadAndApply()
        {
            AppearanceService.EnsureInitialized();
            // appearance.json wins; on a fresh profile the existing preysense.json fields
            // (General.Theme / General.LightTheme) are the initial value.
            AppearanceService.LoadConfigThemeIfNeeded();
            return CurrentTheme;
        }

        public static ApplicationTheme ResolveTheme(GeneralConfig general) =>
            general.Theme ?? (general.LightTheme ? ApplicationTheme.Light : ApplicationTheme.Dark);

        // ── Apply ────────────────────────────────────────────────────────────────
        public static void Apply(bool isLight, bool persist = true) =>
            Apply(isLight ? ApplicationTheme.Light : ApplicationTheme.Dark, persist);

        public static void Apply(ApplicationTheme theme, bool persist = true)
        {
            switch (theme)
            {
                case ApplicationTheme.Light:
                    AppearanceService.SetTheme(AppearanceTheme.Light, persist);
                    break;
                case ApplicationTheme.Glass:
                    AppearanceService.SetTheme(AppearanceTheme.Dark, persist);
                    AppearanceService.SetMaterial(AppearanceMaterial.Mica, persist);
                    break;
                default:
                    AppearanceService.SetTheme(AppearanceTheme.Dark, persist);
                    break;
            }
            ThemeChanged?.Invoke(null, theme);
        }

        // ── Events / helpers ─────────────────────────────────────────────────────
        public static event EventHandler<ApplicationTheme>? ThemeChanged;

        // Legacy compat used by TrayIconManager.
        internal static void ApplyMenuTheme(bool isLight)
        {
            // Handled by TrayIconManager; no-op here.
        }
    }
}
