using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Predex.WinUI.Services;
using PreySense.Core.Services;
using PreySense.Frontend.ViewModels;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Numerics;

namespace Predex.WinUI.Pages
{
    /// <summary>
    /// Home page: performance-mode row (always) + GPU / display / entry groups (expanded).
    ///
    /// Collapse is a VisualStateManager state on one visual tree — the expanded groups are
    /// never rebuilt, only hidden (UI-DESIGN-RULES §6.2). The window height animation lives
    /// in the shell; this page fades the newly revealed groups in (160 ms, 60 ms delay,
    /// 0/40/80 ms stagger) and fades them out first when collapsing (100 ms).
    /// </summary>
    public sealed partial class HomePage : Page
    {
        private static readonly TimeSpan FadeInDuration = TimeSpan.FromMilliseconds(160);
        private static readonly TimeSpan FadeOutDuration = TimeSpan.FromMilliseconds(100);

        public MainViewModel ViewModel => App.CurrentShell!.ViewModel;

        public HomePage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            UpdateGpuTileLayout();

            // Personalisation → Interface: four switches hide four rows of this panel, and four
            // more pick the fields of the status bar itself.
            // Subscribed here (not in the constructor) because the shell drops the whole page
            // tree when the window hides to the tray, so this page is rebuilt and re-reads the
            // persisted flags on every show.
            Predex.WinUI.Services.AppearanceService.UiPanelsChanged -= OnUiPanelsChanged;
            Predex.WinUI.Services.AppearanceService.UiPanelsChanged += OnUiPanelsChanged;
            ApplyInterfacePanels();

            // The status bar reads the shared snapshot directly instead of the ViewModel's
            // CpuFanText / GpuFanText pair: the same snapshot carries the usage and clock fields
            // the row can now show, and MainViewModel only mirrors two of them.
            TelemetryCoordinator.SnapshotAvailable -= OnTelemetrySnapshot;
            TelemetryCoordinator.SnapshotAvailable += OnTelemetrySnapshot;
            RenderStatusBar(TelemetryCoordinator.CurrentSnapshot);

            // --trace evidence for the integration pass: GPU mode state is read on a background
            // task, so "no tile selected" can simply mean "state not known yet / DDS unavailable"
            // rather than a broken binding. Log whatever the ViewModel actually has at load,
            // including the three-mode verdicts and the evidence behind the current reading.
            Predex.WinUI.Services.ShellLog.Write("home",
                $"loaded expanded={_isExpanded} muxMode={ViewModel.ActiveGpuMuxMode} ({ViewModel.GpuMuxModeName}) " +
                $"showDdsTiles={ViewModel.ShowDdsGpuTiles} biosDiscrete={ViewModel.IsBiosDiscreteGpuActive} " +
                $"gpuModes hybrid={ViewModel.IsGpuHybridModeAvailable} " +
                $"integrated={ViewModel.IsGpuIntegratedModeAvailable} " +
                $"discrete={ViewModel.IsGpuDiscreteModeAvailable} " +
                $"current={ViewModel.GpuMuxModeName} basis={ViewModel.GpuModeBasisToken}");
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            Predex.WinUI.Services.AppearanceService.UiPanelsChanged -= OnUiPanelsChanged;
            TelemetryCoordinator.SnapshotAvailable -= OnTelemetrySnapshot;
            // Nobody is looking at the status bar any more: stop paying for usage / clock reads.
            TelemetryCoordinator.SetMainWindowExtraMetrics(TelemetryMetrics.None);
        }

        // ── Personalisation → Interface (the four panel switches) ────────────────
        private void OnUiPanelsChanged(object? sender, EventArgs e) => ApplyInterfacePanels();

