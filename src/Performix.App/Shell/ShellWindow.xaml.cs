using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Predex.WinUI.Pages;
using Predex.WinUI.Services;
using PreySense.Core.Services;
using PreySense.Frontend.Services;
using PreySense.Frontend.ViewModels;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using WinRT.Interop;

namespace Predex.WinUI.Shell
{
    /// <summary>
    /// The one and only Predex window (S1): custom 40 DIP title bar, page host, tray/hotkey
    /// lifetime.
    ///
    /// Layout (docs\UI-DESIGN-RULES.md §6): fixed 320 DIP wide; 116 DIP collapsed (title bar
    /// + one performance-mode row), 280 DIP expanded (adds GPU modes, refresh rate and the
    /// entry points). The window grows from its bottom-right anchor, frame by frame, with
    /// cubic-bezier(0.16, 1, 0.3, 1) — no Storyboard on Height (that drops frames).
    ///
    /// Navigation: no NavigationView pane — 320 DIP has no room for one, and the design spec
    /// turns the old footer into a single 28 DIP entry row. Pages are created lazily, kept
    /// for the window session and swapped into <c>PageHost</c>; the title bar switches to a
    /// back button + page title while a non-home page is open. This is deliberately not
    /// <c>Frame.Navigate</c> (it would rebuild the page and lose half-entered state, the risk
    /// called out in WINUI3-MIGRATION.md §6.7); inactive pages are dropped when the window
    /// hides, keeping the hidden-memory target reachable.
    /// </summary>
    public sealed partial class ShellWindow : Window
    {
        // ── Page registry ────────────────────────────────────────────────────────
        // Settings is a directory page now: General / Power / Theme / Meta are its sub-pages
        // (IA phase 1, docs\design\UI-ARCH.md §1/§2). Every page must appear here and in
        // PageTitleKeys, or NavigateTo silently drops the tag.
        private static readonly Dictionary<string, Type> PageTypes = new(StringComparer.Ordinal)
        {
            ["Home"] = typeof(HomePage),
            ["Tuning"] = typeof(TuningPage),
            ["Fans"] = typeof(FansPage),
            ["Lighting"] = typeof(LightingPage),
            ["Overlay"] = typeof(OverlayPage),
            ["Settings"] = typeof(SettingsPage),
            ["General"] = typeof(GeneralPage),
            ["Power"] = typeof(PowerPage),
            ["Theme"] = typeof(ThemePage),
            ["Meta"] = typeof(MiniMetaPage),
        };

        // English source keys; resolved through LocalizationService on the way out.
        private static readonly Dictionary<string, string> PageTitleKeys = new(StringComparer.Ordinal)
        {
            ["Home"] = "Home",
            ["Tuning"] = "Tuning",
            ["Fans"] = "Fans",
            ["Lighting"] = "Lighting",
            ["Overlay"] = "Overlay",
            ["Settings"] = "Settings",
            ["General"] = "General",
            ["Power"] = "Power Profiles",
            ["Theme"] = "Theme",
            ["Meta"] = "Updates and About",
        };

        // ── Back targets ─────────────────────────────────────────────────────────
        // One row per page that has a fixed parent: the four Settings sub-pages return to
        // Settings (the directory page). Every other page either comes from the history below
        // (Fans has two entries: Home and Tuning) or returns Home. A map instead of a chain of
        // ifs, and the authoritative source for the settings tree — the history never records
        // a hop into a page listed here, so the two cannot fight (PAGES-TREE.md "返回行为").
        private static readonly Dictionary<string, string> PageBackTargets = new(StringComparer.Ordinal)
        {
            ["General"] = "Settings",
            ["Power"] = "Settings",
            ["Theme"] = "Settings",
            ["Meta"] = "Settings",
        };

        // ── DIP sizes (converted with the window DPI before reaching AppWindow) ──
        private const int WindowWidth = 320;
        private const int CompactHeight = 116;
        // H1: the home GPU row gains a 14 DIP header row + 6 DIP gap, so the expanded home is
        // 300 DIP (collapsed stays 116). The extra 20 DIP of window height pays for it, so the
        // version footer keeps sitting in the bottom padding band.
        private const int ExpandedHeight = 300;
        private const int ExpandDurationMs = 220;
        private const int CollapseDurationMs = 200;

        /// <summary>Height of the shell title bar; the page host gets everything below it.</summary>
        private const int TitleBarHeightDip = 40;
        /// <summary>Screen edge margin used when the window is pinned to the work-area corner.</summary>
        private const int PlacementMarginDip = 16;
        /// <summary>Lower bound for an auto-height page window (title bar + a usable body).</summary>
        private const int PageMinHeightDip = 200;
        /// <summary>
        /// Upper bound for a secondary page window (R3). Above this the page does not grow any
        /// more: the remaining content scrolls inside the page's own ScrollViewer, so a long
        /// page (tuning was 880 DIP, lighting 844, fans 765) can no longer stretch the window
        /// past what is comfortable to control with a mouse wheel. The cap is the whole window
        /// height, title bar included, i.e. 480 DIP of page body.
        /// </summary>
        private const int SecondaryPageMaxHeightDip = 520;

        public MainViewModel ViewModel { get; }

        /// <summary>
        /// WinUI implementation of the ViewModel's dialog seam (ContentDialog hosted on this
        /// window's XamlRoot). S6 replaces Views\CustomModalDialogCompat.cs (a Win32
        /// MessageBoxW shim) with it; the linked ViewModels only ever call ConfirmAsync /
        /// NotifyAsync, so the GPU-MUX confirmation and the GPU power-cycle notice are the two
        /// prompts that can appear.
        /// </summary>
        public ContentDialogUserPromptService Prompts { get; }

        private readonly Dictionary<string, Page> _pages = new(StringComparer.Ordinal);
        /// <summary>
        /// Back-button history for pages that have no fixed parent in
        /// <see cref="PageBackTargets"/>. Fans is the only page with two entries (Home and
        /// Tuning), so its back target has to come from the hop that opened it. Settings
        /// sub-pages never push: their parent is the table, so the table cannot disagree with
        /// a stale history entry. One entry per user-initiated hop, capped at 8.
        /// </summary>
        private readonly List<string> _navBackStack = new();
        private readonly ShellUiState _uiState = ShellUiState.Load();
        private ShellServices? _services;
        private DispatcherQueueTimer? _resizeTimer;
        private DispatcherQueueTimer? _autoHideTimer;
        private DispatcherQueueTimer? _toastTimer;
        private bool _toastVisualReady;
        private int _toastSession;
        private bool _isExplicitExit;
        private bool _isExpanded;
        private string _currentTag = "Home";
        private DateTime _ignoreDeactivateUntil = DateTime.MinValue;

