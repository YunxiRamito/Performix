using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PreySense.Frontend.Controls;
using PreySense.Frontend.ViewModels;
using PreySense.Mode;

namespace PreySense.Frontend.Views;

public partial class FanCurveWindow : Window
{
    private static readonly byte[] Modes = [6, 0, 1, 4, 5];
    private readonly Dictionary<byte, FanCurveProfile> _drafts = new();
    private readonly List<int> _visibleStageIndices = new();
    private int[] _dutyValues = Array.Empty<int>();
    private int[] _temperatureLabels = Array.Empty<int>();
    private int _leadInDuty;
    private byte _mode = 1;
    private bool _gpu, _loading, _applying;
    private FanCurveProfile _profile = new();
    private readonly System.Windows.Threading.DispatcherTimer _liveRpmTimer = new()
    {
        Interval = TimeSpan.FromSeconds(1)
    };
    private MainViewModel? Vm => DataContext as MainViewModel;

    public FanCurveWindow()
    {
        InitializeComponent();
        Curve.Edited += (_, _) => OnCurveEdited();
        Curve.SelectionChanged += (_, _) => SyncSelectedStage();
        UpdateEditor();
        _liveRpmTimer.Tick += (_, _) => UpdateLiveRpm();
        Loaded += (_, _) =>
        {
            if (Vm == null) return;
            _loading = true;
            // Select the mode the firmware actually reports (pipe cmd 33), not the optimistic UI state.
            _mode = Vm.TryGetPowerMode(out byte actualMode) ? actualMode : Vm.ActivePowerMode;
            ProfileRow.SelectedItem = ProfileManager.ModeToProfileName(_mode);
            _loading = false;
            LoadProfile();
            UpdateEditingHint();
            UpdateLiveRpm();
            _liveRpmTimer.Start();
        };
        Closed += (_, _) => _liveRpmTimer.Stop();
        ProfileRow.AddHandler(ComboBox.SelectionChangedEvent, new SelectionChangedEventHandler(Profile_Changed));
    }

    /// <summary>Shows the RPM the EC really reports (pipe ids 2/6), not a duty×6400/100 estimate.</summary>
    private void UpdateLiveRpm()
    {
        if (LiveRpmText == null) return;
        var snapshot = PreySense.Core.Services.TelemetryCoordinator.CurrentSnapshot;
        string text = _gpu
            ? (snapshot.GpuRpm is > 0 and var g ? $"{g} RPM" : "N/A")
            : (snapshot.CpuRpm is > 0 and var c ? $"{c} RPM" : "N/A");
        LiveRpmText.Text = text;
    }