        /// <summary>
        /// Shows / hides the four hideable rows. The Settings entry of the widget row is
        /// deliberately not part of WidgetBar: it is the only way into Settings, so hiding it
        /// would strand the user (see Services\UiPanelState.cs).
        /// </summary>
        private void ApplyInterfacePanels()
        {
            bool status = Predex.WinUI.Services.AppearanceService.IsUiPanelVisible(
                Predex.WinUI.Services.UiPanel.StatusBar);
            bool gpu = Predex.WinUI.Services.AppearanceService.IsUiPanelVisible(
                Predex.WinUI.Services.UiPanel.GpuBar);
            bool refreshRate = Predex.WinUI.Services.AppearanceService.IsUiPanelVisible(
                Predex.WinUI.Services.UiPanel.RefreshRateBar);
            bool widgets = Predex.WinUI.Services.AppearanceService.IsUiPanelVisible(
                Predex.WinUI.Services.UiPanel.WidgetBar);

            // All four status-bar fields off means the row would print two bare device labels:
            // an empty row is not worth 14 DIP (UI-DESIGN-RULES §5), so it leaves with them.
            bool statusFields = Predex.WinUI.Services.AppearanceService.UiPanels.AnyStatusFieldVisible;

            SetRowVisible(TelemetryRow, status && statusFields);
            SetRowVisible(RuleStatusBar, status && statusFields);

            SetRowVisible(GpuHeaderRow, gpu);
            SetRowVisible(GpuGroup, gpu);
            SetRowVisible(RuleGpuBar, gpu);

            SetRowVisible(DisplayGroup, refreshRate);
            SetRowVisible(RuleRefreshRateBar, refreshRate);

            SetRowVisible(EntryLighting, widgets);
            SetRowVisible(EntryOverlay, widgets);
            SetRowVisible(EntryFans, widgets);
            SetRowVisible(EntryTuning, widgets);

            ApplyStatusBarDemand();
            RenderStatusBar(TelemetryCoordinator.CurrentSnapshot);

            Predex.WinUI.Services.ShellLog.Write("home",
                $"interface panels statusBar={status} gpuBar={gpu} refreshRateBar={refreshRate} widgetBar={widgets} " +
                $"statusFields={DescribeStatusFields()}");
        }

        /// <summary>Which status-bar fields are on, as log text (one line per panel rebuild).</summary>
        private static string DescribeStatusFields()
        {
            var panels = Predex.WinUI.Services.AppearanceService.UiPanels;
            return $"temp={panels.StatusBarTemperature} usage={panels.StatusBarUsage} " +
                   $"freq={panels.StatusBarFrequency} fan={panels.StatusBarFan}";
        }

        private static void SetRowVisible(FrameworkElement row, bool visible)
            => row.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        // ── Status bar: which fields, and how they are rendered ──────────────────
        //
        // One 14 DIP line, CPU left / GPU right, each column a label plus a star-sized value that
        // trims. The compact forms (62°, 41%, 4.2G, 4109) are what makes four fields fit one
        // column of a 320 DIP window; the exact reading, units included, is the tooltip. A field
        // with no reading prints "—" — never a blank, and never an invented number.
        private readonly record struct StatusField(string Compact, string Exact);

        /// <summary>Asks the coordinator for the sensors the current selection actually shows.</summary>
        private void ApplyStatusBarDemand()
        {
            var panels = Predex.WinUI.Services.AppearanceService.UiPanels;
            TelemetryMetrics extra = TelemetryMetrics.None;

            if (panels.StatusBarUsage)
                extra |= TelemetryMetrics.CpuUsage | TelemetryMetrics.GpuUsage;
            if (panels.StatusBarFrequency)
                extra |= TelemetryMetrics.CpuClock | TelemetryMetrics.GpuActiveClock;

            TelemetryCoordinator.SetMainWindowExtraMetrics(extra);
        }

        private void OnTelemetrySnapshot(TelemetrySnapshot snapshot)
        {
            // SnapshotAvailable is raised on the sampling thread.
            DispatcherQueue.TryEnqueue(() => RenderStatusBar(snapshot));
        }

        private void RenderStatusBar(TelemetrySnapshot snapshot)
        {
            var panels = Predex.WinUI.Services.AppearanceService.UiPanels;
            if (TxtStatusCpu is null || TxtStatusGpu is null) return;

            List<StatusField> cpu = BuildFields(snapshot, gpu: false, panels);
            List<StatusField> gpu = BuildFields(snapshot, gpu: true, panels);

            SetStatusText(TxtStatusCpu, cpu);
            SetStatusText(TxtStatusGpu, gpu);
        }

        private static void SetStatusText(TextBlock target, List<StatusField> fields)
        {
            string compact = string.Empty;
            string exact = string.Empty;
            foreach (StatusField field in fields)
            {
                compact = compact.Length == 0 ? field.Compact : compact + " " + field.Compact;
                exact = exact.Length == 0 ? field.Exact : exact + " · " + field.Exact;
            }

            target.Text = compact.Length == 0 ? "—" : compact;
            // The compact form ("62° 41% 4.2G 4109") is not self-describing, so the exact reading
            // with units is both the tooltip and the screen-reader value (UI-DESIGN-RULES §7.4 —
            // the accessible text may be longer than the visible text, it just must not be drawn).
            ToolTipService.SetToolTip(target, exact.Length == 0 ? null : exact);
            AutomationProperties.SetHelpText(target, exact);
        }

