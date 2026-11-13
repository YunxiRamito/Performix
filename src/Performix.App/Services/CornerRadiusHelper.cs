// Corner-radius / window-shape controller for the WinUI shell (theme page item 2).
//
// Port of Dafeiyu-Go's Click-To-Run/source/CornerRadiusHelper.cs, cut down to what a
// single-window shell needs (UPDATE-DESIGN.md §3.1.3 has the reference facts):
//   • Windows 11 style = round window corners + 4 / 6 DIP control/card radii
//   • Windows 10 style = DWM_DONOTROUND + every radius token 0
//   • the OS build decides the default (>= 22000 → Windows 11)
//   • two separate delivery paths are required:
//       1. DWM  — DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE=33, 2|1, 4)
//       2. XAML — write AppControlCornerRadius / AppOverlayCornerRadius, then force the
//          already-resolved {ThemeResource} expressions to re-resolve (a plain resource
//          write does NOT invalidate them; §3.1.3 / §3.2.4 pitfall 1)
//
// The forced re-resolution is the reference's "flip RequestedTheme to the opposite value
// and back" trick, plus a visual-tree patch of the concrete CornerRadius carriers already
// on screen (pages hold their own copies through the styles, and a style setter's
// ThemeResource is applied once per element). Everything is best effort: a failure here
// must never take the shell down.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Predex.WinUI.Services
{
    internal enum WindowStyleKind
    {
        /// <summary>Windows 11: rounded window + rounded controls (the shell default).</summary>
        Windows11,

        /// <summary>Windows 10: square window + square controls.</summary>
        Windows10,
    }

    internal static class CornerRadiusHelper
    {
        /// <summary>First Windows build with the rounded-corner DWM behaviour.</summary>
        public const int Windows11StyleMinBuild = 22000;

        public const int ControlRadius = 4;
        public const int OverlayRadius = 6;

        private const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_DONOTROUND = 1;
        private const int DWMWCP_ROUND = 2;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hWnd, uint attribute, ref int value, int size);

        /// <summary>
        /// Original radius per element, so switching back to Windows 11 restores the exact
        /// values the styles resolved (4 / 6) instead of guessing from the current number.
        /// </summary>
        private static readonly ConditionalWeakTable<DependencyObject, StrongBox<CornerRadius>> OriginalRadii = new();

        public static WindowStyleKind DetectDefaultStyle() =>
            Environment.OSVersion.Version.Build >= Windows11StyleMinBuild
                ? WindowStyleKind.Windows11
                : WindowStyleKind.Windows10;

        public static int ControlToken(WindowStyleKind style) =>
            style == WindowStyleKind.Windows10 ? 0 : ControlRadius;

        public static int OverlayToken(WindowStyleKind style) =>
            style == WindowStyleKind.Windows10 ? 0 : OverlayRadius;

        /// <summary>DWM corner preference value the style maps to (2 round / 1 don't round).</summary>
        public static int CornerPreference(WindowStyleKind style) =>
            style == WindowStyleKind.Windows10 ? DWMWCP_DONOTROUND : DWMWCP_ROUND;

        /// <summary>
        /// Writes the two corner tokens at the Application.Resources root, which shadows the
        /// merged design dictionary. Must be followed by <see cref="RefreshControlCorners"/>
        /// on every live root, otherwise the already-applied styles keep their old value.
        /// </summary>
        public static void ApplyApplicationResources(WindowStyleKind style)
        {
            try
            {
                ResourceDictionary? resources = Application.Current?.Resources;
                if (resources is null) return;
                resources["AppControlCornerRadius"] = new CornerRadius(ControlToken(style));
                resources["AppOverlayCornerRadius"] = new CornerRadius(OverlayToken(style));
            }
            catch (Exception ex)
            {
                ShellLog.Write("appearance", $"corner token write failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Re-applies the DWM round/square attribute to one HWND. The shell calls this at
        /// startup (after the WS_POPUP switch) and on every window-style change; any window
        /// created later must call it before it is shown (§3.2.4 pitfall 2).
        /// </summary>
        public static void ApplyWindowFrame(IntPtr hwnd, WindowStyleKind style)
        {
            if (hwnd == IntPtr.Zero) return;
            int preference = CornerPreference(style);
            try
            {
                int hr = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
                ShellLog.Write("appearance",
                    $"dwm corner hwnd=0x{hwnd.ToInt64():X} style={style} preference={preference} hr=0x{hr:X8}");
            }
            catch (Exception ex)
            {
                ShellLog.Write("appearance", $"dwm corner failed ({style}): {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Forces every live element that draws a corner radius to pick up the new tokens:
        /// flip RequestedTheme (invalidate ThemeResource expressions) and patch the concrete
        /// Border / Control radii on the way down the visual tree.
        /// </summary>
        public static void RefreshControlCorners(FrameworkElement root, WindowStyleKind style)
        {
            // Patch first, flip second: the walk records the radius each element had *before*
            // the appearance change as its restore value, and the flip afterwards makes every
            // style-setter {ThemeResource} pick up the token that was just written.
            int patched = 0;
            try { patched = PatchTree(root, style); }
            catch (Exception ex)
            {
                ShellLog.Write("appearance", $"corner walk failed: {ex.Message}");
            }

            try
            {
                ElementTheme requested = root.RequestedTheme;
                ElementTheme current = root.ActualTheme;
                // The flip only has to exist for one property-set; the reference restores the
                // requested value immediately so the app never renders in the other theme.
                root.RequestedTheme = current == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
                root.RequestedTheme = requested;
            }
            catch (Exception ex)
            {
                ShellLog.Write("appearance", $"corner theme flip failed: {ex.Message}");
            }

            // A styled corner value resolved through {ThemeResource} is re-written by a theme
            // change, and on some WinUI builds that write wins over the local value the walk
            // just set (observed: Border restored, Buttons not). Re-applying each element's
            // Style forces the setter to evaluate the current token, which is the value the
            // walk wanted in the first place.
            int restyled = ReapplyStyles(root);

            ShellLog.Write("appearance",
                $"control corners style={style} control={ControlToken(style)} overlay={OverlayToken(style)} " +
                $"patched={patched} restyled={restyled} {SampleRadii(root)}");
        }

        /// <summary>Forces every styled element to re-evaluate its setters against the new tokens.</summary>
        private static int ReapplyStyles(DependencyObject element)
        {
            int count = 0;
            try
            {
                if (element is FrameworkElement frameworkElement)
                {
                    Style? style = frameworkElement.Style;
                    if (style is not null)
                    {
                        frameworkElement.Style = null;
                        frameworkElement.Style = style;
                        count++;
                    }
                }

                int children = VisualTreeHelper.GetChildrenCount(element);
                for (int i = 0; i < children; i++)
                    count += ReapplyStyles(VisualTreeHelper.GetChild(element, i));
            }
            catch (Exception ex)
            {
                ShellLog.Write("appearance", $"corner restyle failed: {ex.Message}");
            }
            return count;
        }

        private static string SampleRadii(FrameworkElement root)
        {
            var samples = new List<string>(4);
            CollectRadii(root, samples, 4);
            return samples.Count == 0 ? "corner=<none>" : string.Join(" ", samples);
        }

        private static void CollectRadii(DependencyObject element, List<string> samples, int limit)
        {
            if (samples.Count >= limit) return;
            if (element is Control control && control.CornerRadius.TopLeft > 0)
                samples.Add($"[{control.GetType().Name}={control.CornerRadius.TopLeft}]");

            int children = VisualTreeHelper.GetChildrenCount(element);
            for (int i = 0; i < children; i++)
                CollectRadii(VisualTreeHelper.GetChild(element, i), samples, limit);
        }

        private static int PatchTree(DependencyObject element, WindowStyleKind style)
        {
            int patched = 0;

            if (element is Border border)
            {
                CornerRadius target = Map(OriginalRadius(border), style);
                if (!border.CornerRadius.Equals(target)) { border.CornerRadius = target; patched++; }
            }
            else if (element is Control control)
            {
                CornerRadius target = Map(OriginalRadius(control), style);
                if (!control.CornerRadius.Equals(target)) { control.CornerRadius = target; patched++; }
            }

            int count = VisualTreeHelper.GetChildrenCount(element);
            for (int i = 0; i < count; i++)
                patched += PatchTree(VisualTreeHelper.GetChild(element, i), style);

            return patched;
        }

        /// <summary>
        /// First radius seen for an element (the value the styles resolved before any
        /// appearance change), so switching back to Windows 11 restores it exactly.
        /// </summary>
        private static CornerRadius OriginalRadius(DependencyObject element)
        {
            CornerRadius current = element switch
            {
                Border border => border.CornerRadius,
                Control control => control.CornerRadius,
                _ => default,
            };
            return OriginalRadii.GetValue(element, _ => new StrongBox<CornerRadius>(current)).Value;
        }

        /// <summary>
        /// 4 → control token, 6 → overlay token, anything else (0, 8, 20 …) untouched: those
        /// are decorative radii that are not part of the window-style contract.
        /// </summary>
        private static CornerRadius Map(CornerRadius original, WindowStyleKind style)
        {
            if (original.TopLeft == ControlRadius && original.BottomRight == ControlRadius)
                return new CornerRadius(ControlToken(style));
            if (original.TopLeft == OverlayRadius && original.BottomRight == OverlayRadius)
                return new CornerRadius(OverlayToken(style));
            return original;
        }
    }
}
