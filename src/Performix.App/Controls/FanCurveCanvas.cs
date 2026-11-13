// FanCurveCanvas — WinUI 3 replacement for the WPF `WpfFanCurveControl` (Controls\FanCurveControl.cs
// in PreySense.Frontend), which drew itself in `OnRender`/`DrawingContext`.
//
// WinUI 3 has no OnRender, so this takes the Canvas route (WINUI3-MIGRATION.md §3.9, §4 #9,
// approach A in the S4 brief):
//   • one Polyline  → the straight segments between the 8 points
//   • Line          → the 0/25/50/75/100 % and 20/40/60/80/100 °C grid
//   • Ellipse × 8   → the draggable points
//   • TextBlock     → the axis labels
// No Win2D: red line 11 forbids new NuGet dependencies, and the whole drawing is 1 polyline +
// ~19 straight lines + 8 dots, which a child Canvas renders identically.
//
// Segments are drawn straight on purpose: the EC cannot interpolate a curve table, so
// GHelperFanCurve.Interpolate() does piecewise-linear interpolation in software. Drawing a
// smooth spline would show an airflow the hardware never produces.
//
// Interaction: PointerPressed/Moved/Released on the control + CapturePointer (the WinUI equivalent
// of WPF's CaptureMouse) and a manual nearest-point hit test, because WinUI has no
// OnMouseLeftButtonDown override on a FrameworkElement.
//
// All geometry lives in FanCurveMath below: pure functions, no WinUI types, compiled verbatim by
// docs\s4-probe.ps1 to prove the drag/snap/clamp numbers without starting the app.