        private static List<StatusField> BuildFields(
            TelemetrySnapshot snapshot, bool gpu, UiPanelVisibility panels)
        {
            var fields = new List<StatusField>(4);
            if (panels.StatusBarTemperature) fields.Add(Temperature(snapshot, gpu));
            if (panels.StatusBarUsage) fields.Add(Usage(snapshot, gpu));
            if (panels.StatusBarFrequency) fields.Add(Frequency(snapshot, gpu));
            if (panels.StatusBarFan) fields.Add(Fan(snapshot, gpu));
            return fields;
        }

        // The three readings below are only trusted while the snapshot is valid, exactly like
        // MainViewModel.ApplyMainWindowTelemetry: a stale snapshot reports nulls, and a null must
        // render as "—" rather than as a zero.
        private static StatusField Temperature(TelemetrySnapshot snapshot, bool gpu)
        {
            int? celsius = snapshot.IsValid ? (gpu ? snapshot.GpuTemp : snapshot.CpuTemp) : null;
            return celsius is > 0
                ? new StatusField($"{celsius}°", $"{celsius} °C")
                : new StatusField("—", "—");
        }

        private static StatusField Usage(TelemetrySnapshot snapshot, bool gpu)
        {
            int? percent = snapshot.IsValid ? (gpu ? snapshot.GpuUsage : snapshot.CpuUsage) : null;
            return percent is >= 0 and <= 100
                ? new StatusField($"{percent}%", $"{percent} %")
                : new StatusField("—", "—");
        }

        private static StatusField Frequency(TelemetrySnapshot snapshot, bool gpu)
        {
            // CPU: the P-core active clock (CpuActiveFrequencyMhz is the same sample's maximum).
            // GPU: NVAPI's active clock, falling back to the reported clock.
            int? mhz = snapshot.IsValid
                ? (gpu
                    ? snapshot.GpuActiveClockMhz ?? snapshot.GpuClockMhz
                    : snapshot.CpuClockMhz ?? snapshot.CpuActiveFrequencyMhz)
                : null;

            if (mhz is not > 0) return new StatusField("—", "—");

            string compact = mhz >= 1000
                ? (mhz.Value / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "G"
                : mhz.Value.ToString(CultureInfo.InvariantCulture) + "M";
            return new StatusField(compact, $"{mhz} MHz");
        }

        private static StatusField Fan(TelemetrySnapshot snapshot, bool gpu)
        {
            int? rpm = snapshot.IsValid ? (gpu ? snapshot.GpuRpm : snapshot.CpuRpm) : null;
            return rpm is > 0
                ? new StatusField($"{rpm}", $"{rpm} RPM")
                : new StatusField("—", "—");
        }

        // ── Density state ────────────────────────────────────────────────────────
        public bool IsExpanded => _isExpanded;
        private bool _isExpanded;

        /// <summary>Switches the density state without any animation.</summary>
        public void SetExpandedState(bool expanded)
        {
            _isExpanded = expanded;
            VisualStateManager.GoToState(this, expanded ? "Expanded" : "Collapsed", false);
        }

        /// <summary>
        /// Collapsed → expanded: reveal the groups, then fade them in so they do not appear
        /// fully opaque for a frame before the animation starts.
        /// </summary>
        public void PlayExpandContent()
        {
            foreach (UIElement group in GroupElements())
                ElementCompositionPreview.GetElementVisual(group).Opacity = 0f;

            SetExpandedState(true);

            if (!MotionEnabled())
            {
                foreach (UIElement group in GroupElements())
                    ElementCompositionPreview.GetElementVisual(group).Opacity = 1f;
                return;
            }

            int index = 0;
            foreach (UIElement group in GroupElements())
            {
                Visual visual = ElementCompositionPreview.GetElementVisual(group);
                Compositor compositor = visual.Compositor;

                // Opacity only. Animating "Offset" here would overwrite each group's
                // layout offset (they sit below the mode row) and pile them up at the top.
                var fade = compositor.CreateScalarKeyFrameAnimation();
                fade.InsertKeyFrame(0f, 0f);
                fade.InsertKeyFrame(1f, 1f,
                    compositor.CreateCubicBezierEasingFunction(
                        new Vector2(0.23f, 1f), new Vector2(0.32f, 1f)));
                fade.Duration = FadeInDuration;
                fade.DelayTime = TimeSpan.FromMilliseconds(60 + (index * 40));

                visual.StartAnimation("Opacity", fade);
                index++;
            }
        }

        /// <summary>Expanded → collapsed: the content leaves first (100 ms), then the shell shrinks.</summary>
        public void PlayCollapseContent(Action? onComplete = null)
        {
            if (!MotionEnabled())
            {
                SetExpandedState(false);
                onComplete?.Invoke();
                return;
            }

            int pending = 3;
            foreach (UIElement group in GroupElements())
            {
                Visual visual = ElementCompositionPreview.GetElementVisual(group);
                Compositor compositor = visual.Compositor;

                var fade = compositor.CreateScalarKeyFrameAnimation();
                fade.InsertKeyFrame(0f, visual.Opacity);
                fade.InsertKeyFrame(1f, 0f,
                    compositor.CreateCubicBezierEasingFunction(
                        new Vector2(0.23f, 1f), new Vector2(0.32f, 1f)));
                fade.Duration = FadeOutDuration;

                var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
                visual.StartAnimation("Opacity", fade);
                batch.End();
                batch.Completed += (_, _) =>
                {
                    if (--pending > 0) return;
                    SetExpandedState(false);
                    foreach (UIElement element in GroupElements())
                        ElementCompositionPreview.GetElementVisual(element).Opacity = 1f;
                    onComplete?.Invoke();
                };
            }
        }

        private UIElement[] GroupElements() => [GpuGroup, DisplayGroup, EntryGroup];

        private static bool MotionEnabled()
        {
            try { return new Windows.UI.ViewManagement.UISettings().AnimationsEnabled; }
            catch { return true; }
        }

        // ── GPU tiles ────────────────────────────────────────────────────────────
        private bool _telemetryLogged;

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // The GPU row is a fixed three-column layout, so no property drives a re-flow here:
            // each tile's IsEnabled / tooltip / selected state comes straight from x:Bind.

            // One line of evidence per run: prove CPU and GPU fan come from separate properties
            // (a review flagged "both 4109 RPM" as a possible double binding).
            if (!_telemetryLogged && e.PropertyName == nameof(MainViewModel.CpuFanText))
            {
                _telemetryLogged = true;
                Predex.WinUI.Services.ShellLog.Write("home",
                    $"telemetry cpu={ViewModel.CpuTemperatureText}/{ViewModel.CpuFanText} " +
                    $"gpu={ViewModel.GpuTemperatureText}/{ViewModel.GpuFanText} " +
                    $"display={ViewModel.ActiveGpuDisplayMode} muxMode={ViewModel.ActiveGpuMuxMode}");
            }
        }