        public ShellWindow()
        {
            // Hardware + ViewModel must exist before InitializeComponent so x:Bind resolves.
            IHardwareService hardware;
            try
            {
                hardware = new ProductionHardwareService();
                ShellLog.Write("hardware", $"ProductionHardwareService ready (mode={SafeReadMode(hardware)})");
            }
            catch (Exception ex)
            {
                ShellLog.Write("hardware", $"ProductionHardwareService failed: {ex.Message} — falling back to mock");
                hardware = new MockHardwareService();
            }

            ViewModel = new MainViewModel(hardware);
            AttachViewModelThreadGuard();
            _isExpanded = _uiState.HomeExpanded;

            InitializeComponent();

            App.CurrentShell = this;
            ThemeManager.Initialize(this);

            // S6 dialog seam: assign the ContentDialog-backed prompt service before any page can
            // raise a prompt. MainViewModel.UserPrompts is the internal setter the WPF project
            // added for exactly this (WPF wires WpfUserPromptService in its own MainWindow).
            Prompts = new ContentDialogUserPromptService(
                () => RootGrid.XamlRoot,
                () => RootGrid.RequestedTheme,
                DispatcherQueue);
            ViewModel.UserPrompts = Prompts;

            SetupWindowChrome();
            ApplyLocalizedChrome();
            SetupEscapeToHide();
            NavigateTo("Home", allowRedirect: false);

            // First layout: apply the persisted density without animating.
            var (w, h) = TargetSize(_isExpanded);
            PlaceBottomRight(w, h);
            ApplyDensity(_isExpanded, animate: false);
            UpdateChrome();

            // ── Shell services (tray / hotkeys / HID / memory) ───────────────────
            IntPtr hWnd = WindowNative.GetWindowHandle(this);
            _services = new ShellServices(hWnd, DispatcherQueue);
            _services.OpenRequested += ShowAndActivate;
            _services.TrayToggleRequested += TrayClicked;
            _services.ToggleRequested += ToggleVisibility;
            _services.QuitRequested += QuitApplication;
            _services.OverlayToggleRequested += () => DispatcherQueue.TryEnqueue(OverlayBridge.Toggle);
            _services.ModeShortcutRequested += mode =>
                DispatcherQueue.TryEnqueue(() => _ = ViewModel.SetModeAsync(mode));
            _services.ModeKeyPressed += () =>
                DispatcherQueue.TryEnqueue(ViewModel.HandleModeKeyPressed);

            ViewModel.NotifyApplicationReady();

            // ── Lifetime events (S0 never subscribed Deactivated: fixed here) ────
            Activated += Window_Activated;
            Closed += OnWindowClosed;

            LocalizationService.LanguageChanged += OnLanguageChanged;

            PreySense.Frontend.Views.ModeToastWindow.Initialize(DispatcherQueue);

            // ── Mode-switch confirmation → desktop OSD overlay ────────────────────
            // MainViewModel.NotifyConfirmedPowerMode() runs only after the EC confirmed the
            // switch; it calls ModeToastPresenter if one is installed, otherwise the
            // ModeToastWindow compat fallback (both end up in the same overlay, see
            // Views\ModeToastCompat.cs). Installing the presenter here is the explicit
            // success-path wiring: the overlay replaces the old InfoBar toast for success,
            // while failures keep using ContentDialogUserPromptService / ShowToast.
            ViewModel.ModeToastPresenter = (mode, title) =>
            {
                ShellLog.Write("osd", $"mode confirmed 0x{mode:X2} '{title}' → overlay");
                Views.ModeOsdWindow.ShowMode(mode, title);
            };

            ShellLog.Write("shell",
                $"window ready expanded={_isExpanded} size={AppWindow.Size.Width}x{AppWindow.Size.Height}");
        }

        private static string SafeReadMode(IHardwareService hardware)
        {
            try { return $"0x{hardware.CurrentPowerMode:X2}"; }
            catch { return "unavailable"; }
        }

        /// <summary>
        /// Diagnostic for the intermittent cold-start crash (0xc000027b in Microsoft.UI.Xaml.dll).
        /// The linked WPF ViewModel raises INotifyPropertyChanged from whatever thread performs
        /// the work — legal in WPF because its binding engine marshals to the dispatcher, but NOT
        /// in WinUI 3, where a compiled x:Bind updates the target element directly on the raising
        /// thread. Any off-UI-thread raise of a bound property is therefore a XAML wrong-thread
        /// access; log the raise plus the setter stack so it shows up in shell.log instead of
        /// only as a fail-fast with an empty crash.log.
        /// </summary>
        private void AttachViewModelThreadGuard()
        {
            // Captured on the UI thread: Window.DispatcherQueue itself is not safe to read
            // from a worker thread, the queue object it returns is (TryEnqueue is cross-thread).
            DispatcherQueue uiQueue = DispatcherQueue;
            int logged = 0;
            ViewModel.PropertyChanged += (_, e) =>
            {
                try
                {
                    if (uiQueue.HasThreadAccess) return;
                    int n = System.Threading.Interlocked.Increment(ref logged);
                    if (n > 40) return;
                    ShellLog.Write("thread-guard",
                        $"cross-thread PropertyChanged '{e.PropertyName}' on thread " +
                        $"{Environment.CurrentManagedThreadId} (uiAccess={uiQueue.HasThreadAccess}) #{n} {SetStack()}");
                }
                catch { }
            };
        }

        /// <summary>"A.B ← C.D ← ..." for the innermost frames (who raised the change).</summary>
        private static string SetStack()
        {
            try
            {
                var frames = new System.Diagnostics.StackTrace(2, fNeedFileInfo: false);
                var names = new List<string>(6);
                for (int i = 0; i < frames.FrameCount && names.Count < 6; i++)
                {
                    System.Reflection.MethodBase? method = frames.GetFrame(i)?.GetMethod();
                    if (method is null) continue;
                    string? type = method.DeclaringType?.Name;
                    names.Add(type is null ? method.Name : $"{type}.{method.Name}");
                }
                return names.Count == 0 ? "<unknown>" : string.Join(" ← ", names);
            }
            catch (Exception ex) { return $"<frames unavailable: {ex.GetType().Name}>"; }
        }