using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Predex.WinUI.Controls.Pure;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace Predex.WinUI.Controls
{
    /// <summary>
    /// Editable 8-point fan curve (x = 20..110 °C, y = 0..100 %), CPU or GPU.
    /// The control owns no curve state of its own: <see cref="Temperatures"/> / <see cref="Duties"/>
    /// are the caller's arrays and are mutated in place on drag, exactly like the WPF control did,
    /// so the ViewModel's curve arrays <em>are</em> the ones the user is dragging.
    /// </summary>
    public sealed class FanCurveCanvas : UserControl
    {
        // ── Public surface (set from XAML / code-behind) ─────────────────────────

        public static readonly DependencyProperty TemperaturesProperty = DependencyProperty.Register(
            nameof(Temperatures), typeof(int[]), typeof(FanCurveCanvas),
            new PropertyMetadata(null, OnVisualPropertyChanged));

        public static readonly DependencyProperty DutiesProperty = DependencyProperty.Register(
            nameof(Duties), typeof(int[]), typeof(FanCurveCanvas),
            new PropertyMetadata(null, OnVisualPropertyChanged));

        public static readonly DependencyProperty DeviceLabelProperty = DependencyProperty.Register(
            nameof(DeviceLabel), typeof(string), typeof(FanCurveCanvas),
            new PropertyMetadata(string.Empty, OnVisualPropertyChanged));

        public static readonly DependencyProperty SnapToGridProperty = DependencyProperty.Register(
            nameof(SnapToGrid), typeof(bool), typeof(FanCurveCanvas),
            new PropertyMetadata(true));

        public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
            nameof(AccentBrush), typeof(Brush), typeof(FanCurveCanvas),
            new PropertyMetadata(null, OnVisualPropertyChanged));

        public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
            nameof(GridBrush), typeof(Brush), typeof(FanCurveCanvas),
            new PropertyMetadata(null, OnVisualPropertyChanged));

        public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(
            nameof(LabelBrush), typeof(Brush), typeof(FanCurveCanvas),
            new PropertyMetadata(null, OnVisualPropertyChanged));

        public static readonly DependencyProperty DotFillBrushProperty = DependencyProperty.Register(
            nameof(DotFillBrush), typeof(Brush), typeof(FanCurveCanvas),
            new PropertyMetadata(null, OnVisualPropertyChanged));

        /// <summary>8 temperatures in °C (20..110, strictly increasing, ≥6 °C apart). Not copied.</summary>
        public int[]? Temperatures
        {
            get => (int[]?)GetValue(TemperaturesProperty);
            set => SetValue(TemperaturesProperty, value);
        }

        /// <summary>8 duty percentages (0..100). Not copied — the caller's array is mutated.</summary>
        public int[]? Duties
        {
            get => (int[]?)GetValue(DutiesProperty);
            set => SetValue(DutiesProperty, value);
        }

        /// <summary>"CPU Fan" / "GPU Fan" — used for the automation name and the per-dot tooltips.</summary>
        public string DeviceLabel
        {
            get => (string)GetValue(DeviceLabelProperty);
            set => SetValue(DeviceLabelProperty, value);
        }

        /// <summary>Duty snaps to the 5 % grid while dragging ("Clamp to Grid (5 %)").</summary>
        public bool SnapToGrid
        {
            get => (bool)GetValue(SnapToGridProperty);
            set => SetValue(SnapToGridProperty, value);
        }

        public Brush? AccentBrush
        {
            get => (Brush?)GetValue(AccentBrushProperty);
            set => SetValue(AccentBrushProperty, value);
        }

        public Brush? GridBrush
        {
            get => (Brush?)GetValue(GridBrushProperty);
            set => SetValue(GridBrushProperty, value);
        }

        public Brush? LabelBrush
        {
            get => (Brush?)GetValue(LabelBrushProperty);
            set => SetValue(LabelBrushProperty, value);
        }

        /// <summary>Fill behind the unselected dots, so the segment does not show through them.</summary>
        public Brush? DotFillBrush
        {
            get => (Brush?)GetValue(DotFillBrushProperty);
            set => SetValue(DotFillBrushProperty, value);
        }

        private int _selectedIndex = -1;

        /// <summary>-1 = nothing selected. Dragging a point selects it.</summary>
        public int SelectedIndex
        {
            get => _selectedIndex;
            set
            {
                if (_selectedIndex == value) return;
                _selectedIndex = value;
                UpdateGeometry();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>Raised on every accepted point move (drag and arrow keys).</summary>
        public event EventHandler? CurveEdited;

        /// <summary>Raised when a drag ends / an arrow key commits — the page's "Auto Apply" hook.</summary>
        public event EventHandler? EditCompleted;

        public event EventHandler? SelectionChanged;

        // ── Visual tree ──────────────────────────────────────────────────────────

        private static readonly int[] YGridPercent = [0, 25, 50, 75, 100];
        private static readonly int[] XGridCelsius = [20, 40, 60, 80, 100];

        // 11 px = FontSizeCaption (UI-DESIGN-RULES §3.3: 字号只有 11/12/14/16 四级).
        private const double AxisFontSize = 11;
        private const double HitTolerance = 14;   // DIP, WPF used a 16 px Manhattan distance
        private const double DotRadius = 4.5;
        private const double SelectedDotRadius = 6.5;

        private readonly Canvas _plot = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        private readonly List<Line> _gridLines = new();
        private readonly List<TextBlock> _labels = new();
        private readonly List<Ellipse> _dots = new();
        private Polyline? _polyline;

        private double _builtWidth = double.NaN;
        private double _builtHeight = double.NaN;
        private int _builtCount = -1;

        private Pointer? _dragPointer;
        private int _dragIndex = -1;

        private static readonly Brush FallbackAccent = new SolidColorBrush(Color.FromArgb(0xFF, 0x3B, 0x82, 0xF6));
        private static readonly Brush FallbackGrid = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        private static readonly Brush FallbackLabel = new SolidColorBrush(Color.FromArgb(0xFF, 0x7C, 0x7C, 0x90));
        private static readonly Brush FallbackDotFill = new SolidColorBrush(Color.FromArgb(0xFF, 0x0A, 0x0A, 0x12));

        public FanCurveCanvas()
        {
            IsTabStop = true;                                   // arrow-key editing (WPF parity)
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent); // hit-test the whole surface
            Content = _plot;

            SizeChanged += (_, _) => Redraw();
            Loaded += (_, _) => Redraw();

            PointerPressed += OnPointerPressed;
            PointerMoved += OnPointerMoved;
            PointerReleased += OnPointerReleased;
            PointerCaptureLost += OnPointerCaptureLost;
            PointerCanceled += OnPointerCanceled;
            KeyDown += OnKeyDown;
        }

        private static void OnVisualPropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        {
            var control = (FanCurveCanvas)sender;
            control._builtCount = -1;   // brushes/count may have changed → rebuild the child tree
            control.Redraw();
        }

        private int PointCount => Temperatures is { } t && Duties is { } d ? Math.Min(t.Length, d.Length) : 0;

        // ── Pointer: WPF's MouseLeftButtonDown/Move/Up + CaptureMouse ────────────

        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (!IsEnabled) return;
            var point = e.GetCurrentPoint(this);
            if (point.Properties.IsRightButtonPressed) return;

            Focus(FocusState.Pointer);

            int index = FanCurveMath.NearestPoint(
                Temperatures, Duties, point.Position.X, point.Position.Y,
                ActualWidth, ActualHeight, HitTolerance);

            if (index < 0)
            {
                SelectedIndex = -1;
                return;
            }

            _dragPointer = e.Pointer;
            _dragIndex = index;
            SelectedIndex = index;
            CapturePointer(e.Pointer);        // keeps the stream even when the pointer leaves the plot
            ApplyPoint(point.Position);
            e.Handled = true;
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_dragIndex < 0 || _dragPointer is null || e.Pointer.PointerId != _dragPointer.PointerId) return;
            ApplyPoint(e.GetCurrentPoint(this).Position);
            e.Handled = true;
        }

        private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_dragIndex < 0 || _dragPointer is null || e.Pointer.PointerId != _dragPointer.PointerId) return;
            ReleasePointerCapture(e.Pointer);
            EndDrag();
            e.Handled = true;
        }

        private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e) => EndDrag();

        private void OnPointerCanceled(object sender, PointerRoutedEventArgs e) => EndDrag();

        private void ApplyPoint(Point position)
        {
            // Mutates Temperatures/Duties in place — the caller's (ViewModel) arrays.
            FanCurveMath.ApplyDrag(
                Temperatures, Duties, _dragIndex, position.X, position.Y,
                ActualWidth, ActualHeight, SnapToGrid);

            UpdateGeometry();
            CurveEdited?.Invoke(this, EventArgs.Empty);
        }

        private void EndDrag()
        {
            if (_dragIndex < 0) return;
            _dragIndex = -1;
            _dragPointer = null;
            UpdateGeometry();
            EditCompleted?.Invoke(this, EventArgs.Empty);
        }

        // ── Keyboard (WPF parity: ←/→ select, ↑/↓ nudge, Esc deselect) ───────────

        private void OnKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (!IsEnabled) return;
            int count = PointCount;
            if (count == 0) return;

            switch (e.Key)
            {
                case VirtualKey.Escape:
                    SelectedIndex = -1;
                    break;

                case VirtualKey.Left:
                    SelectedIndex = SelectedIndex < 0 ? 0 : Math.Max(0, SelectedIndex - 1);
                    break;

                case VirtualKey.Right:
                    SelectedIndex = SelectedIndex < 0 ? 0 : Math.Min(count - 1, SelectedIndex + 1);
                    break;

                case VirtualKey.Up:
                case VirtualKey.Down:
                {
                    if (SelectedIndex < 0 || Duties is not { } duties) return;
                    int step = e.Key == VirtualKey.Up ? FanCurveMath.DutyStepPercent : -FanCurveMath.DutyStepPercent;
                    duties[SelectedIndex] = Math.Clamp(duties[SelectedIndex] + step, 0, 100);
                    UpdateGeometry();
                    CurveEdited?.Invoke(this, EventArgs.Empty);
                    EditCompleted?.Invoke(this, EventArgs.Empty);
                    break;
                }

                default:
                    return;
            }

            e.Handled = true;
        }

        // ── Drawing ──────────────────────────────────────────────────────────────

        private void Redraw()
        {
            double width = ActualWidth;
            double height = ActualHeight;
            if (width < 40 || height < 40) return;

            int count = PointCount;
            if (count == 0)
            {
                _plot.Children.Clear();
                _gridLines.Clear();
                _labels.Clear();
                _dots.Clear();
                _polyline = null;
                _builtCount = -1;
                return;
            }

            EnsureStructure(width, height, count);
            UpdateGeometry();
        }

        /// <summary>
        /// Builds the grid / labels / polyline / dots once per (size, count, brush) change.
        /// Only <see cref="UpdateGeometry"/> runs while a point is dragged, so a 120 Hz pointer
        /// stream does not allocate a fresh element tree on every move.
        /// </summary>
        private void EnsureStructure(double width, double height, int count)
        {
            if (_builtCount == count
                && Math.Abs(_builtWidth - width) < 0.5
                && Math.Abs(_builtHeight - height) < 0.5
                && _polyline is not null)
                return;

            _plot.Children.Clear();
            _gridLines.Clear();
            _labels.Clear();
            _dots.Clear();
            _polyline = null;

            Brush grid = GridBrush ?? FallbackGrid;
            Brush label = LabelBrush ?? FallbackLabel;
            Brush accent = AccentBrush ?? FallbackAccent;
            var (left, top, plotWidth, plotHeight) = FanCurveMath.Plot(width, height);

            foreach (int percent in YGridPercent)
            {
                double y = FanCurveMath.DutyToY(percent, width, height);
                var line = new Line
                {
                    X1 = left, X2 = left + plotWidth, Y1 = y, Y2 = y,
                    Stroke = grid, StrokeThickness = 1
                };
                _plot.Children.Add(line);
                _gridLines.Add(line);

                // 11 px text: label 0/50/100 only, 25/75 stay unlabelled or they collide.
                if (percent % 50 != 0) continue;
                AddLabel($"{percent}%", left - 30, y - 8, label);
            }

            foreach (int celsius in XGridCelsius)
            {
                double x = FanCurveMath.TemperatureToX(celsius, width, height);
                var line = new Line
                {
                    X1 = x, X2 = x, Y1 = top, Y2 = top + plotHeight,
                    Stroke = grid, StrokeThickness = 1
                };
                _plot.Children.Add(line);
                _gridLines.Add(line);
                AddLabel($"{celsius}°", x - 8, top + plotHeight + 4, label);
            }

            var polyline = new Polyline
            {
                Stroke = accent,
                StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round,
                Points = new PointCollection()
            };
            _plot.Children.Add(polyline);
            _polyline = polyline;

            for (int i = 0; i < count; i++)
            {
                var dot = new Ellipse { Stroke = accent, StrokeThickness = 1.5 };
                if (!string.IsNullOrEmpty(DeviceLabel))
                    AutomationProperties.SetName(dot, $"{DeviceLabel} {i + 1}");
                _plot.Children.Add(dot);
                _dots.Add(dot);
            }

            if (!string.IsNullOrEmpty(DeviceLabel))
                AutomationProperties.SetName(this, DeviceLabel);

            _builtWidth = width;
            _builtHeight = height;
            _builtCount = count;
        }

        private void AddLabel(string text, double x, double y, Brush brush)
        {
            var label = new TextBlock
            {
                Text = text,
                FontSize = AxisFontSize,
                Foreground = brush,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(label, Math.Max(0, x));
            Canvas.SetTop(label, Math.Max(0, y));
            _plot.Children.Add(label);
            _labels.Add(label);
        }

        /// <summary>Moves the polyline and the 8 dots to match the current arrays.</summary>
        private void UpdateGeometry()
        {
            if (_polyline is null || Temperatures is not { } temps || Duties is not { } duties) return;

            double width = ActualWidth;
            double height = ActualHeight;
            if (width < 40 || height < 40) return;
            if (temps.Length != _dots.Count || duties.Length != _dots.Count) return;

            Brush accent = AccentBrush ?? FallbackAccent;
            Brush dotFill = DotFillBrush ?? FallbackDotFill;
            var points = _polyline.Points;

            for (int i = 0; i < _dots.Count; i++)
            {
                double x = FanCurveMath.TemperatureToX(temps[i], width, height);
                double y = FanCurveMath.DutyToY(duties[i], width, height);

                var point = new Point(x, y);
                if (i < points.Count) points[i] = point;
                else points.Add(point);

                bool selected = i == SelectedIndex;
                double radius = selected ? SelectedDotRadius : DotRadius;
                Ellipse dot = _dots[i];
                dot.Width = radius * 2;
                dot.Height = radius * 2;
                dot.Fill = selected ? accent : dotFill;
                dot.Stroke = accent;
                Canvas.SetLeft(dot, x - radius);
                Canvas.SetTop(dot, y - radius);
                ToolTipService.SetToolTip(dot, $"{temps[i]} °C · {duties[i]} %");
            }
        }
    }
}