    private void SchedulerOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || CustomOption?.IsChecked != true) return;
        SaveDraft();
    }

    private void PredictiveLead_Committed(object? sender, EventArgs e)
    {
        if (_loading || CustomOption?.IsChecked != true) return;
        SaveDraft();
    }

    private void SaveDraft()
    {
        _profile.ControlMode = MaxOption.IsChecked == true ? FanControlMode.Max
            : CustomOption.IsChecked == true ? FanControlMode.Custom
            : CurveOption.IsChecked == true ? FanControlMode.Curve
            : FanControlMode.Auto;
        _profile.RampUpSeconds = Math.Clamp((int)Math.Round(DelayRow.Value), 0, 5);
        _profile.Scheduler = new FanSchedulerSettings(
            PredictiveEnabled: PredictiveOption?.IsChecked == true,
            PredictiveLoadPercent: _profile.Scheduler.PredictiveLoadPercent,
            PredictiveLeadCelsius: double.IsFinite(PredictiveLeadRow?.Value ?? double.NaN) ? (int)Math.Round(PredictiveLeadRow!.Value) : 4,
            ClosedLoopEnabled: ClosedLoopOption?.IsChecked == true,
            ClosedLoopTrimPercent: _profile.Scheduler.ClosedLoopTrimPercent,
            ClosedLoopIntervalMs: _profile.Scheduler.ClosedLoopIntervalMs);
        if (CurvePointsBox is not null)
        {
            var points = ParseCurvePoints(CurvePointsBox.Text);
            if (points.Count == GHelperFanCurve.PointCount)
            {
                var temps = points.Select(p => p.TempC).ToArray();
                var duties = points.Select(p => p.DutyPercent).ToArray();
                _profile.Curve.CpuCurve.Temps = (int[])temps.Clone();
                _profile.Curve.CpuCurve.Duties = (int[])duties.Clone();
                _profile.Curve.GpuCurve.GpuTemps = (int[])temps.Clone();
                _profile.Curve.GpuCurve.GpuDuties = (int[])duties.Clone();
            }
        }
        if (double.IsFinite(CurveRampUpRow?.Value ?? double.NaN))
            _profile.Curve.RampUpPercentPerSecond = (int)Math.Round(CurveRampUpRow!.Value);
        if (double.IsFinite(CurveRampDownRow?.Value ?? double.NaN))
            _profile.Curve.RampDownPercentPerSecond = (int)Math.Round(CurveRampDownRow!.Value);
        if (double.IsFinite(CurveHysteresisRow?.Value ?? double.NaN))
            _profile.Curve.HysteresisCelsius = (int)Math.Round(CurveHysteresisRow!.Value);
        _profile.Curve.Normalize();
        _drafts[_mode] = _profile;
    }

    private void CurveField_Committed(object? sender, EventArgs e)
    {
        if (_loading || CurveOption?.IsChecked != true) return;
        SaveDraft();
        RedrawCurves();
    }

    // ---- Drag-point canvas (GHelper-style 8-point linear editor) ---------------------

    private Canvas? _dragCanvas;
    private int _dragPoint = -1;

    internal void RedrawCurves()
    {
        if (CpuCurveCanvas is null || GpuCurveCanvas is null) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            DrawCurve(CpuCurveCanvas, _profile.Curve.CpuCurve, gpu: false);
            DrawCurve(GpuCurveCanvas, _profile.Curve.GpuCurve, gpu: true);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void DrawCurve(Canvas canvas, GHelperFanCurve curve, bool gpu)
    {
        canvas.Children.Clear();
        double width = canvas.ActualWidth > 10 ? canvas.ActualWidth : 470;
        double height = canvas.ActualHeight > 10 ? canvas.ActualHeight : 180;
        var grid = (Brush)(TryFindResource("ControlBorderBrush") ?? Brushes.Gray);
        foreach (int duty in new[] { 0, 50, 100 })
        {
            double y = height - duty / 100.0 * height;
            canvas.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = 0, X2 = width, Y1 = y, Y2 = y,
                Stroke = grid, StrokeThickness = 0.6, Opacity = 0.5
            });
        }
        var points = CurvePoints(canvas, curve, gpu);
        var line = new System.Windows.Shapes.Polyline
        {
            Stroke = (Brush)(TryFindResource("AccentBrush") ?? Brushes.DodgerBlue),
            StrokeThickness = 2
        };
        foreach (var point in points) line.Points.Add(point);
        canvas.Children.Add(line);
        foreach (var point in points)
        {
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 9, Height = 9,
                Fill = (Brush)(TryFindResource("AccentBrush") ?? Brushes.DodgerBlue),
                Stroke = Brushes.White, StrokeThickness = 1
            };
            Canvas.SetLeft(dot, point.X - 4.5);
            Canvas.SetTop(dot, point.Y - 4.5);
            canvas.Children.Add(dot);
        }
    }

    private static List<System.Windows.Point> CurvePoints(Canvas canvas, GHelperFanCurve curve, bool gpu)
    {
        double width = canvas.ActualWidth > 10 ? canvas.ActualWidth : 470;
        double height = canvas.ActualHeight > 10 ? canvas.ActualHeight : 180;
        int[] temps = gpu ? curve.GpuTemps : curve.Temps;
        int[] duties = gpu ? curve.GpuDuties : curve.Duties;
        var list = new List<System.Windows.Point>(GHelperFanCurve.PointCount);
        for (int i = 0; i < GHelperFanCurve.PointCount; i++)
        {
            double x = (Math.Clamp(temps[i], 20, 110) - 20) / 90.0 * width;
            double y = height - Math.Clamp(duties[i], 0, 100) / 100.0 * height;
            list.Add(new System.Windows.Point(x, y));
        }
        return list;
    }

    private void CurveCanvas_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not Canvas canvas || CurveOption?.IsChecked != true || _applying) return;
        bool gpu = (string?)canvas.Tag == "gpu";
        var curve = gpu ? _profile.Curve.GpuCurve : _profile.Curve.CpuCurve;
        var position = e.GetPosition(canvas);
        var points = CurvePoints(canvas, curve, gpu);
        int nearest = -1;
        double best = 16;
        for (int i = 0; i < points.Count; i++)
        {
            double distance = Math.Abs(points[i].X - position.X) + Math.Abs(points[i].Y - position.Y);
            if (distance < best) { best = distance; nearest = i; }
        }
        if (nearest < 0) return;
        _dragCanvas = canvas;
        _dragPoint = nearest;
        canvas.CaptureMouse();
        UpdateCurveFromPosition(canvas, position);
    }

    private void CurveCanvas_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragCanvas is null || !ReferenceEquals(sender, _dragCanvas) || _dragPoint < 0) return;
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
        UpdateCurveFromPosition(_dragCanvas, e.GetPosition(_dragCanvas));
    }

    private void CurveCanvas_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_dragCanvas is null) return;
        _dragCanvas.ReleaseMouseCapture();
        _dragCanvas = null;
        _dragPoint = -1;
        SaveDraft();
        RedrawCurves();
        if (AutoApplyOption?.IsChecked == true) Apply_Click(this, new RoutedEventArgs());
    }

    private void UpdateCurveFromPosition(Canvas canvas, System.Windows.Point position)
    {
        bool gpu = (string?)canvas.Tag == "gpu";
        var curve = gpu ? _profile.Curve.GpuCurve : _profile.Curve.CpuCurve;
        double width = canvas.ActualWidth > 10 ? canvas.ActualWidth : 470;
        double height = canvas.ActualHeight > 10 ? canvas.ActualHeight : 180;

        int[] temps = gpu ? curve.GpuTemps : curve.Temps;
        int[] duties = gpu ? curve.GpuDuties : curve.Duties;
        int index = _dragPoint;
        if (index < 0 || index >= GHelperFanCurve.PointCount) return;

        double rawTemp = 20 + Math.Clamp(position.X / width, 0, 1) * 90;
        double rawDuty = (1 - Math.Clamp(position.Y / height, 0, 1)) * 100;
        int temp = (int)Math.Round(rawTemp);
        int duty = (int)Math.Round(rawDuty);
        if (ClampOption?.IsChecked == true) duty = (int)Math.Round(duty / 5.0) * 5;

        // Keep the GHelper invariant: strictly increasing, ≥6 °C apart.
        int minTemp = index == 0 ? 20 : temps[index - 1] + 6;
        int maxTemp = index == GHelperFanCurve.PointCount - 1 ? 110 : temps[index + 1] - 6;
        temps[index] = Math.Clamp(temp, Math.Min(minTemp, maxTemp), Math.Max(minTemp, maxTemp));
        duties[index] = Math.Clamp(duty, 0, 100);
        if (gpu) { curve.GpuTemps = temps; curve.GpuDuties = duties; }
        else { curve.Temps = temps; curve.Duties = duties; }

        if (CurvePointsBox is not null) CurvePointsBox.Text = FormatCurvePoints(curve);
        DrawCurve(canvas, curve, gpu);
    }

    private static string FormatCurvePoints(GHelperFanCurve curve) =>
        string.Join(Environment.NewLine, Enumerable.Range(0, GHelperFanCurve.PointCount)
            .Select(i => $"{curve.Temps[i]}={curve.Duties[i]}"));

    /// <summary>Parses the 8 "temp=duty" rows; exactly 8 points are required (GHelper table).</summary>
    private static List<FanCurvePoint> ParseCurvePoints(string? text)
    {
        var points = new List<FanCurvePoint>();
        if (string.IsNullOrWhiteSpace(text)) return points;
        foreach (string raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            int separator = line.IndexOf('=');
            if (separator <= 0) separator = line.IndexOf(',');
            if (separator <= 0) continue;
            if (!int.TryParse(line[..separator].Trim(), out int temp)) continue;
            if (!int.TryParse(line[(separator + 1)..].Trim(), out int duty)) continue;
            if (temp is <= 0 or > 125) continue;
            points.Add(new FanCurvePoint(temp, Math.Clamp(duty, 0, 100)));
        }
        return points.OrderBy(p => p.TempC).GroupBy(p => p.TempC).Select(g => g.Last()).ToList();
    }

    private void LoadProfile()
    {
        _loading = true;
        _profile = _drafts.TryGetValue(_mode, out var draft) ? draft : Vm?.LoadFanProfile(_mode) ?? FanCurveProfile.CreateDefault(_mode);
        AutoOption.IsChecked = _profile.ControlMode == FanControlMode.Auto;
        MaxOption.IsChecked = _profile.ControlMode == FanControlMode.Max;
        CustomOption.IsChecked = _profile.ControlMode == FanControlMode.Custom;
        CurveOption.IsChecked = _profile.ControlMode == FanControlMode.Curve;
        DelayRow.Value = Math.Clamp(_profile.RampUpSeconds, 0, 5);
        SyncDelayValueText();
        if (CurvePointsBox is not null)
        {
            CurvePointsBox.Text = FormatCurvePoints(_profile.Curve.CpuCurve);
            CurveRampUpRow.Value = _profile.Curve.RampUpPercentPerSecond;
            CurveRampDownRow.Value = _profile.Curve.RampDownPercentPerSecond;
            CurveHysteresisRow.Value = _profile.Curve.HysteresisCelsius;
        }
        PredictiveOption.IsChecked = _profile.Scheduler.PredictiveEnabled;
        ClosedLoopOption.IsChecked = _profile.Scheduler.ClosedLoopEnabled;
        PredictiveLeadRow.Value = _profile.Scheduler.PredictiveLeadCelsius;
        if (BindingsBox is not null)
            BindingsBox.Text = Vm is null ? string.Empty : FormatBindings(Vm.GetForegroundFanBindings());
        Curve.SelectedIndex = -1;
        _loading = false;
        UpdateEditor();
        SyncCurve();
    }

    private static string FormatBindings(System.Collections.Generic.IReadOnlyDictionary<string, byte> bindings) =>
        string.Join(Environment.NewLine, bindings
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{pair.Key}={ProfileManager.ModeToProfileName(pair.Value)}"));

    private static Dictionary<string, byte> ParseBindings(string? text)
    {
        var result = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (string raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int separator = line.IndexOf('=');
            if (separator <= 0) continue;
            string process = line[..separator].Trim();
            string modeName = line[(separator + 1)..].Trim();
            byte mode = modeName switch
            {
                "Eco" or "eco" => 6,
                "Quiet" or "quiet" => 0,
                "Balanced" or "balanced" => 1,
                "Performance" or "performance" => 4,
                "Turbo" or "turbo" => 5,
                _ when byte.TryParse(modeName, out byte parsed) && ProfileManager.IsSupportedMode(parsed) => parsed,
                _ => 0xFF
            };
            if (mode != 0xFF && process.Length > 0) result[process] = mode;
        }
        return result;
    }

    private void DelayRow_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || DelayRow == null) return;
        SyncDelayValueText();
        if (CustomOption.IsChecked == true)
            SaveDraft();
    }

    private void SyncDelayValueText()
    {
        int seconds = Math.Clamp((int)Math.Round(DelayRow.Value), 0, 5);
        DelayRow.ValueText = $"{seconds} s";
    }

    internal static (int[] StageIndices, int[] Temperatures, int[] Duties, int LeadInDuty)
        BuildGraph(FanStage[] stages, bool gpu)
    {
        // Reachable via this fan's rise. Disabled (127) rise stops the chain.
        var candidates = new List<(int Stage, int EntryC, int Duty)>();
        var reachable = new List<int>();
        for (int i = 0; i < stages.Length; i++)
        {
            if (i > 0 && !stages[i - 1].HasActiveRise(gpu))
                break;
            reachable.Add(i);
        }

        int leadInDuty = reachable.Count > 0 ? stages[0].DutyPercent(gpu) : 0;
        for (int v = 0; v < reachable.Count; v++)
        {
            int stageIndex = reachable[v];
            if (stageIndex == 0)
                continue;

            var stage = stages[stageIndex];
            int duty = stage.DutyPercent(gpu);
            if (!stages[stageIndex - 1].HasActiveRise(gpu))
                continue;
            int entryC = Math.Clamp(
                stages[stageIndex - 1].RiseCelsius(gpu),
                WpfFanCurveControl.MinCelsius,
                WpfFanCurveControl.MaxCelsius);

            // Unique X: bump past a colliding previous entry instead of overlaying.
            if (candidates.Count > 0 && entryC <= candidates[^1].EntryC)
                entryC = Math.Min(WpfFanCurveControl.MaxCelsius, candidates[^1].EntryC + 1);
            if (entryC > WpfFanCurveControl.MaxCelsius)
                continue;
            candidates.Add((stageIndex, entryC, duty));
        }

        return (candidates.Select(point => point.Stage).ToArray(),
            candidates.Select(point => point.EntryC).ToArray(),
            candidates.Select(point => point.Duty).ToArray(), leadInDuty);
    }

    private void RebuildVisibleStages()
    {
        var graph = BuildGraph(_profile.Stages, _gpu);
        _visibleStageIndices.Clear();
        _visibleStageIndices.AddRange(graph.StageIndices);
        _temperatureLabels = graph.Temperatures;
        _dutyValues = graph.Duties;
        _leadInDuty = graph.LeadInDuty;
    }

    private void SyncCurve()
    {
        int previousStage = SelectedStageIndex;
        RebuildVisibleStages();
        Curve.Temperatures = _temperatureLabels;
        Curve.Values = _dutyValues;
        Curve.LeadInDuty = _leadInDuty;
        int visible = previousStage < 0 ? -1 : _visibleStageIndices.IndexOf(previousStage);
        Curve.SelectedIndex = visible;
        Curve.InvalidateVisual();
        SyncSelectedStage();
    }

    private int SelectedStageIndex
    {
        get
        {
            int visible = Curve.SelectedIndex;
            return visible >= 0 && visible < _visibleStageIndices.Count
                ? _visibleStageIndices[visible]
                : -1;
        }
    }

    private void OnCurveEdited()
    {
        if (_loading || CustomOption.IsChecked != true || Curve.SelectedIndex < 0) return;
        int stageIndex = SelectedStageIndex;
        if (stageIndex < 0) return;

        int visible = Curve.SelectedIndex;
        if (visible < _temperatureLabels.Length)
        {
            WpfFanCurveControl.SetTemperature(_temperatureLabels, visible, _temperatureLabels[visible]);
            FanStage.ApplyEntryTemperatures(_profile.Stages, _visibleStageIndices, _temperatureLabels, _gpu);
        }

        int duty = Math.Clamp(_dutyValues[visible], 0, 100);
        _profile.Stages[stageIndex].SetDuty(_gpu, duty);
        _profile.Stages[stageIndex].Normalize();
        _dutyValues[visible] = _profile.Stages[stageIndex].DutyPercent(_gpu);
        SyncSelectedStage();
        SaveDraft();
    }

    private FanStage? SelectedStage
    {
        get
        {
            int index = SelectedStageIndex;
            return index >= 0 ? _profile.Stages[index] : null;
        }
    }

    private void SyncSelectedStage()
    {
        if (SelectedStage is not FanStage stage)
        {
            RowTemp.IsEnabled = false;
            RowDuty.IsEnabled = false;
            RowRpm.IsEnabled = false;
            RowTemp.Title = "Temp";
            RowDuty.Title = "Duty";
            RowRpm.Title = "RPM";
            return;
        }

        bool isCustom = CustomOption.IsChecked == true;
        _loading = true;
        RowTemp.IsEnabled = isCustom;
        RowDuty.IsEnabled = isCustom;
        RowRpm.IsEnabled = isCustom;
        string fan = _gpu ? "GPU" : "CPU";
        int visible = Curve.SelectedIndex;
        int celsius = visible >= 0 && visible < _temperatureLabels.Length
            ? _temperatureLabels[visible]
            : WpfFanCurveControl.MinCelsius;
        RowTemp.Title = $"{fan} Temp";
        RowDuty.Title = $"{fan} Duty";
        RowRpm.Title = $"{fan} RPM";
        var (minDuty, maxDuty) = WpfFanCurveControl.DutyBounds(_dutyValues, visible);
        RowDuty.Minimum = minDuty;
        RowDuty.Maximum = maxDuty;
        var (minTemp, maxTemp) = WpfFanCurveControl.TemperatureBounds(_temperatureLabels, visible);
        RowTemp.Minimum = minTemp;
        RowTemp.Maximum = maxTemp;
        RowTemp.Value = celsius;
        int duty = stage.DutyPercent(_gpu);
        int maxRpm = _gpu ? FanStage.GpuMaxRpm : FanStage.CpuMaxRpm;
        RowDuty.Value = duty;
        // E1: show the real measured-curve RPM, not duty*6400/100.
        RowRpm.Maximum = FanRpmCalibration.MaxRpm;
        RowRpm.Value = FanRpmCalibration.DutyToRpm(duty);
        _loading = false;
        UpdateEditor();
    }

    private void StageField_Committed(object? sender, EventArgs e)
    {
        if (_loading || CustomOption.IsChecked != true || SelectedStage is not FanStage stage) return;
        int visible = Curve.SelectedIndex;
        int stageIndex = SelectedStageIndex;
        if (visible < 0 || stageIndex < 0) return;

        if (ReferenceEquals(sender, RowTemp))
        {
            WpfFanCurveControl.SetTemperature(_temperatureLabels, visible, (int)Math.Round(RowTemp.Value));
            FanStage.ApplyEntryTemperatures(_profile.Stages, _visibleStageIndices, _temperatureLabels, _gpu);
        }
        else if (ReferenceEquals(sender, RowDuty))
        {
            WpfFanCurveControl.SetDuty(_dutyValues, visible, (int)Math.Round(RowDuty.Value));
            stage.SetDuty(_gpu, _dutyValues[visible]);
        }
        else if (ReferenceEquals(sender, RowRpm))
        {
            int maxRpm = _gpu ? FanStage.GpuMaxRpm : FanStage.CpuMaxRpm;
            int duty = FanRpmCalibration.RpmToDuty((int)Math.Round(RowRpm.Value));
            WpfFanCurveControl.SetDuty(_dutyValues, visible, duty);
            stage.SetDuty(_gpu, _dutyValues[visible]);
        }

        stage.Normalize();
        SyncCurve();
        SaveDraft();
    }

    private async void FanMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _applying) return;
        SaveDraft();
        UpdateEditor();
        SyncSelectedStage();
        if (Vm != null && !_applying)
        {
            SetApplying(true);
            try
            {
                await SaveProfileAsync(Vm, _mode, _profile);
                if (await EnsureModeActiveAsync(Vm))
                    await Vm.ApplyFanProfileAsync();
            }
            catch (Exception ex) { ReportFailure(ex); }
            finally { SetApplying(false); }
        }
    }

    private void UpdateEditor()
    {
        if (CurveEditor == null) return;
        bool isCustom = CustomOption.IsChecked == true;
        bool isCurve = CurveOption.IsChecked == true;
        CurveEditor.IsEnabled = isCustom && !_applying;
        CurveEditor.Opacity = isCustom ? 1.0 : 0.45;
        if (CurveEditorPanel != null)
        {
            CurveEditorPanel.Visibility = isCurve ? Visibility.Visible : Visibility.Collapsed;
            CurveEditorPanel.IsEnabled = isCurve && !_applying;
        }
        if (FooterPanel != null)
            FooterPanel.Visibility = isCustom || isCurve ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Profile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _applying || !IsLoaded || Vm == null) return;
        int index = Array.IndexOf(Vm.FanCurveModeNames, ProfileRow.SelectedItem as string);
        if (index < 0 || Modes[index] == _mode) return;
        SaveDraft();
        _mode = Modes[index];
        LoadProfile();
        UpdateEditingHint();
        await Vm.SetModeAsync(_mode);
        UpdateEditingHint();
    }

    /// <summary>
    /// Shows which mode the editor is editing and whether it is the mode the firmware is actually in,
    /// so a curve saved for a different mode is never mistaken for "no effect".
    /// </summary>
    private void UpdateEditingHint()
    {
        if (Vm == null || EditingHint == null) return;
        byte current = Vm.TryGetPowerMode(out byte actual) ? actual : Vm.ActivePowerMode;
        string editing = Services.LocalizationService.Translate(ProfileManager.ModeToProfileName(_mode));
        if (current == _mode)
        {
            EditingHint.Text = Services.LocalizationService.Format("Editing: {0} (current mode)", editing);
            ModeHint.Visibility = Visibility.Collapsed;
        }
        else
        {
            string currentName = Services.LocalizationService.Translate(ProfileManager.ModeToProfileName(current));
            EditingHint.Text = Services.LocalizationService.Format("Editing: {0} — current mode is {1}", editing, currentName);
            ModeHint.Text = Services.LocalizationService.Format(
                "This curve applies to {0}; switch to {0} to use it.", editing);
            ModeHint.Visibility = Visibility.Visible;
        }
    }

    private void FanTab_Changed(object sender, RoutedEventArgs e)
    {
        if (Curve == null) return;
        Curve.SelectedIndex = -1;
        _gpu = FanTabs.SelectedIndex == 1;
        SyncCurve();
    }

    private async System.Threading.Tasks.Task<bool> EnsureModeActiveAsync(MainViewModel vm)
    {
        if (await System.Threading.Tasks.Task.Run(() => vm.IsGpuHardwareModeActive(_mode))) return true;
        await vm.ApplyModeSwitchAsync(_mode);
        return await System.Threading.Tasks.Task.Run(() => vm.IsGpuHardwareModeActive(_mode));
    }

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (Vm == null || _applying) return;
        SetApplying(true);
        try
        {
            var defaultProfile = FanCurveProfile.CreateDefault(_mode);
            defaultProfile.ControlMode = FanControlMode.Custom;
            _drafts[_mode] = defaultProfile;
            await SaveProfileAsync(Vm, _mode, defaultProfile);
            LoadProfile();
            if (!await EnsureModeActiveAsync(Vm)) return;
            await Vm.ApplyFanProfileAsync();
        }
        catch (Exception ex) { ReportFailure(ex); }
        finally { SetApplying(false); }
    }

    private void ReportFailure(Exception error)
    {
        AppLogger.Error($"Fan operation failed: {error}");
        OperationError.Text = $"Fan operation failed: {error.Message}";
        OperationError.Visibility = Visibility.Visible;
    }

    private void SetApplying(bool applying)
    {
        if (applying) OperationError.Visibility = Visibility.Collapsed;
        _applying = applying;
        ProfileRow.IsEnabled = AutoOption.IsEnabled = MaxOption.IsEnabled = CustomOption.IsEnabled = CurveOption.IsEnabled = !applying;
        FooterPanel.IsEnabled = !applying;
        UpdateEditor();
    }

    private static System.Threading.Tasks.Task SaveProfileAsync(MainViewModel vm, byte mode, FanCurveProfile profile)
    {
        var snapshot = profile.Copy();
        return System.Threading.Tasks.Task.Run(() => vm.SaveFanProfile(mode, snapshot));
    }

    private async System.Threading.Tasks.Task SaveAllAsync()
    {
        if (Vm is not { } vm) return;
        SaveDraft();
        var snapshots = _drafts.Select(entry => (entry.Key, Profile: entry.Value.Copy())).ToArray();
        var bindings = ParseBindings(BindingsBox?.Text);
        await System.Threading.Tasks.Task.Run(() =>
        {
            foreach (var (mode, profile) in snapshots) vm.SaveFanProfile(mode, profile);
            vm.SaveForegroundFanBindings(bindings);
        });
        _drafts.Clear();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_applying) return;
        SetApplying(true);
        try
        {
            await SaveAllAsync();
            if (Vm != null)
            {
                byte current = Vm.TryGetPowerMode(out byte actual) ? actual : _mode;
                if (current != _mode)
                {
                    string editing = Services.LocalizationService.Translate(ProfileManager.ModeToProfileName(_mode));
                    string currentName = Services.LocalizationService.Translate(ProfileManager.ModeToProfileName(current));
                    UpdateEditingHint();
                    ModeHint.Text = Services.LocalizationService.Format(
                        "Saved the {0} curve. The current mode is {1}, so it only takes effect after switching to {0}.",
                        editing, currentName);
                    ModeHint.Visibility = Visibility.Visible;
                }
                else
                {
                    UpdateEditingHint();
                }
            }
        }
        catch (Exception ex) { ReportFailure(ex); }
        finally { SetApplying(false); }
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (Vm == null || _applying) return;
        SetApplying(true);
        try
        {
            await SaveAllAsync();
            if (!await EnsureModeActiveAsync(Vm)) return;
            await Vm.ApplyFanProfileAsync();
        }
        catch (Exception ex) { ReportFailure(ex); }
        finally { SetApplying(false); }
    }
}

