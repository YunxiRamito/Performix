using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Predex.WinUI.Services;
using PreySense.Frontend.ViewModels;
using System;
using System.ComponentModel;
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

            // Personalisation → Interface: four switches hide four rows of this panel.
            // Subscribed here (not in the constructor) because the shell drops the whole page
            // tree when the window hides to the tray, so this page is rebuilt and re-reads the
            // persisted flags on every show.
            Predex.WinUI.Services.AppearanceService.UiPanelsChanged -= OnUiPanelsChanged;
            Predex.WinUI.Services.AppearanceService.UiPanelsChanged += OnUiPanelsChanged;
            ApplyInterfacePanels();

            // --trace evidence for the integration pass: GPU mux state is read on a background
            // task, so "no tile selected" can simply mean "state not known yet / DDS unavailable"
            // rather than a broken binding. Log whatever the ViewModel actually has at load.
            Predex.WinUI.Services.ShellLog.Write("home",
                $"loaded expanded={_isExpanded} muxMode={ViewModel.ActiveGpuMuxMode} ({ViewModel.GpuMuxModeName}) " +
                $"showDdsTiles={ViewModel.ShowDdsGpuTiles} biosDiscrete={ViewModel.IsBiosDiscreteGpuActive}");
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            Predex.WinUI.Services.AppearanceService.UiPanelsChanged -= OnUiPanelsChanged;
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

            SetRowVisible(TelemetryRow, status);
            SetRowVisible(RuleStatusBar, status);

            SetRowVisible(GpuHeaderRow, gpu);
            SetRowVisible(GpuGroup, gpu);
            SetRowVisible(RuleGpuBar, gpu);

            SetRowVisible(DisplayGroup, refreshRate);
            SetRowVisible(RuleRefreshRateBar, refreshRate);

            SetRowVisible(EntryLighting, widgets);
            SetRowVisible(EntryOverlay, widgets);
            SetRowVisible(EntryFans, widgets);
            SetRowVisible(EntryTuning, widgets);

            Predex.WinUI.Services.ShellLog.Write("home",
                $"interface panels statusBar={status} gpuBar={gpu} refreshRateBar={refreshRate} widgetBar={widgets}");
        }

        private static void SetRowVisible(FrameworkElement row, bool visible)
            => row.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

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
            if (e.PropertyName is nameof(MainViewModel.ShowDdsGpuTiles) or nameof(MainViewModel.IsBiosDiscreteGpuActive))
                UpdateGpuTileLayout();

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
        /// The WPF build swaps the GPU tile UniformGrid between 4 and 2 columns. A Grid
        /// cannot change its column count per state, so the two tiles that stay visible span
        /// two columns each when the DDS tiles are hidden.
        /// </summary>
        private void UpdateGpuTileLayout()
        {
            bool showDds = ViewModel.ShowDdsGpuTiles;
            GpuHybridTile.Visibility = showDds ? Visibility.Visible : Visibility.Collapsed;
            GpuAdvancedTile.Visibility = showDds ? Visibility.Visible : Visibility.Collapsed;

            Grid.SetColumn(GpuAutoTile, 0);
            Grid.SetColumnSpan(GpuAutoTile, showDds ? 1 : 2);
            Grid.SetColumn(GpuDiscreteTile, showDds ? 3 : 2);
            Grid.SetColumnSpan(GpuDiscreteTile, showDds ? 1 : 2);
        }

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