// >>> FanCurveMath-BEGIN (extracted verbatim by app\experiments\Predex.WinUI\docs\s4-probe.ps1)
namespace Predex.WinUI.Controls.Pure
{
    /// <summary>
    /// Pixel ↔ temperature / duty ↔ 5 % snap geometry for <c>FanCurveCanvas</c>.
    ///
    /// Deliberately free of any WinUI type: this block is compiled by a one-shot console probe
    /// (docs\s4-probe.ps1) so the drag maths can be verified without starting the app or touching
    /// fan hardware. The constants match the S4 brief: x = 20..110 °C, y = 0..100 %, 5 % snap,
    /// 8 points that stay ≥6 °C apart (GHelper FixFanCurve invariant).
    /// </summary>
    internal static class FanCurveMath
    {
        public const int MinTemperature = 20;
        public const int MaxTemperature = 110;
        public const int MinimumGapCelsius = 6;
        public const int DutyStepPercent = 5;

        // Plot box inside the control, in DIP: left gutter for "%" labels, bottom strip for "°C".
        public const double PadLeft = 34;
        public const double PadTop = 8;
        public const double PadRight = 10;
        public const double PadBottom = 22;

        /// <summary>Plot rectangle (left, top, width, height) for a control of the given size.</summary>
        public static (double Left, double Top, double Width, double Height) Plot(double width, double height) =>
            (PadLeft, PadTop,
             Math.Max(1, width - PadLeft - PadRight),
             Math.Max(1, height - PadTop - PadBottom));

