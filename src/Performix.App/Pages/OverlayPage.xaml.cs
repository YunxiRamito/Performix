// S6 — Overlay page code-behind.
//
// This page is a 1:1 port of the WPF MetricsWindow's settings surface
// (app\Fork\PreySense.Frontend\Views\MetricsWindow.xaml.cs) onto the WinUI shell's 320 DIP
// column. Everything the WPF window writes still goes through the same two entry points:
//   • PreySense.Overlay.AppConfig          (scalars: scale / opacity / show-fps / position)
//   • PreySense.Overlay.HardwareOverlayManager (start-stop + refresh + reposition)
//   • PreySense.Core.Configuration.ConfigurationService (theme preset)
// so the overlay renderer keeps listening to exactly one configuration object. No new
// write path into PreySense.Core / PreySense.Overlay was opened (both are red-lined).
//
// Why no MainViewModel property: the WPF MetricsWindow never touches the ViewModel either —
// AppConfig.LoadOverlaySlots()/SaveOverlaySlots() is the model. docs\SHELL-CONTRACT.md §2
// listed an `OverlayMetricSlots` ViewModel property that does not exist in the WPF project;
// the contract row was corrected in this stage instead of inventing a second source of truth.
//
// Read-only until the user changes something: LoadSettings() only reads config, and every
// event handler is gated by `_loading` so constructing the page never writes preysense.json.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Predex.WinUI.Services;
using PreySense.Core.Configuration;
using PreySense.Frontend.Services;
using PreySense.Overlay;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Predex.WinUI.Pages
{
    public sealed partial class OverlayPage : Page
    {
        // ── Metric catalogue (same ids/labels as MetricsWindow.xaml.cs) ─────────
        private sealed class MetricChoice
        {
            public MetricChoice(string id, string label)
            {
                Id = id;
                Label = label;
            }

            public string Id { get; }
            public string Label { get; }
        }

        private static readonly MetricChoice[] CpuChoices =
        [
            new("None", "None"),
            new("Usage", "CPU Usage (%)"),
            new("Temperature", "CPU Temperature (°C)"),
            new("Active Clock", "CPU Active Clock (MHz)"),
            new("Effective Clock", "CPU Effective Clock (MHz)"),
            new("Voltage", "CPU VID (V)"),
            new("Power", "CPU Power (W)"),
            new("Memory", "RAM Usage (GB)"),
            new("Memory Speed", "RAM Data Rate (MHz)"),
            new("Fan Speed", "CPU Fan (RPM)"),
        ];

        private static readonly MetricChoice[] GpuChoices =
        [
            new("None", "None"),
            new("Usage", "GPU Usage (%)"),
            new("Temperature", "GPU Temperature (°C)"),
            new("Hotspot", "GPU Hotspot (°C)"),
            new("Memory Temperature", "GPU Memory Junction (°C)"),
            new("Clock", "GPU Clock (MHz)"),
            new("Voltage", "GPU Voltage (V)"),
            new("Power", "GPU Power (W)"),
            new("Power Limit", "GPU Enforced Power Limit (W)"),
            new("PerfCap Reason", "GPU PerfCap Reason"),
            new("Memory", "VRAM Usage (GB)"),
            new("Memory Speed", "VRAM Data Rate (MHz)"),
            new("Fan Speed", "GPU Fan (RPM)"),
        ];

        private readonly List<OverlaySlotConfig> _slots = new();
        private bool _loading = true;

        public OverlayPage()
        {
            InitializeComponent();
            try
            {
                LoadSettings();
                BuildSlots();
            }
            catch (Exception ex)
            {
                ShowError($"overlay settings could not be read: {ex.Message}");
                ShellLog.Write("overlay", $"load FAILED: {ex}");
            }
            finally
            {
                _loading = false;
            }

            ShellLog.Write("overlay",
                $"page ready enabled={SafeIsOverlay()} theme={SafeTheme()} slots={_slots.Count} " +
                $"scale={AppConfig.Get("overlay_scale_percent", 100)}% " +
                $"bg={AppConfig.Get("overlay_alpha", 100)}% text={AppConfig.Get("overlay_text_alpha", 100)}% " +
                $"pos={AppConfig.GetString("overlay_pos_horizontal", "Left")}/{AppConfig.GetString("overlay_pos_vertical", "Top")} " +
                $"showFps={AppConfig.Get("overlay_show_fps", 1) == 1}");
        }

        private static bool SafeIsOverlay()
        {
            try { return AppConfig.IsOverlay(); } catch { return false; }
        }

        private static string SafeTheme()
        {
            try { return ConfigurationService.Instance.GetCurrent().Overlay.Theme.ToString(); }
            catch { return "unknown"; }
        }

        // ── Load: read config into the controls (no writes) ─────────────────────
        private void LoadSettings()
        {
            _loading = true;
            try
            {
                OverlayConfig overlay = ConfigurationService.Instance.GetCurrent().Overlay;

                ChkEnableOverlay.IsOn = overlay.Enabled;
                CmbOverlayTheme.SelectedIndex = Math.Clamp((int)overlay.Theme, 0, 1);
                ChkShowFps.IsOn = AppConfig.Get("overlay_show_fps", 1) == 1;

                int scale = Math.Clamp(AppConfig.Get("overlay_scale_percent", 100) / 5 * 5, 50, 200);
                int background = NormalizeSavedOpacity(AppConfig.Get("overlay_alpha", 100));
                int text = NormalizeSavedOpacity(AppConfig.Get("overlay_text_alpha", 100));

                SldHudScale.Value = scale;
                SldOpacity.Value = background;
                SldTextOpacity.Value = text;
                LabelHudScale.Text = $"{scale}%";
                LabelOpacity.Text = $"{background}%";
                LabelTextOpacity.Text = $"{text}%";

                CmbHorizontalPos.SelectedIndex =
                    Math.Max(0, Array.IndexOf(new[] { "Left", "Center", "Right" },
                        AppConfig.GetString("overlay_pos_horizontal", "Left")));
                CmbVerticalPos.SelectedIndex =
                    Math.Max(0, Array.IndexOf(new[] { "Top", "Center", "Bottom" },
                        AppConfig.GetString("overlay_pos_vertical", "Top")));

                ErrorText.Visibility = Visibility.Collapsed;
            }
            finally
            {
                _loading = false;
            }
        }

        /// <summary>Older stores keep a 0–255 alpha byte; current ones use percent (WPF parity).</summary>
        private static int NormalizeSavedOpacity(int savedOpacity)
        {
            double percent = savedOpacity > 100 ? savedOpacity / 255.0 * 100 : savedOpacity;
            return Math.Clamp((int)Math.Round(percent / 10) * 10, 0, 100);
        }

        // ── Slot list ────────────────────────────────────────────────────────────
        private void BuildSlots()
        {
            SlotsPanel.Children.Clear();

            // Work on a copy: mutating the live OverlayConfig.Slots list would change the
            // configuration object before SaveOverlaySlots() persisted it (the WPF window
            // mutates the live list; this keeps read and write clearly separated).
            _slots.Clear();
            _slots.AddRange((AppConfig.LoadOverlaySlots() ?? new List<OverlaySlotConfig>()).Select(slot => new OverlaySlotConfig
            {
                CpuMetric = slot.CpuMetric,
                GpuMetric = slot.GpuMetric,
                ShowGraph = slot.ShowGraph,
            }));

            bool empty = _slots.Count == 0;
            SlotEmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            if (empty) return;

            for (int i = 0; i < _slots.Count; i++)
            {
                if (i > 0) SlotsPanel.Children.Add(Hairline());
                SlotsPanel.Children.Add(BuildSlot(i));
            }
        }

        private void AddSlot_Click(object sender, RoutedEventArgs e)
        {
            _slots.Add(new OverlaySlotConfig { CpuMetric = "None", GpuMetric = "None", ShowGraph = false });
            SaveSlots();
            BuildSlots();
        }

        private FrameworkElement BuildSlot(int index)
        {
            OverlaySlotConfig slot = _slots[index];
            var block = new StackPanel { Spacing = 4, Margin = new Thickness(0, 8, 0, 8) };

            // ── header: "Slot #n" + graph switch + remove ────────────────────────
            var header = new Grid { ColumnSpacing = 6 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = new TextBlock
            {
                Text = LocalizationService.Format("Slot #{0}", index + 1),
                FontSize = 11,
                Foreground = Brush("TextSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            header.Children.Add(title);

            var graph = new ToggleSwitch
            {
                IsOn = slot.ShowGraph,
                MinWidth = 0,
                FontSize = 11,
                Foreground = Brush("TextSecondaryBrush"),
                OnContent = Loc.T("Graph"),
                OffContent = Loc.T("Graph"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetAutomationId(graph, $"ChkSlotGraph{index + 1}");
            AutomationProperties.SetName(graph, Loc.T("Graph"));
            graph.Toggled += (_, _) =>
            {
                if (_loading) return;
                slot.ShowGraph = graph.IsOn;
                SaveSlots();
            };
            Grid.SetColumn(graph, 1);
            header.Children.Add(graph);

            var remove = new Button
            {
                Content = "\u2212", // minus sign — same glyph the WPF window uses for "remove"
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                FontSize = 14,
                CornerRadius = new CornerRadius(4),
                VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetAutomationId(remove, $"BtnRemoveSlot{index + 1}");
            AutomationProperties.SetName(remove, Loc.T("Remove slot"));
            remove.Click += (_, _) =>
            {
                _slots.RemoveAt(index);
                SaveSlots();
                BuildSlots();
            };
            Grid.SetColumn(remove, 2);
            header.Children.Add(remove);

            block.Children.Add(header);
            block.Children.Add(MetricRow("CPU", CpuChoices, slot.CpuMetric, index, isCpu: true));
            block.Children.Add(MetricRow("GPU", GpuChoices, slot.GpuMetric, index, isCpu: false));
            return block;
        }

        /// <summary>One label + ComboBox row ("CPU"/"GPU" are category tags, not translated).</summary>
        private FrameworkElement MetricRow(string tag, MetricChoice[] choices, string selectedId,
            int slotIndex, bool isCpu)
        {
            var row = new Grid { Height = 30, ColumnSpacing = 6 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            row.Children.Add(new TextBlock
            {
                Text = tag,
                FontSize = 11,
                Foreground = Brush("TextMutedBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            });

            var combo = new ComboBox
            {
                Height = 30,
                CornerRadius = new CornerRadius(4),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
            };
            int selectedIndex = 0;
            for (int i = 0; i < choices.Length; i++)
            {
                combo.Items.Add(new ComboBoxItem { Content = Loc.T(choices[i].Label), Tag = choices[i].Id });
                if (choices[i].Id == selectedId) selectedIndex = i;
            }
            combo.SelectedIndex = selectedIndex;
            AutomationProperties.SetAutomationId(combo, $"CmbSlot{slotIndex + 1}{(isCpu ? "Cpu" : "Gpu")}");
            AutomationProperties.SetName(combo, $"{(isCpu ? "CPU" : "GPU")} {Loc.T("Overlay Metric Slots")} #{slotIndex + 1}");
            combo.SelectionChanged += (_, _) =>
            {
                if (_loading) return;
                if (combo.SelectedItem is not ComboBoxItem item) return;
                string id = item.Tag as string ?? "None";
                if (isCpu) _slots[slotIndex].CpuMetric = id;
                else _slots[slotIndex].GpuMetric = id;
                SaveSlots();
            };

            Grid.SetColumn(combo, 1);
            row.Children.Add(combo);
            return row;
        }

        private void SaveSlots()
        {
            try
            {
                AppConfig.SaveOverlaySlots(_slots);
                HardwareOverlayManager.RefreshDisplayFlags();
                ShellLog.Write("overlay",
                    "slots saved: " + string.Join(", ",
                        _slots.Select((s, i) => $"#{i + 1} {s.CpuMetric}/{s.GpuMetric}{(s.ShowGraph ? " +graph" : "")}")));
                ErrorText.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                ShowError($"could not save overlay slots: {ex.Message}");
            }
        }

        // ── Display card handlers ───────────────────────────────────────────────
        private void EnableOverlay_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            try
            {
                if (ChkEnableOverlay.IsOn) HardwareOverlayManager.Start();
                else HardwareOverlayManager.Stop();
                ShellLog.Write("overlay", $"enabled={ChkEnableOverlay.IsOn}");
                ErrorText.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                ShowError($"could not toggle the overlay: {ex.Message}");
                LoadSettings();
            }
        }

        private void OverlayTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || CmbOverlayTheme.SelectedIndex < 0) return;
            var theme = (OverlayTheme)CmbOverlayTheme.SelectedIndex;
            // Re-selecting the same theme must not wipe hand-edited colours/opacity (WPF parity).
            try
            {
                if (theme == ConfigurationService.Instance.GetCurrent().Overlay.Theme) return;
                ConfigurationService.Instance.Update(config => config.Overlay.ApplyTheme(theme));
                LoadSettings();
                HardwareOverlayManager.RefreshDisplayFlags();
                ShellLog.Write("overlay", $"theme={theme}");
                ErrorText.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                ShowError($"could not apply the overlay theme: {ex.Message}");
                LoadSettings();
            }
        }

        private void HudScale_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (LabelHudScale is null) return;
            int value = Math.Clamp((int)Math.Round(e.NewValue / 5.0) * 5, 50, 200);
            if (Math.Abs(SldHudScale.Value - value) > 0.01)
            {
                SldHudScale.Value = value; // snap first; the second pass persists
                return;
            }
            LabelHudScale.Text = $"{value}%";
            if (_loading) return;
            TrySave(() => AppConfig.Set("overlay_scale_percent", value), "overlay scale");
        }

        private void Opacity_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (LabelOpacity is null) return;
            int value = Math.Clamp((int)Math.Round(e.NewValue / 10.0) * 10, 0, 100);
            if (Math.Abs(SldOpacity.Value - value) > 0.01)
            {
                SldOpacity.Value = value;
                return;
            }
            LabelOpacity.Text = $"{value}%";
            if (_loading) return;
            TrySave(() => AppConfig.Set("overlay_alpha", value), "overlay background opacity");
        }

        private void TextOpacity_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (LabelTextOpacity is null) return;
            int value = Math.Clamp((int)Math.Round(e.NewValue / 10.0) * 10, 0, 100);
            if (Math.Abs(SldTextOpacity.Value - value) > 0.01)
            {
                SldTextOpacity.Value = value;
                return;
            }
            LabelTextOpacity.Text = $"{value}%";
            if (_loading) return;
            TrySave(() => AppConfig.Set("overlay_text_alpha", value), "overlay text opacity");
        }

        private void TrySave(Action write, string what)
        {
            try
            {
                write();
                HardwareOverlayManager.RefreshDisplayFlags();
                ErrorText.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                ShowError($"could not save {what}: {ex.Message}");
            }
        }

        // ── Position card handlers ───────────────────────────────────────────────
        private void Position_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            if (CmbHorizontalPos.SelectedItem is not ComboBoxItem horizontal ||
                CmbVerticalPos.SelectedItem is not ComboBoxItem vertical) return;
            string h = horizontal.Tag as string ?? "Left";
            string v = vertical.Tag as string ?? "Top";
            try
            {
                AppConfig.Set("overlay_pos_horizontal", h);
                AppConfig.Set("overlay_pos_vertical", v);
                HardwareOverlayManager.Reposition(h, v);
                ShellLog.Write("overlay", $"position={h}/{v}");
                ErrorText.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                ShowError($"could not move the overlay: {ex.Message}");
            }
        }

        private void ShowFps_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            TrySave(() => AppConfig.Set("overlay_show_fps", ChkShowFps.IsOn ? 1 : 0), "overlay FPS");
        }

        // ── Helpers ──────────────────────────────────────────────────────────────
        private void ShowError(string message)
        {
            ErrorText.Text = message;
            ErrorText.Visibility = Visibility.Visible;
            ShellLog.Write("overlay", $"ERROR {message}");
        }

        private static Border Hairline() => new()
        {
            Height = 1,
            Margin = new Thickness(0, 0, 0, 0),
            Background = Brush("BorderSubtleBrush"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        /// <summary>Resolves a brush from the app resources; a missing token must not throw.</summary>
        private static Brush Brush(string key) =>
            Application.Current.Resources.TryGetValue(key, out object? value) && value is Brush brush
                ? brush
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }
}
