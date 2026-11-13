// WinUI 3 replacement for the WPF self-drawn keyboard layout
// (app\Fork\PreySense.Frontend\Controls\RgbKeyboardLayout.cs + KeyboardLayoutGeometry).
//
// Why a rewrite instead of a port: the WPF control paints everything in OnRender with
// FormattedText/DrawingContext, none of which exists in WinUI 3 (WINUI3-MIGRATION.md §3.9).
// Here every key is a Border + TextBlock on a Canvas, so:
//   * hit testing is free — each Border gets its own Tapped handler (no manual rect math),
//   * theming works (brushes are DependencyProperties, defaults mirror Styles\Resources.xaml),
//   * AutomationProperties.Name can be set per key,
//   * the selection highlight is a real BorderBrush/BorderThickness change.
//
// The key geometry table is the WPF KeyboardLayoutGeometry table unchanged: 40 DIP per 1u
// key, 4 DIP gap. Wire indices still come from the detected OEM device; the table only
// decides where a key *name* is drawn.

using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PreySense.Frontend.ViewModels;
using PreySense.Rgb;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace Predex.WinUI.Controls
{
    public sealed partial class KeyboardLayoutView : UserControl
    {
        // ── Geometry constants (same numbers as the WPF control) ─────────────────
        private const double KeyUnit = 40.0;
        private const double KeyGap = 4.0;
        private const double EmptyWidth = 740.0;
        private const double EmptyHeight = 256.0;

        // Dark base used to tint the per-key body, roughly WindowBg(#0A0A12) over a card.
        private static readonly Color TintBase = Color.FromArgb(0xFF, 0x24, 0x24, 0x2E);
        private static readonly Color FallbackKeyColor = Color.FromArgb(0xFF, 0x00, 0xE5, 0xFF);

        private readonly Dictionary<LightingLedEditor, KeyVisual> _visuals = new();
        private readonly List<LightingLedEditor> _editors = new();
        private IReadOnlyList<KeyboardKeyPlacement> _placements = Array.Empty<KeyboardKeyPlacement>();
        private bool _listening;

        public KeyboardLayoutView()
        {
            InitializeComponent();
            Loaded += (_, _) => { if (!_listening) Rebuild(); };
            Unloaded += (_, _) => DetachAll();
        }

        // ── Dependency properties ────────────────────────────────────────────────
        public static readonly DependencyProperty KeysSourceProperty =
            DependencyProperty.Register(nameof(KeysSource), typeof(IEnumerable<LightingLedEditor>),
                typeof(KeyboardLayoutView), new PropertyMetadata(null, OnSourceChanged));

        /// <summary>LED editors to draw; one visual per item, ordered by its index.</summary>
        public IEnumerable<LightingLedEditor>? KeysSource
        {
            get => (IEnumerable<LightingLedEditor>?)GetValue(KeysSourceProperty);
            set => SetValue(KeysSourceProperty, value);
        }

        public static readonly DependencyProperty SelectedKeyProperty =
            DependencyProperty.Register(nameof(SelectedKey), typeof(LightingLedEditor),
                typeof(KeyboardLayoutView), new PropertyMetadata(null, OnSelectionChanged));

        /// <summary>Primary (last clicked) key. Bound two-way to MainViewModel.SelectedLightingKey.</summary>
        public LightingLedEditor? SelectedKey
        {
            get => (LightingLedEditor?)GetValue(SelectedKeyProperty);
            set => SetValue(SelectedKeyProperty, value);
        }

        public static readonly DependencyProperty SelectedKeysProperty =
            DependencyProperty.Register(nameof(SelectedKeys), typeof(IList<LightingLedEditor>),
                typeof(KeyboardLayoutView), new PropertyMetadata(null, OnSelectedKeysChanged));

        /// <summary>Multi-selection list (Ctrl/Shift click). Bound to MainViewModel.SelectedLightingKeys.</summary>
        public IList<LightingLedEditor>? SelectedKeys
        {
            get => (IList<LightingLedEditor>?)GetValue(SelectedKeysProperty);
            set => SetValue(SelectedKeysProperty, value);
        }

        public static readonly DependencyProperty CommonColorHexProperty =
            DependencyProperty.Register(nameof(CommonColorHex), typeof(string),
                typeof(KeyboardLayoutView), new PropertyMetadata("#00E5FF", OnColorInputChanged));

        /// <summary>Colour used for all keys while <see cref="UseIndividualColors"/> is false.</summary>
        public string CommonColorHex
        {
            get => (string)GetValue(CommonColorHexProperty);
            set => SetValue(CommonColorHexProperty, value);
        }

        public static readonly DependencyProperty UseIndividualColorsProperty =
            DependencyProperty.Register(nameof(UseIndividualColors), typeof(bool),
                typeof(KeyboardLayoutView), new PropertyMetadata(true, OnColorInputChanged));

        /// <summary>False = every key shows <see cref="CommonColorHex"/> (uniform colour mode).</summary>
        public bool UseIndividualColors
        {
            get => (bool)GetValue(UseIndividualColorsProperty);
            set => SetValue(UseIndividualColorsProperty, value);
        }

        public static readonly DependencyProperty KeyBackgroundBrushProperty =
            DependencyProperty.Register(nameof(KeyBackgroundBrush), typeof(Brush), typeof(KeyboardLayoutView),
                new PropertyMetadata(new SolidColorBrush(TintBase), OnBrushChanged));

        public Brush KeyBackgroundBrush
        {
            get => (Brush)GetValue(KeyBackgroundBrushProperty);
            set => SetValue(KeyBackgroundBrushProperty, value);
        }

        public static readonly DependencyProperty KeyForegroundBrushProperty =
            DependencyProperty.Register(nameof(KeyForegroundBrush), typeof(Brush), typeof(KeyboardLayoutView),
                new PropertyMetadata(new SolidColorBrush(Color.FromArgb(0xFF, 0xF2, 0xF2, 0xF2)), OnBrushChanged));

        public Brush KeyForegroundBrush
        {
            get => (Brush)GetValue(KeyForegroundBrushProperty);
            set => SetValue(KeyForegroundBrushProperty, value);
        }

        public static readonly DependencyProperty KeyAccentBrushProperty =
            DependencyProperty.Register(nameof(KeyAccentBrush), typeof(Brush), typeof(KeyboardLayoutView),
                new PropertyMetadata(new SolidColorBrush(Color.FromArgb(0xFF, 0x3B, 0x82, 0xF6)), OnBrushChanged));

        public Brush KeyAccentBrush
        {
            get => (Brush)GetValue(KeyAccentBrushProperty);
            set => SetValue(KeyAccentBrushProperty, value);
        }

        public static readonly DependencyProperty KeyBorderBrushProperty =
            DependencyProperty.Register(nameof(KeyBorderBrush), typeof(Brush), typeof(KeyboardLayoutView),
                new PropertyMetadata(new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), OnBrushChanged));

        public Brush KeyBorderBrush
        {
            get => (Brush)GetValue(KeyBorderBrushProperty);
            set => SetValue(KeyBorderBrushProperty, value);
        }

        private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is KeyboardLayoutView view) view.Rebuild();
        }

        private static void OnSelectionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is KeyboardLayoutView view)
            {
                view.UpdateAllVisuals();
                view.UpdateAutomationName();
            }
        }

        private static void OnSelectedKeysChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not KeyboardLayoutView view) return;
            if (e.OldValue is INotifyCollectionChanged oldList) oldList.CollectionChanged -= view.OnSelectedKeysCollectionChanged;
            if (e.NewValue is INotifyCollectionChanged newList) newList.CollectionChanged += view.OnSelectedKeysCollectionChanged;
            view.UpdateAllVisuals();
        }

        private static void OnColorInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is KeyboardLayoutView view) view.UpdateAllVisuals();
        }

        private static void OnBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is KeyboardLayoutView view) view.UpdateAllVisuals();
        }

        private void OnSelectedKeysCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
            => UpdateAllVisuals();

        // ── Build / tear down ────────────────────────────────────────────────────
        private void DetachAll()
        {
            if (KeysSource is INotifyCollectionChanged source)
                source.CollectionChanged -= OnSourceCollectionChanged;
            foreach (LightingLedEditor editor in _editors)
                editor.PropertyChanged -= OnEditorPropertyChanged;
            if (SelectedKeys is INotifyCollectionChanged selection)
                selection.CollectionChanged -= OnSelectedKeysCollectionChanged;
            _editors.Clear();
            _visuals.Clear();
            _placements = Array.Empty<KeyboardKeyPlacement>();
            _listening = false;
        }

        private void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

        private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is LightingLedEditor editor &&
                (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(LightingLedEditor.ColorHex)))
                UpdateVisual(editor);
        }

        private void Rebuild()
        {
            DetachAll();
            KeyCanvas.Children.Clear();

            if (KeysSource is INotifyCollectionChanged source)
                source.CollectionChanged += OnSourceCollectionChanged;
            if (SelectedKeys is INotifyCollectionChanged selection)
                selection.CollectionChanged += OnSelectedKeysCollectionChanged;
            _listening = true;

            _editors.AddRange(KeysSource ?? Enumerable.Empty<LightingLedEditor>());
            foreach (LightingLedEditor editor in _editors)
                editor.PropertyChanged += OnEditorPropertyChanged;

            _placements = KeyboardGeometry.Build(
                _editors.Select(editor => new LightingLedInfo(editor.Index, editor.Name)).ToArray());

            // Duplicate indices cannot happen in valid layouts, but a malformed device must
            // not throw: draw the first editor for an index and skip its duplicates.
            var byIndex = new Dictionary<int, LightingLedEditor>();
            foreach (LightingLedEditor editor in _editors)
                byIndex.TryAdd(editor.Index, editor);

            double width = 0, height = 0;
            foreach (KeyboardKeyPlacement placement in _placements)
            {
                if (!byIndex.TryGetValue(placement.Index, out LightingLedEditor? editor)) continue;
                Border box = CreateKeyVisual(placement, editor);
                Canvas.SetLeft(box, placement.X);
                Canvas.SetTop(box, placement.Y);
                KeyCanvas.Children.Add(box);
                width = Math.Max(width, placement.X + placement.Width);
                height = Math.Max(height, placement.Y + placement.Height);
            }

            Width = _placements.Count == 0 ? EmptyWidth : width + 4;
            Height = _placements.Count == 0 ? EmptyHeight : height + 4;
            UpdateAllVisuals();
            UpdateAutomationName();
        }

        private Border CreateKeyVisual(KeyboardKeyPlacement placement, LightingLedEditor editor)
        {
            var label = new TextBlock
            {
                Text = placement.Label,
                // The keyboard diagram is a miniature: 11 DIP matches the shell caption size,
                // long cap labels drop to 9 DIP so they still fit inside a 40 DIP key.
                FontSize = placement.Label.Length > 5 ? 9.0 : 11.0,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(2, 0, 2, 6),
            };

            var strip = new Border
            {
                Height = 2,
                CornerRadius = new CornerRadius(1),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(4, 0, 4, 3),
            };

            var layout = new Grid();
            layout.Children.Add(label);
            layout.Children.Add(strip);

            var box = new Border
            {
                Width = placement.Width,
                Height = placement.Height,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Child = layout,
                Tag = editor,
            };
            AutomationProperties.SetName(box, editor.Name);
            box.Tapped += OnKeyTapped;

            _visuals[editor] = new KeyVisual(box, strip, label);
            return box;
        }

        // ── Painting ─────────────────────────────────────────────────────────────
        private void UpdateAllVisuals()
        {
            foreach (LightingLedEditor editor in _editors) UpdateVisual(editor);
        }

        private void UpdateVisual(LightingLedEditor editor)
        {
            if (!_visuals.TryGetValue(editor, out KeyVisual? visual)) return;

            Color keyColor = ParseHex(UseIndividualColors ? editor.ColorHex : CommonColorHex, FallbackKeyColor);
            visual.Box.Background = new SolidColorBrush(Tint(KeyBackgroundBrush, keyColor));
            visual.Box.BorderBrush = IsSelected(editor) ? KeyAccentBrush : KeyBorderBrush;
            visual.Box.BorderThickness = new Thickness(IsSelected(editor) ? 2.0 : 1.0);
            visual.Strip.Background = new SolidColorBrush(keyColor);
            visual.Label.Foreground = KeyForegroundBrush;
        }

        private bool IsSelected(LightingLedEditor editor)
            => SelectedKeys is { Count: > 0 } keys ? keys.Contains(editor) : ReferenceEquals(editor, SelectedKey);

        /// <summary>Mix 25 % of the key colour into the key body (same ratio as the WPF control).</summary>
        private Color Tint(Brush? background, Color keyColor)
        {
            Color baseColor = background is SolidColorBrush solid && solid.Color.A != 0 ? solid.Color : TintBase;
            return Color.FromArgb(0xFF,
                (byte)((baseColor.R * 3 + keyColor.R) / 4),
                (byte)((baseColor.G * 3 + keyColor.G) / 4),
                (byte)((baseColor.B * 3 + keyColor.B) / 4));
        }

        private void UpdateAutomationName()
        {
            string? name = _editors.FirstOrDefault(editor => ReferenceEquals(editor, SelectedKey))?.Name;
            AutomationProperties.SetName(this,
                string.IsNullOrEmpty(name) ? "Keyboard layout" : "Keyboard layout, selected key " + name);
        }

        private static Color ParseHex(string? hex, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(hex)) return fallback;
            string value = hex.Trim().TrimStart('#');
            if (value.Length == 3)
                value = string.Concat(value[0], value[0], value[1], value[1], value[2], value[2]);
            if (value.Length != 6) return fallback;
            try
            {
                return Color.FromArgb(0xFF,
                    Convert.ToByte(value[..2], 16),
                    Convert.ToByte(value.Substring(2, 2), 16),
                    Convert.ToByte(value.Substring(4, 2), 16));
            }
            catch { return fallback; }
        }

        // ── Input ────────────────────────────────────────────────────────────────
        private void OnKeyTapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: LightingLedEditor editor }) return;

            bool additive = IsDown(VirtualKey.Control) || IsDown(VirtualKey.Shift);
            IList<LightingLedEditor>? keys = SelectedKeys;
            if (!additive) keys?.Clear();
            if (keys is not null && !keys.Contains(editor)) keys.Add(editor);

            SelectedKey = editor;
            UpdateAllVisuals();
            UpdateAutomationName();
            e.Handled = true;
        }

        /// <summary>Arrow-key navigation between keys (parity with the WPF control).</summary>
        private void OnGotFocus(object sender, RoutedEventArgs e)
        {
            if (SelectedKey is null && _editors.Count > 0) SelectedKey = _editors[0];
        }

        private void OnKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (_placements.Count == 0) return;
            if (e.Key is not (VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down)) return;

            KeyboardKeyPlacement[] ordered = _placements.OrderBy(p => p.Y).ThenBy(p => p.X).ToArray();
            KeyboardKeyPlacement? current = ordered.FirstOrDefault(p => p.Index == SelectedKey?.Index);
            if (current is null)
            {
                SelectPlacement(ordered[0]);
                e.Handled = true;
                return;
            }

            bool vertical = e.Key is VirtualKey.Up or VirtualKey.Down;
            int sign = e.Key is VirtualKey.Left or VirtualKey.Up ? -1 : 1;
            var next = ordered
                .Select(p => (Placement: p, DeltaX: CenterX(p) - CenterX(current), DeltaY: CenterY(p) - CenterY(current)))
                .Where(p => (vertical ? p.DeltaY : p.DeltaX) * sign > 1)
                .OrderBy(p => vertical
                    ? Math.Abs(p.DeltaX) * 4 + Math.Abs(p.DeltaY)
                    : Math.Abs(p.DeltaY) * 4 + Math.Abs(p.DeltaX))
                .FirstOrDefault();

            if (next.Placement is not null) SelectPlacement(next.Placement);
            e.Handled = true;
        }

        private void SelectPlacement(KeyboardKeyPlacement placement)
        {
            LightingLedEditor? editor = _editors.FirstOrDefault(candidate => candidate.Index == placement.Index);
            if (editor is null) return;

            IList<LightingLedEditor>? keys = SelectedKeys;
            if (IsDown(VirtualKey.Shift) && SelectedKey is { } previous)
            {
                keys?.Clear();
                keys?.Add(previous);
                keys?.Add(editor);
            }
            else
            {
                keys?.Clear();
            }

            SelectedKey = editor;
            UpdateAllVisuals();
            UpdateAutomationName();
        }

        private static double CenterX(KeyboardKeyPlacement placement) => placement.X + (placement.Width / 2);
        private static double CenterY(KeyboardKeyPlacement placement) => placement.Y + (placement.Height / 2);

        private static bool IsDown(VirtualKey key)
        {
            try
            {
                return Microsoft.UI.Input.InputKeyboardSource
                    .GetKeyStateForCurrentThread(key)
                    .HasFlag(CoreVirtualKeyStates.Down);
            }
            catch
            {
                // InputKeyboardSource is unavailable outside an input thread — treat as "not held".
                return false;
            }
        }

        private sealed record KeyVisual(Border Box, Border Strip, TextBlock Label);
    }

    internal sealed record KeyboardKeyPlacement(int Index, string Name, string Label,
        double X, double Y, double Width, double Height);

    /// <summary>
    /// Port of the WPF <c>KeyboardLayoutGeometry</c> table (Controls\RgbKeyboardLayout.cs
    /// L15-107). Coordinates are in key units; the control multiplies by 40 DIP.
    /// </summary>
    internal static class KeyboardGeometry
    {
        public static IReadOnlyList<KeyboardKeyPlacement> Build(IReadOnlyList<LightingLedInfo> keys)
        {
            var source = keys.ToArray();
            bool numberPad = source.Any(key => Normalize(key.Name).StartsWith("NUM", StringComparison.Ordinal));
            var positions = new Dictionary<string, (double X, double Y, double W, double H, string Label)>(StringComparer.OrdinalIgnoreCase);

            void Add(string key, double x, double y, double width = 1, double height = 1, string? label = null) =>
                positions[key] = (x, y, width, height, label ?? key);

            void Row(string[] names, double x, double y)
            {
                foreach (string name in names) { Add(name, x, y); x++; }
            }

            Add("ESCAPE", 0, 0, label: "Esc");
            for (int i = 1; i <= 12; i++) Add($"F{i}", 1 + i + (i > 4 ? 0.5 : 0) + (i > 8 ? 0.5 : 0), 0);
            Row(["`", "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "-", "="], 0, 1.3);
            Add("BACKSPACE", 13, 1.3, 2, label: "Backspace");
            Add("TAB", 0, 2.3, 1.5, label: "Tab");
            Row(["Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P", "[", "]"], 1.5, 2.3);
            Add("\\", 13.5, 2.3, 1.5, label: "\\");
            Add("CAPSLOCK", 0, 3.3, 1.75, label: "Caps");
            Row(["A", "S", "D", "F", "G", "H", "J", "K", "L", ";", "'"], 1.75, 3.3);
            Add("ENTER", 12.75, 3.3, 2.25, label: "Enter");
            Add("LEFTSHIFT", 0, 4.3, 2.25, label: "Shift");
            Row(["Z", "X", "C", "V", "B", "N", "M", ",", ".", "/"], 2.25, 4.3);
            Add("RIGHTSHIFT", 12.25, 4.3, 2.75, label: "Shift");
            Add("LEFTCONTROL", 0, 5.3, 1.25, label: "Ctrl");
            Add("LEFTFN", 1.25, 5.3, label: "Fn");
            Add("LEFTWINDOWS", 2.25, 5.3, 1.25, label: "Win");
            Add("LEFTALT", 3.5, 5.3, 1.25, label: "Alt");
            Add("SPACE", 4.75, 5.3, 6, label: "Space");
            if (source.Any(key => Normalize(key.Name) is "LEFTSPACE" or "RIGHTSPACE"))
            {
                Add("LEFTSPACE", 4.75, 5.3, 2, label: "Space L");
                Add("SPACE", 6.75, 5.3, 2, label: "Space");
                Add("RIGHTSPACE", 8.75, 5.3, 2, label: "Space R");
            }

            Add("RIGHTALT", 10.75, 5.3, 1.25, label: "Alt");
            Add("OPTION", 12, 5.3, 1.5, label: "Menu");
            Add("RIGHTCONTROL", 13.5, 5.3, 1.5, label: "Ctrl");
            Add("PRINTSCREEN", 15.5, 0, label: "PrtSc");
            Add("PREDATORSENSE", 16.5, 0, label: "P");
            Add("PAUSE/BREAK", 17.5, 0, label: "Pause");
            Row(["INSERT", "HOME", "PAGEUP"], 15.5, 1.3);
            Row(["DELETE", "END", "PAGEDOWN"], 15.5, 2.3);
            Add("UPARROW", 16.5, 4.3, label: "\u2191");
            Add("LEFTARROW", 15.5, 5.3, label: "\u2190");
            Add("DOWNARROW", 16.5, 5.3, label: "\u2193");
            Add("RIGHTARROW", 17.5, 5.3, label: "\u2192");
            if (numberPad)
            {
                Row(["NUMLOCK", "NUM/", "NUM*", "NUM-"], 19, 1.3);
                Row(["NUM7", "NUM8", "NUM9"], 19, 2.3);
                Row(["NUM4", "NUM5", "NUM6"], 19, 3.3);
                Row(["NUM1", "NUM2", "NUM3"], 19, 4.3);
                Add("NUM+", 22, 2.3, height: 2, label: "+");
                Add("NUMENTER", 22, 4.3, height: 2, label: "Enter");
                Add("NUM0", 19, 5.3, 2, label: "0");
                Add("NUM.", 21, 5.3, label: ".");
                Row(["MUTE", "VOLUMEDOWN", "VOLUMEUP", "POWER"], 19, 0);
            }

            var result = new List<KeyboardKeyPlacement>();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            double extraX = 0, extraY = 6.6;
            foreach (LightingLedInfo key in source)
            {
                string name = Normalize(key.Name);
                if (positions.TryGetValue(name, out var position) && used.Add(name))
                {
                    result.Add(new(key.Index, key.Name, ShortLabel(position.Label),
                        position.X * 40, position.Y * 40, position.W * 40 - 4, position.H * 40 - 4));
                }
                else
                {
                    // Preserve vendor-specific keys without inventing their physical location.
                    result.Add(new(key.Index, key.Name, key.Name, extraX * 40, extraY * 40, 56, 36));
                    extraX += 1.5;
                    if (extraX > (numberPad ? 21 : 17)) { extraX = 0; extraY += 1.1; }
                }
            }

            return result;
        }

        private static string ShortLabel(string label)
        {
            string shortLabel = label switch
            {
                "INSERT" => "Ins",
                "HOME" => "Home",
                "PAGEUP" => "PgUp",
                "DELETE" => "Del",
                "END" => "End",
                "PAGEDOWN" => "PgDn",
                "NUMLOCK" => "Num",
                "MUTE" => "Mute",
                "VOLUMEDOWN" => "Vol\u2212",
                "VOLUMEUP" => "Vol+",
                "POWER" => "Power",
                _ => label,
            };
            return shortLabel.StartsWith("NUM", StringComparison.Ordinal) ? shortLabel[3..] : shortLabel;
        }

        private static string Normalize(string name)
        {
            string key = (name ?? string.Empty)
                .Replace(" (ANSI)", "", StringComparison.OrdinalIgnoreCase)
                .Replace(" ", "")
                .ToUpperInvariant();
            if (key.StartsWith("NUMBERPAD", StringComparison.Ordinal)) key = "NUM" + key[9..];
            if (key is "LEFTENTER" or "RETURN") return "ENTER";
            if (key.Length > 0 && key.All(c => c == '\\')) return "\\";
            return key;
        }
    }
}