        public static int ClampTemperature(int celsius) => Math.Clamp(celsius, MinTemperature, MaxTemperature);

        /// <summary>°C → x (DIP). Clamped, so out-of-range points hug the plot edge.</summary>
        public static double TemperatureToX(int celsius, double width, double height)
        {
            var (left, _, plotWidth, _) = Plot(width, height);
            double fraction = (ClampTemperature(celsius) - MinTemperature) / (double)(MaxTemperature - MinTemperature);
            return left + (plotWidth * fraction);
        }

        /// <summary>duty % → y (DIP), y grows downwards.</summary>
        public static double DutyToY(int duty, double width, double height)
        {
            var (_, top, _, plotHeight) = Plot(width, height);
            return top + (plotHeight * (1 - (Math.Clamp(duty, 0, 100) / 100.0)));
        }

        /// <summary>x (DIP) → °C, rounded to whole degrees and clamped to 20..110.</summary>
        public static int XToTemperature(double x, double width, double height)
        {
            var (left, _, plotWidth, _) = Plot(width, height);
            double fraction = Math.Clamp((x - left) / plotWidth, 0, 1);
            return ClampTemperature((int)Math.Round(MinTemperature + (fraction * (MaxTemperature - MinTemperature))));
        }

        /// <summary>y (DIP) → duty %, rounded to whole percent and clamped to 0..100.</summary>
        public static int YToDuty(double y, double width, double height)
        {
            var (_, top, _, plotHeight) = Plot(width, height);
            double fraction = 1 - Math.Clamp((y - top) / plotHeight, 0, 1);
            return Math.Clamp((int)Math.Round(fraction * 100), 0, 100);
        }