        // ── Window chrome (DWM + Win32) ──────────────────────────────────────────
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hWnd, uint attr, ref int attrValue, int size);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hWnd);

        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_CAPTION = 0x00C00000;
        private const int WS_THICKFRAME = 0x00040000;
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_FRAMECHANGED = 0x0020;
        private const uint DWMWA_BORDER_COLOR = 34;
        private const uint DWMWA_CAPTION_COLOR = 35;
        /// <summary>DWMWA_COLOR_NONE — let DWM draw no frame line at all.</summary>
        private const int DwmwaColorNone = unchecked((int)0xFFFFFFFE);

        private IntPtr Hwnd => WindowNative.GetWindowHandle(this);

        private int Scale(double dip) => (int)Math.Round(dip * GetDpiForWindow(Hwnd) / 96.0);

        private void SetupWindowChrome()
        {
            var titleBar = AppWindow.TitleBar;
            titleBar.ExtendsContentIntoTitleBar = true;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonForegroundColor = Colors.White;
            titleBar.ButtonInactiveForegroundColor = Colors.Gray;

            // R3, on request: the panel is NOT draggable. SetTitleBar() used to hand the
            // whole left part of the title bar to the system as a caption/drag region; it is
            // gone now, together with our own WM_NCLBUTTONDOWN(HTCAPTION) fallback, so a
            // click or a drag on the title bar does nothing but pass hit testing through.
            // The window position is purely the shell's own bottom-right placement.

            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = false;       // fixed 320 DIP width
                // R2: this is a fixed-width tray panel — no maximize (there is no maximize
                // button either) and no minimize; the window is not a taskbar window.
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.SetBorderAndTitleBar(false, false);
            }

            // Tray app: out of the taskbar / Alt+Tab, but activation + keyboard focus allowed
            // (S0 also set WS_EX_NOACTIVATE, which is why typing was never verified).
            int exStyle = GetWindowLong(Hwnd, GWL_EXSTYLE);
            SetWindowLong(Hwnd, GWL_EXSTYLE, (exStyle | WS_EX_TOOLWINDOW) & ~WS_EX_NOACTIVATE);

            // ── The light outline around the window is TWO lines, not one ────────
            // 1. DWM draws its own 2 px frame; SetBorderAndTitleBar(false, false) does not
            //    remove it. DWMWA_COLOR_NONE turns it off completely.
            // 2. WS_OVERLAPPEDWINDOW still leaves a 3 px non-client frame whose innermost
            //    1 px shows the window's white background. Only switching the window style
            //    to WS_POPUP (and forcing SWP_FRAMECHANGED) removes it — same fix as
            //    Dafeiyu-Go InfoWindow.MakeBorderlessPopup().
            int none = DwmwaColorNone;
            DwmSetWindowAttribute(Hwnd, DWMWA_BORDER_COLOR, ref none, sizeof(int));
            DwmSetWindowAttribute(Hwnd, DWMWA_CAPTION_COLOR, ref none, sizeof(int));

            MakeBorderlessPopup();

            // Window shape (DWM round vs square) is owned by CornerRadiusHelper so startup and
            // the appearance page share one code path (UPDATE-DESIGN §3.2.4 pitfall 2: the DWM
            // attribute has to be re-applied to the live HWND on every style change).
            CornerRadiusHelper.ApplyWindowFrame(Hwnd, AppearanceService.WindowStyle);
        }

        /// <summary>
        /// Swaps WS_OVERLAPPEDWINDOW for WS_POPUP so the non-client frame (and its 1 px
        /// white inner line) disappears. The window is fixed-size by design and is not
        /// draggable (R3), so dropping WS_THICKFRAME and the caption — hence every native
        /// drag path — costs nothing.
        /// </summary>
        private void MakeBorderlessPopup()
        {
            int style = GetWindowLong(Hwnd, GWL_STYLE);
            int popup = (style & ~(WS_CAPTION | WS_THICKFRAME)) | WS_POPUP;
            SetWindowLong(Hwnd, GWL_STYLE, popup);
            SetWindowPos(Hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            ShellLog.Write("window",
                $"style 0x{style:X8} → 0x{popup:X8} (WS_POPUP, DWM border colour = COLOR_NONE)");
        }

        // ── Placement / sizing ───────────────────────────────────────────────────
        private static (int Width, int Height) TargetSize(bool expanded) =>
            (WindowWidth, expanded ? ExpandedHeight : CompactHeight);

        private void PlaceBottomRight(int widthDip, int heightDip)
        {
            var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            int w = Scale(widthDip);
            int h = Scale(heightDip);
            int margin = Scale(PlacementMarginDip);
            int x = work.X + work.Width - w - margin;
            int y = work.Y + work.Height - h - margin;
            x = Math.Clamp(x, work.X, work.X + Math.Max(0, work.Width - w));
            y = Math.Clamp(y, work.Y, work.Y + Math.Max(0, work.Height - h));
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, w, h));
        }

        private void AnimateWindowSize(int targetHeightDip, int durationMs, int delayMs)
        {
            int targetWidth = Scale(WindowWidth);
            int targetHeight = Scale(targetHeightDip);

            var size = AppWindow.Size;
            var position = AppWindow.Position;

            void ApplyImmediately()
            {
                int x = position.X + size.Width - targetWidth;
                int y = position.Y + size.Height - targetHeight;
                try
                {
                    AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, targetWidth, targetHeight));
                }
                catch { }
            }

            // Reduced motion (or a first layout pass): switch size without animating.
            if (durationMs <= 0 || !MotionEnabled())
            {
                ApplyImmediately();
                return;
            }

            // Anchored bottom-right: interpolate the right/bottom edges, derive x/y.
            int right = position.X + size.Width;
            int bottom = position.Y + size.Height;
            int fromHeight = size.Height;
            int fromWidth = size.Width;
            int ticks = 0;
            int skipped = 0;
            const int frameMs = 16;
            int delayFrames = (int)Math.Ceiling(delayMs / (double)frameMs);

            _resizeTimer ??= DispatcherQueue.CreateTimer();
            _resizeTimer.Stop();
            _resizeTimer.Interval = TimeSpan.FromMilliseconds(frameMs);
            _resizeTimer.IsRepeating = true;
            _resizeTimer.Tick += ResizeTick;
            _resizeTimer.Start();

            void ResizeTick(DispatcherQueueTimer timer, object args)
            {
                // Frame-count progress (never wall clock): a stalled UI thread must not
                // teleport the window to its final size.
                if (skipped++ < delayFrames) return;
                ticks++;

                double progress = Math.Min(1.0, (double)(ticks * frameMs) / durationMs);
                double eased = CubicBezierEase(progress);
                int width = (int)Math.Round(fromWidth + ((targetWidth - fromWidth) * eased));
                int height = (int)Math.Round(fromHeight + ((targetHeight - fromHeight) * eased));
                try
                {
                    AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                        right - width, bottom - height, width, height));
                }
                catch { /* window may be closing */ }

                if (progress >= 1)
                {
                    timer.Stop();
                    timer.Tick -= ResizeTick;
                }
            }
        }

        private static bool MotionEnabled()
        {
            try { return new Windows.UI.ViewManagement.UISettings().AnimationsEnabled; }
            catch { return true; }
        }

        // ── Expand / collapse ────────────────────────────────────────────────────
        private void ExpandButton_Click(object sender, RoutedEventArgs e) => SetExpanded(!_isExpanded);

        private void SetExpanded(bool expanded)
        {
            if (_isExpanded == expanded) return;
            _uiState.HomeExpanded = expanded;
            _uiState.Save();
            ApplyDensity(expanded, animate: true);
            ShellLog.Write("shell", $"expanded={expanded}");
        }

        /// <summary>Drives the home density state and the window height together.</summary>
        private void ApplyDensity(bool expanded, bool animate)
        {
            _isExpanded = expanded;
            UpdateChrome();
            AnimateChevron(expanded);

            HomePage? home = HomeIfCreated();
            if (expanded)
            {
                home?.PlayExpandContent();
                AnimateWindowSize(ExpandedHeight,
                    animate ? ExpandDurationMs : 0, 0);
            }
            else
            {
                if (home is null)
                {
                    AnimateWindowSize(CompactHeight, animate ? CollapseDurationMs : 0, 0);
                    return;
                }

                // Content leaves first (100 ms), the window follows 60 ms later.
                home.PlayCollapseContent();
                AnimateWindowSize(CompactHeight,
                    animate ? CollapseDurationMs : 0, animate ? 60 : 0);
            }
        }

        private void AnimateChevron(bool expanded)
        {
            Visual visual = ElementCompositionPreview.GetElementVisual(ExpandGlyph);
            visual.CenterPoint = new Vector3(
                (float)ExpandGlyph.ActualWidth / 2f,
                (float)ExpandGlyph.ActualHeight / 2f,
                0f);

            float target = expanded ? MathF.PI : 0f;
            if (!MotionEnabled())
            {
                visual.RotationAngle = target;
                return;
            }

            var rotation = visual.Compositor.CreateScalarKeyFrameAnimation();
            rotation.InsertKeyFrame(1f, target,
                visual.Compositor.CreateCubicBezierEasingFunction(
                    new Vector2(0.23f, 1f), new Vector2(0.32f, 1f)));
            rotation.Duration = TimeSpan.FromMilliseconds(180);
            visual.StartAnimation("RotationAngle", rotation);
        }

        // ── Keyboard / popup helpers ─────────────────────────────────────────────
        /// <summary>
        /// Esc hides the window in both densities. The title bar has no caption buttons any
        /// more, so the keyboard is one of the exits (the others: tray menu → 退出, and
        /// losing focus). Esc is skipped while a ComboBox drop-down / ContentDialog popup is
        /// open, because Esc belongs to that popup first.
        /// </summary>
        private void SetupEscapeToHide()
        {
            var escape = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
            escape.Invoked += (KeyboardAccelerator _, KeyboardAcceleratorInvokedEventArgs args) =>
            {
                if (HasOpenPopup()) return;
                args.Handled = true;
                HideWindow("escape");
            };
            RootGrid.KeyboardAccelerators.Add(escape);
        }

        /// <summary>True while a drop-down / dialog popup of this XamlRoot is open.</summary>
        private bool HasOpenPopup()
        {
            try
            {
                XamlRoot? root = RootGrid.XamlRoot;
                return root is not null
                    && Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Count > 0;
            }
            catch { return false; }
        }

        // ── Page height (R2 interaction pass) ────────────────────────────────────
        /// <summary>
        /// Window height the current page wants. Home keeps the two design densities
        /// (116 collapsed / 280 expanded); every other page asks for a window as tall as its
        /// own content.
        /// </summary>
        private int CurrentTargetHeightDip(string tag, Page? page)
        {
            if (string.Equals(tag, "Home", StringComparison.Ordinal) || page is null)
                return TargetSize(_isExpanded).Height;
            return SecondaryPageHeightDip(page);
        }

        /// <summary>
        /// Content-adaptive height for a secondary page. The number is read from the page's
        /// own ScrollViewer extent after a layout pass (no per-page constant): the extent is
        /// the full content height regardless of the current viewport, so the window can be
        /// sized before/while the page fades in.
        ///
        /// R3: the result is capped at <see cref="SecondaryPageMaxHeightDip"/> (520 DIP of
        /// window = 480 DIP of page) before the work-area clamp, because the user scrolls a
        /// long page with the wheel instead of growing a screen-high panel. Above the cap the
        /// page's ScrollViewer scrolls (every secondary page already has one with
        /// VerticalScrollBarVisibility=Auto), so nothing is lost. The cap also keeps the
        /// bottom-right anchor: the window still grows up/left from the same corner.
        /// </summary>
        private int SecondaryPageHeightDip(Page page)
        {
            double contentHeight;
            try
            {
                page.UpdateLayout();     // realize templates / list items, then read the extent
                ScrollViewer? sv = FindScrollViewer(page);
                contentHeight = sv is null
                    ? ExpandedHeight - TitleBarHeightDip
                    : sv.ExtentHeight + sv.Padding.Top + sv.Padding.Bottom;

                // X1: a page-level action bar is pinned outside the ScrollViewer (it carries
                // the name PageFooter and is 44 DIP). Its height is not part of the scroll
                // extent, so add it or the buttons get cut by the window edge.
                if (page.FindName("PageFooter") is FrameworkElement footer &&
                    footer.Visibility == Visibility.Visible)
                {
                    contentHeight += footer.ActualHeight;
                }
            }
            catch (Exception ex)
            {
                ShellLog.Write("layout", $"page height measure failed: {ex.Message}");
                contentHeight = ExpandedHeight - TitleBarHeightDip;
            }

            var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            double workHeightDip = work.Height * 96.0 / Math.Max(1u, GetDpiForWindow(Hwnd));
            double maxHeightDip = Math.Max(PageMinHeightDip, workHeightDip - (PlacementMarginDip * 2));
            maxHeightDip = Math.Min(maxHeightDip, SecondaryPageMaxHeightDip);
            double wanted = TitleBarHeightDip + Math.Ceiling(contentHeight);
            int height = (int)Math.Round(Math.Clamp(wanted, PageMinHeightDip, maxHeightDip));
            ShellLog.Write("layout",
                $"page={page.GetType().Name} content={contentHeight:F0}DIP wanted={wanted:F0}DIP " +
                $"cap={SecondaryPageMaxHeightDip}DIP workArea={workHeightDip:F0}DIP → {height}DIP");
            return height;
        }

        private static ScrollViewer? FindScrollViewer(DependencyObject root)
        {
            int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
                if (child is ScrollViewer sv) return sv;
                if (FindScrollViewer(child) is { } nested) return nested;
            }
            return null;
        }

        // ── Navigation ───────────────────────────────────────────────────────────
        /// <summary>
        /// Switches the page displayed in the single window. Home is the only page that can
        /// be shown in the collapsed density, so opening any other page expands the shell.
        /// <paramref name="allowRedirect"/> = false is the shell's own restore path (no window
        /// animation, no navigation log line); <paramref name="userInitiated"/> = false is
        /// back navigation, which logs "back X -> Y" itself instead of "navigate X -> Y".
        /// Settings sub-pages are resolved by <see cref="PageBackTargets"/>; every other hop is
        /// recorded for the pages that have two entries (Fans).
        /// </summary>
        internal void NavigateTo(string tag, bool allowRedirect = true, bool userInitiated = true)
        {
            if (!PageTypes.TryGetValue(tag, out Type? pageType)) return;

            string from = _currentTag;
            if (allowRedirect && userInitiated && !string.Equals(tag, from, StringComparison.Ordinal))
            {
                ShellLog.Write("nav", $"navigate {from} -> {tag}");

                // Settings sub-pages are not recorded: PageBackTargets already says where they
                // return, and a history entry on top of that could only drift (for example
                // after back returned to Settings instead of popping a matching entry).
                if (!PageBackTargets.ContainsKey(tag))
                {
                    _navBackStack.Add(from);
                    if (_navBackStack.Count > 8) _navBackStack.RemoveAt(0);
                }
            }

            if (!_pages.TryGetValue(tag, out Page? page))
            {
                // A page that fails to construct (bad StaticResource, bad x:Bind, a throwing
                // property getter) must not take the shell down with it: log the full exception
                // and stay on the current page. This is also how the integration pass found the
                // Settings page's crash.
                try
                {
                    page = (Page)Activator.CreateInstance(pageType)!;

                    // Page contract (SHELL-CONTRACT §3): the shell owns the ViewModel instance and
                    // hands it to the page right after construction, before the page is mounted.
                    // Pages raise PropertyChanged("Vm") / call Bindings.Update() so a late
                    // assignment still re-evaluates every compiled binding.
                    if (page is IPredexPage bindable) bindable.Vm = ViewModel;
                }
                catch (Exception ex)
                {
                    ShellLog.Write("nav", $"page {tag} construction FAILED: {ex}");
                    return;
                }

                _pages[tag] = page;
                ShellLog.Write("nav", $"created page {tag} ({pageType.Name}, Vm={(page is IPredexPage ? "assigned" : "n/a")})");
            }

            if (PageHost.Children.Count != 1 || !ReferenceEquals(PageHost.Children[0], page))
            {
                PageHost.Children.Clear();
                PageHost.Children.Add(page);
                PlayPageTransition();
            }

            _currentTag = tag;
            UpdateChrome();

            // Window height and content fade start together: PlayPageTransition() above fades
            // the host in while this resizes the window into the new page, so the window is
            // never stretched empty first and filled afterwards.
            int targetHeight = CurrentTargetHeightDip(tag, page);
            if (allowRedirect)
            {
                if (tag == "Home" && HomeIfCreated() is { } home) home.SetExpandedState(_isExpanded);
                AnimateWindowSize(targetHeight, ExpandDurationMs, 0);
            }

            ShellLog.Write("nav",
                $"page={tag} expanded={_isExpanded} targetHeight={targetHeight}DIP");
            DispatcherQueue.TryEnqueue(LogLayout);
            DispatcherQueue.TryEnqueue(FocusPageRoot);
        }

        /// <summary>
        /// Where the back button goes from <paramref name="tag"/>: the fixed parent from
        /// <see cref="PageBackTargets"/> (Settings sub-pages → Settings), otherwise the page
        /// that opened it (Tuning → Fans → Tuning), otherwise Home.
        /// </summary>
        private string BackTargetFor(string tag)
        {
            if (PageBackTargets.TryGetValue(tag, out string? parent)) return parent;
            return _navBackStack.Count > 0 ? _navBackStack[^1] : "Home";
        }

        /// <summary>
        /// R4 bug 4: a hotkey summons the window and WinUI treats that as keyboard input, so
        /// the first focusable control (mode tile / back button / Apply) got focus and drew a
        /// white focus ring. Focus goes to the page root instead — the pages set
        /// IsTabStop=True + UseSystemFocusVisuals=False — and Tab still walks into the first
        /// control from there.
        /// </summary>
        private void FocusPageRoot()
        {
            try
            {
                if (PageHost.Children.Count == 0) return;
                if (PageHost.Children[0] is Control page) page.Focus(FocusState.Programmatic);
            }
            catch (Exception ex) { ShellLog.Write("focus", $"page focus failed: {ex.Message}"); }
        }

        /// <summary>
        /// R5: the mode tiles / the chevron kept the white system focus rectangle because a
        /// hotkey summon is keyboard input, so WinUI put keyboard focus on the first focusable
        /// control. Focus goes to RootGrid instead (IsTabStop + no focus visual): the outlined
        /// tile was reading as a second "selected" next to the accent selection. Tab still
        /// walks into the first control from the root.
        /// </summary>
        private void ResetShellFocus()
        {
            // RootGrid is a Grid (a Panel, not a Control) and cannot be a tab stop; the page
            // root is a Page (Control) with IsTabStop=True + UseSystemFocusVisuals=False.
            FocusPageRoot();
        }

        private Page? CurrentPage()
            => PageHost.Children.Count > 0 ? PageHost.Children[0] as Page : null;

        private HomePage? HomeIfCreated()
            => _pages.TryGetValue("Home", out Page? page) ? page as HomePage : null;

        /// <summary>Layout diagnostics for --trace (title bar vs page host geometry).</summary>
        private void LogLayout()
        {
            try
            {
                var hostOrigin = PageHost.TransformToVisual(RootGrid).TransformPoint(new Windows.Foundation.Point(0, 0));
                double pageY = -1, pageHeight = -1;
                if (PageHost.Children.Count > 0 && PageHost.Children[0] is FrameworkElement page)
                {
                    pageY = page.TransformToVisual(RootGrid).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
                    pageHeight = page.ActualHeight;
                }
                ShellLog.Write("layout",
                    $"root={RootGrid.ActualWidth:F0}x{RootGrid.ActualHeight:F0} " +
                    $"titleH={TitleBarRoot.ActualHeight:F0} host={hostOrigin.X:F0},{hostOrigin.Y:F0} " +
                    $"hostSize={PageHost.ActualWidth:F0}x{PageHost.ActualHeight:F0} " +
                    $"pageY={pageY:F0} pageH={pageHeight:F0}");
            }
            catch (Exception ex) { ShellLog.Write("layout", $"failed: {ex.Message}"); }
        }

        /// <summary>
        /// S6 hidden-memory pass: drops the whole page visual tree (current page included), not
        /// just the inactive page instances. A Page that is still a child of PageHost keeps its
        /// entire XAML tree, its x:Bind table and its Composition visuals alive even after the
        /// dictionary entry is removed, so it has to be detached first. The page is rebuilt by
        /// <see cref="RestoreCurrentPage"/> the next time the window is shown.
        /// </summary>
        private void ReleasePageTree()
        {
            int released = _pages.Count;
            try
            {
                PageHost.Children.Clear();
                _pages.Clear();
            }
            catch (Exception ex)
            {
                ShellLog.Write("nav", $"page tree release failed: {ex.Message}");
                return;
            }

            // Let the dispatcher finish the detach (Unloaded handlers, layout queue, the
            // telemetry subscriptions the page dropped), then collect and return the memory to
            // the OS. MemoryManager.TrimNow() is the same GC.Collect + SetProcessWorkingSetSize
            // pair the idle trimmer runs; calling it here makes the release immediate instead of
            // waiting for the 8 s idle delay.
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    MemoryManager.TrimNow();
                    System.Diagnostics.Process self = System.Diagnostics.Process.GetCurrentProcess();
                    ShellLog.Write("mem",
                        $"page tree released ({released} page(s), current={_currentTag}); " +
                        $"workingSet={self.WorkingSet64 / (1024 * 1024)}MB " +
                        $"private={self.PrivateMemorySize64 / (1024 * 1024)}MB");
                }
                catch (Exception ex) { ShellLog.Write("mem", $"trim failed: {ex.Message}"); }
            });
        }

        /// <summary>Rebuilds the page that was open when the window hid (S6).</summary>
        private void RestoreCurrentPage()
        {
            if (PageHost.Children.Count > 0) return;
            ShellLog.Write("nav", $"restoring page {_currentTag} after hide");
            NavigateTo(_currentTag, allowRedirect: false);
            if (_currentTag == "Home" && HomeIfCreated() is { } home)
                home.SetExpandedState(_isExpanded);
        }

        private void UpdateChrome()
        {
            bool home = string.Equals(_currentTag, "Home", StringComparison.Ordinal);
            HomeChrome.Visibility = home ? Visibility.Visible : Visibility.Collapsed;
            PageChrome.Visibility = home ? Visibility.Collapsed : Visibility.Visible;
            ExpandButton.Visibility = home ? Visibility.Visible : Visibility.Collapsed;
            PageTitleText.Text = Loc.T(PageTitleKeys[_currentTag]);
            UpdateBackTooltip();
        }

        /// <summary>
        /// The back button's tooltip names where it goes (Settings for a settings sub-page,
        /// Tuning when Fans was opened from there, Home otherwise) instead of always "Home".
        /// </summary>
        private void UpdateBackTooltip()
            => ToolTipService.SetToolTip(BackButton, Loc.T(PageTitleKeys[BackTargetFor(_currentTag)]));

        /// <summary>
        /// Back button: returns to the page this one belongs to. The four Settings sub-pages go
        /// back to Settings through <see cref="PageBackTargets"/> — the table survives the
        /// hide/show page-tree rebuild, so back is the same target whether or not the window was
        /// re-summoned in between. Pages with two entries (Fans: Home and Tuning) fall back to
        /// the recorded hop, and a page without either falls back Home.
        /// </summary>
        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            string from = _currentTag;
            string target = BackTargetFor(from);
            if (!PageBackTargets.ContainsKey(from) && _navBackStack.Count > 0)
                _navBackStack.RemoveAt(_navBackStack.Count - 1);
            ShellLog.Write("nav", $"back {from} -> {target}");
            NavigateTo(target, userInitiated: false);
        }

        // ── Show / hide / quit ───────────────────────────────────────────────────
        public void ShowAndActivate()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isExplicitExit) return;
                // S6: the page tree is dropped while hidden. Rebuild the page the user left
                // inside the window before placing/measuring it, otherwise the window would
                // come back with an empty body at the home density.
                RestoreCurrentPage();
                // Restore the height the current page wants, not just the home density: a
                // page that was open when the window hid must come back at its own height.
                PlaceBottomRight(WindowWidth, CurrentTargetHeightDip(_currentTag, CurrentPage()));
                AppWindow.Show();
                Activate();
                ForceForeground();
                PlayShowAnimation();
                _services?.NotifyWindowShown();
                ViewModel.SetMainWindowTelemetryVisible(true);
                _ignoreDeactivateUntil = DateTime.UtcNow.AddMilliseconds(700);
                ResetShellFocus();
                ShellLog.Write("window", "shown");
            });
        }

        public void InitTrayOnly()
        {
            // Created hidden: the tray icon lives on this window's HWND, so only hide the
            // chrome. Telemetry stays off until the user opens the window.
            AppWindow.Hide();
            ViewModel.SetMainWindowTelemetryVisible(false);
            ShellLog.Write("window", "started hidden (tray only)");
        }

        public void ToggleVisibility()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isExplicitExit) return;
                if (AppWindow.IsVisible && IsAppForeground()) HideWindow("toggle");
                else ShowAndActivate();
            });
        }

        /// <summary>
        /// Tray icon left-click: hide when the window is visible, show it otherwise. Unlike
        /// the Predator key (which must summon the panel while another app has focus) this is
        /// a plain toggle — clicking the tray icon again closes the panel (R2 interaction
        /// pass: with the caption buttons gone, the tray icon is the obvious second click).
        /// </summary>
        public void TrayClicked()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isExplicitExit) return;
                if (AppWindow.IsVisible) HideWindow("tray-toggle");
                else ShowAndActivate();
            });
        }

        private void HideWindow(string reason)
        {
            if (!AppWindow.IsVisible || _isExplicitExit) return;
            ShellLog.Write("window", $"hide requested ({reason})");
            PlayHideAnimation(onComplete: () =>
            {
                AppWindow.Hide();
                _services?.NotifyWindowHidden();
                ViewModel.SetMainWindowTelemetryVisible(false);
                ReleasePageTree();
                ShellLog.Write("window", "hidden");
            });
        }

        public void QuitApplication()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isExplicitExit) return;
                _isExplicitExit = true;
                ShellLog.Write("window", "quit");
                try { ViewModel.PersistSessionSettings(); } catch { }
                PreySense.Frontend.Views.ModeToastWindow.Shutdown();
                _services?.Dispose();
                _services = null;
                ViewModel.Dispose();
                App.RequestExit();
            });
        }

        // ── Events ───────────────────────────────────────────────────────────────
        private void Window_Activated(object sender, WindowActivatedEventArgs args)
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated)
            {
                if (Predex.WinUI.App.NoAutoHide || _isExplicitExit) return;
                if (DateTime.UtcNow < _ignoreDeactivateUntil) return;
                QueueAutoHide();
                return;
            }

            _ignoreDeactivateUntil = DateTime.UtcNow.AddMilliseconds(400);
            _services?.NotifyWindowShown();
            ViewModel.SetMainWindowTelemetryVisible(true);
        }

        /// <summary>
        /// Auto-hide after the window loses focus. Delayed and foreground-checked so opening
        /// our own tray menu or a modal dialog (same process) does not hide the window.
        /// </summary>
        private void QueueAutoHide()
        {
            _autoHideTimer ??= DispatcherQueue.CreateTimer();
            _autoHideTimer.Stop();
            _autoHideTimer.Interval = TimeSpan.FromMilliseconds(220);
            _autoHideTimer.IsRepeating = false;
            _autoHideTimer.Tick += AutoHideTick;
            _autoHideTimer.Start();

            void AutoHideTick(DispatcherQueueTimer timer, object args)
            {
                timer.Stop();
                timer.Tick -= AutoHideTick;
                if (Predex.WinUI.App.NoAutoHide || _isExplicitExit) return;
                if (!AppWindow.IsVisible) return;
                if (IsAppForeground()) return;
                // A drop-down / dialog popup means the user is still working in the window
                // even when the foreground window is not ours (clicking a ComboBox must not
                // hide the panel out from under the list).
                if (HasOpenPopup()) return;
                HideWindow("deactivated");
            }
        }


        private void OnWindowClosed(object sender, WindowEventArgs args)
        {
            if (_isExplicitExit) return;
            args.Handled = true;      // close = hide to tray, never exit
            HideWindow("close");
        }

        private void OnLanguageChanged(object? sender, EventArgs e) => ApplyLocalizedChrome();

        private void ApplyLocalizedChrome()
        {
            PageTitleText.Text = Loc.T(PageTitleKeys[_currentTag]);
            ToolTipService.SetToolTip(ExpandButton, Loc.T(_isExpanded ? "Collapse" : "Expand"));
            UpdateBackTooltip();
        }


        // ── Toast (S6: ModeToastWindow / InfoWindowService replacement) ──────────
        /// <summary>
        /// Slides the shell InfoBar in at the top of the page area and closes it again after 3 s.
        /// Thread-safe (InfoWindowService can be called from a background task): the work is
        /// marshalled to the dispatcher.
        ///
        /// Why an InfoBar and not the WPF InfoWindow/ModeToastWindow form: a second window is a
        /// second XAML island (own HWND, own visual tree, own composition target) whose memory
        /// would count against the hidden-to-tray target, and the shell already reserves this
        /// strip. The animation only touches "Translation" (enabled via
        /// ElementCompositionPreview.SetIsTranslationEnabled) and opacity, so it composes with
        /// the layout offset instead of overwriting it (SHELL-CONTRACT §7.1).
        /// </summary>
        internal void ShowToast(InfoBarSeverity severity, string title, string? message = null)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    _toastSession++;
                    ShellInfoBar.Severity = severity;
                    ShellInfoBar.Title = title;
                    ShellInfoBar.Message = message ?? string.Empty;
                    ShellInfoBar.IsOpen = true;
                    AnimateToast(show: true, session: _toastSession);
                    ShellLog.Write("toast", $"{severity} '{title}'" + (message is null ? "" : $": {message}"));

                    if (_toastTimer is null)
                    {
                        _toastTimer = DispatcherQueue.CreateTimer();
                        _toastTimer.IsRepeating = false;
                        _toastTimer.Tick += ToastTimer_Tick;
                    }
                    _toastTimer.Stop();
                    _toastTimer.Interval = TimeSpan.FromSeconds(3);
                    _toastTimer.Start();
                }
                catch (Exception ex) { ShellLog.Write("toast", $"show failed: {ex.Message}"); }
            });
        }

        private void ToastTimer_Tick(DispatcherQueueTimer timer, object args)
        {
            timer.Stop();
            ShellLog.Write("toast", "auto-dismiss after 3 s");
            AnimateToast(show: false, session: _toastSession);
        }

        private void AnimateToast(bool show, int session)
        {
            Visual visual = ElementCompositionPreview.GetElementVisual(ShellInfoBar);
            if (!_toastVisualReady)
            {
                ElementCompositionPreview.SetIsTranslationEnabled(ShellInfoBar, true);
                _toastVisualReady = true;
            }

            if (!MotionEnabled())
            {
                visual.Opacity = show ? 1f : 0f;
                if (!show) ShellInfoBar.IsOpen = false;
                return;
            }

            float duration = show ? 160f : 140f;
            var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0f, show ? 0f : 1f);
            fade.InsertKeyFrame(1f, show ? 1f : 0f,
                visual.Compositor.CreateCubicBezierEasingFunction(
                    new Vector2(0.23f, 1f), new Vector2(0.32f, 1f)));
            fade.Duration = TimeSpan.FromMilliseconds(duration);

            var slide = visual.Compositor.CreateVector3KeyFrameAnimation();
            slide.InsertKeyFrame(0f, show ? new Vector3(0, -8, 0) : Vector3.Zero);
            slide.InsertKeyFrame(1f, show ? Vector3.Zero : new Vector3(0, -8, 0),
                visual.Compositor.CreateCubicBezierEasingFunction(
                    new Vector2(0.23f, 1f), new Vector2(0.32f, 1f)));
            slide.Duration = TimeSpan.FromMilliseconds(duration);

            visual.StartAnimation("Opacity", fade);
            visual.StartAnimation("Translation", slide);

            if (show) return;

            var close = DispatcherQueue.CreateTimer();
            close.Interval = TimeSpan.FromMilliseconds(duration + 20);
            close.IsRepeating = false;
            close.Tick += (DispatcherQueueTimer t, object _) =>
            {
                t.Stop();
                // A newer toast may have replaced this one while it was fading out.
                if (session != _toastSession) return;
                ShellInfoBar.IsOpen = false;
                visual.Opacity = 1f;
                visual.Properties.InsertVector3("Translation", Vector3.Zero);
            };
            close.Start();
        }

        /// <summary>
        /// UI verification helper, wired to <c>--demo=notify|confirm|toast</c> (S6 acceptance:
        /// "the prompt layer can actually be shown"). The confirm/notify text is the real GPU
        /// MUX / power-cycle text, but this path never calls a ViewModel command, so no EC /
        /// WMI / NVAPI write and no restart can happen.
        /// </summary>
        internal void RunUiDemo(string kind)
        {
            // OSD overlay demo: no window resize, no hardware, just the overlay on a cycle.
            if (kind.StartsWith("osd", StringComparison.OrdinalIgnoreCase))
            {
                RunOsdDemo(kind);
                return;
            }

            // The demo screenshots need the expanded home density: a 116 DIP collapsed window
            // clips the ContentDialog and the InfoBar. Expanding is pure chrome, no hardware.
            // Delayed by 400 ms so the window is already shown/loaded: expanding from OnLaunched
            // starts the content fade-in before the page is in the XamlRoot, and that animation
            // is dropped (the expanded groups then stay at opacity 0).
            var expand = DispatcherQueue.CreateTimer();
            expand.Interval = TimeSpan.FromMilliseconds(400);
            expand.IsRepeating = false;
            expand.Tick += (DispatcherQueueTimer t, object _) =>
            {
                t.Stop();
                if (!_isExpanded) SetExpanded(true);
            };
            expand.Start();

            void ShowDemoToast() => ShowToast(InfoBarSeverity.Success, Loc.T("Performance Mode"),
                "Beast — demo toast, no mode was changed");

            if (string.Equals(kind, "toast", StringComparison.OrdinalIgnoreCase))
            {
                // First toast 2 s after the window is up (DispatcherQueueTimer cannot use a
                // different first delay), then repeat every 7 s: the strip is visible for 3 s
                // and gone for 4 s, which is a reachable timing for the screenshot script.
                var first = DispatcherQueue.CreateTimer();
                first.Interval = TimeSpan.FromSeconds(2);
                first.IsRepeating = false;
                first.Tick += (DispatcherQueueTimer t, object _) =>
                {
                    t.Stop();
                    ShowDemoToast();
                    var repeat = DispatcherQueue.CreateTimer();
                    repeat.Interval = TimeSpan.FromSeconds(7);
                    repeat.IsRepeating = true;
                    repeat.Tick += (DispatcherQueueTimer _, object __) => ShowDemoToast();
                    repeat.Start();
                };
                first.Start();
                return;
            }

            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(2);
            timer.IsRepeating = false;
            timer.Tick += (DispatcherQueueTimer t, object _) =>
            {
                switch (kind.ToLowerInvariant())
                {
                    case "notify":
                        t.Stop();
                        _ = Prompts.NotifyAsync(
                            "Performix • Discrete GPU Power Cycle",
                            " • Discrete GPU Power Cycle",
                            "Demo notification from the WinUI prompt layer. No hardware action was performed.",
                            "OK");
                        break;
                    case "confirm":
                        t.Stop();
                        _ = Prompts.ConfirmAsync(
                            "Performix • Hardware Discrete GPU",
                            " • Hardware Display MUX",
                            "Would you like to switch to dGPU Only mode and restart now? " +
                            "(demo — nothing is written and no restart happens)",
                            "Apply & Restart Now",
                            "Cancel");
                        break;
                }
            };
            timer.Start();
        }

        /// <summary>
        /// <c>--demo=osd</c> / <c>--demo=osd:&lt;mode&gt;</c> (verification only): replays the
        /// mode-switch overlay on a 4.2 s cycle so docs\osd-verify.ps1 can capture the entry,
        /// settled and exit frames and the final state of all five modes. The mode name goes
        /// through the same Loc.T table the real notification uses, and no ViewModel command is
        /// invoked — this path cannot write the EC, the fans or the keyboard lighting.
        /// </summary>
        private void RunOsdDemo(string kind)
        {
            var modes = new (byte Mode, string Key)[]
            {
                (0x06, "Eco"),
                (0x00, "Quiet"),
                (0x01, "Balanced"),
                (0x04, "Performance"),
                (0x05, "Turbo"),        // localized as 狂暴 / Beast
            };

            var script = new List<(byte Mode, string Key)>();
            bool all = kind.Equals("osd", StringComparison.OrdinalIgnoreCase);
            if (all)
            {
                script.AddRange(modes);
            }
            else
            {
                string arg = kind.Length > "osd:".Length ? kind["osd:".Length..] : string.Empty;
                (byte Mode, string Key) pick = modes[4];
                bool matched = false;
                foreach (var candidate in modes)
                {
                    if ((byte.TryParse(arg, out byte raw) && candidate.Mode == raw) ||
                        candidate.Key.Equals(arg, StringComparison.OrdinalIgnoreCase) ||
                        (candidate.Mode == 0x05 && arg.Equals("Beast", StringComparison.OrdinalIgnoreCase)))
                    {
                        pick = candidate;
                        matched = true;
                        break;
                    }
                }
                if (!matched && arg.Length > 0)
                    ShellLog.Write("osd", $"demo: unknown mode '{arg}', falling back to {pick.Key}");
                script.Add(pick);
            }

            ShellLog.Write("osd",
                $"demo: {string.Join(", ", script.ConvertAll(s => s.Key))} on a 4.2 s cycle " +
                "(display only, no hardware write)");

            int index = 0;
            void ShowNext()
            {
                (byte Mode, string Key) item = script[index % script.Count];
                index++;
                Views.ModeOsdWindow.ShowMode(item.Mode, Loc.T(item.Key));
            }

            var first = DispatcherQueue.CreateTimer();
            first.Interval = TimeSpan.FromMilliseconds(1500);
            first.IsRepeating = false;
            first.Tick += (DispatcherQueueTimer t, object _) =>
            {
                t.Stop();
                ShowNext();
                var repeat = DispatcherQueue.CreateTimer();
                repeat.Interval = TimeSpan.FromMilliseconds(4200);
                repeat.IsRepeating = true;
                repeat.Tick += (DispatcherQueueTimer _, object __) => ShowNext();
                repeat.Start();
            };
            first.Start();
        }

        // ── Foreground helpers ───────────────────────────────────────────────────
        private bool IsAppForeground()
        {
            IntPtr foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero) return false;
            GetWindowThreadProcessId(foreground, out uint pid);
            return pid == (uint)Environment.ProcessId;
        }

        private void ForceForeground()
        {
            IntPtr hwnd = Hwnd;
            SetForegroundWindow(hwnd);
            if (GetForegroundWindow() == hwnd) return;

            IntPtr foreground = GetForegroundWindow();
            uint targetThread = GetWindowThreadProcessId(hwnd, out _);
            uint foregroundThread = foreground != IntPtr.Zero ? GetWindowThreadProcessId(foreground, out _) : 0;
            if (foregroundThread == 0 || foregroundThread == targetThread ||
                !AttachThreadInput(targetThread, foregroundThread, true))
            {
                SetForegroundWindow(hwnd);
                return;
            }
            try { SetForegroundWindow(hwnd); Activate(); }
            finally { AttachThreadInput(targetThread, foregroundThread, false); }
        }
    }
}