        /// <summary>
        /// The GPU row is a fixed three-column layout: one tile per mode, and a mode this machine
        /// cannot do is disabled in place (greyed + reason in its tooltip), never hidden. There is
        /// nothing left to re-flow, so this re-asserts the placement and records the capability
        /// set once per load — the log line is what tells "this chassis cannot do it" apart from
        /// "the binding broke".
        /// </summary>
        private void UpdateGpuTileLayout()
        {
            Grid.SetColumn(GpuHybridTile, 0);
            Grid.SetColumn(GpuIntegratedTile, 1);
            Grid.SetColumn(GpuDiscreteTile, 2);

            Predex.WinUI.Services.ShellLog.Write("home",
                $"gpu tiles columns=3 " +
                $"hybrid={Describe(ViewModel.GpuCapabilities.Hybrid)} " +
                $"integrated={Describe(ViewModel.GpuCapabilities.Integrated)} " +
                $"discrete={Describe(ViewModel.GpuCapabilities.Discrete)}");
        }

        /// <summary>One tile's verdict as log text: available, or which blocker disabled it.</summary>
        private static string Describe(PreySense.Gpu.GpuModeAvailability availability) =>
            availability.IsAvailable ? "on" : $"off({availability.Blocker})";

        // ── Entry points into the other pages ────────────────────────────────────
        private void LightingShortcut_Click(object sender, RoutedEventArgs e)
            => App.CurrentShell?.NavigateTo("Lighting");

        private void OverlayShortcut_Click(object sender, RoutedEventArgs e)
            => App.CurrentShell?.NavigateTo("Overlay");

        private void FansShortcut_Click(object sender, RoutedEventArgs e)
            => App.CurrentShell?.NavigateTo("Fans");

        private void TuningShortcut_Click(object sender, RoutedEventArgs e)
            => App.CurrentShell?.NavigateTo("Tuning");

        private void SettingsShortcut_Click(object sender, RoutedEventArgs e)
            => App.CurrentShell?.NavigateTo("Settings");
    }
}