        /// <summary>Nearest 5 % step (0/5/10 … 100) — the "Clamp to Grid (5 %)" behaviour.</summary>
        public static int SnapDuty(int duty) =>
            Math.Clamp((int)Math.Round(duty / (double)DutyStepPercent, MidpointRounding.AwayFromZero) * DutyStepPercent, 0, 100);

        /// <summary>
        /// Legal °C window for one point: 20..110 overall, and ≥6 °C away from both neighbours so
        /// the 8 x values stay strictly increasing (GHelper FixFanCurve invariant).
        /// Returns the current value when the neighbours already squeezed the window shut.
        /// </summary>
        public static (int Min, int Max) TemperatureBounds(int[]? temperatures, int index)
        {
            if (temperatures is null || index < 0 || index >= temperatures.Length)
                return (MinTemperature, MaxTemperature);

            int min = index == 0 ? MinTemperature : temperatures[index - 1] + MinimumGapCelsius;
            int max = index == temperatures.Length - 1 ? MaxTemperature : temperatures[index + 1] - MinimumGapCelsius;
            min = ClampTemperature(min);
            max = ClampTemperature(max);
            if (min > max) return (temperatures[index], temperatures[index]);
            return (min, max);
        }

        /// <summary>
        /// One drag step: pixel position → (°C, duty) with both clamps applied, written back into
        /// the caller's arrays. Returns the values that were stored.
        /// </summary>
        public static (int Temp, int Duty) ApplyDrag(
            int[]? temperatures, int[]? duties, int index,
            double x, double y, double width, double height, bool snapToGrid)
        {
            if (temperatures is null || duties is null) return (-1, -1);
            if (index < 0 || index >= temperatures.Length || index >= duties.Length) return (-1, -1);

            var (min, max) = TemperatureBounds(temperatures, index);
            int temperature = Math.Clamp(XToTemperature(x, width, height), min, max);

            int duty = YToDuty(y, width, height);
            if (snapToGrid) duty = SnapDuty(duty);

            temperatures[index] = temperature;
            duties[index] = duty;
            return (temperature, duty);
        }

        /// <summary>Index of the point within <paramref name="tolerance"/> DIP of (x, y), else -1.</summary>
        public static int NearestPoint(
            int[]? temperatures, int[]? duties,
            double x, double y, double width, double height, double tolerance)
        {
            if (temperatures is null || duties is null) return -1;

            int count = Math.Min(temperatures.Length, duties.Length);
            int nearest = -1;
            double best = tolerance * tolerance;
            for (int i = 0; i < count; i++)
            {
                double dx = TemperatureToX(temperatures[i], width, height) - x;
                double dy = DutyToY(duties[i], width, height) - y;
                double distance = (dx * dx) + (dy * dy);
                if (distance > best) continue;
                best = distance;
                nearest = i;
            }
            return nearest;
        }

        /// <summary>
        /// Straight-line (piecewise-linear) duty at a temperature — mirrors
        /// <c>GHelperFanCurve.Interpolate</c>, which is what the fan scheduler actually runs.
        /// Used by the probe to prove the drawn polyline equals the software interpolation.
        /// </summary>
        public static int Interpolate(int[]? temperatures, int[]? duties, int temperature)
        {
            if (temperatures is null || duties is null) return 0;
            int count = Math.Min(temperatures.Length, duties.Length);
            if (count == 0) return 0;
            if (temperature <= temperatures[0]) return Math.Clamp(duties[0], 0, 100);
            if (temperature >= temperatures[count - 1]) return Math.Clamp(duties[count - 1], 0, 100);

            for (int i = 1; i < count; i++)
            {
                if (temperature > temperatures[i]) continue;
                int span = Math.Max(1, temperatures[i] - temperatures[i - 1]);
                double fraction = (temperature - temperatures[i - 1]) / (double)span;
                int value = (int)Math.Round(duties[i - 1] + ((duties[i] - duties[i - 1]) * fraction), MidpointRounding.AwayFromZero);
                return Math.Clamp(value, Math.Min(duties[i - 1], duties[i]), Math.Max(duties[i - 1], duties[i]));
            }
            return Math.Clamp(duties[count - 1], 0, 100);
        }
    }
}
// <<< FanCurveMath-END
