// ModeOsdWindow — the desktop-level "mode switched" confirmation overlay.
//
// Implements docs\TOAST-ANIMATION-SPEC.md ("模式切换确认浮层 · WinUI 3 动效规范") on top of a
// second, lazily created top-level window.
//
// HOST = CARD (deviation from spec §1's "window height = card + 32", see docs\R6-OSD.md):
// WinUI 3 top-level windows cannot be per-pixel transparent, so any window larger than the
// card shows an opaque slab around it (the first build produced exactly that: a black 32 DIP
// band under the card, reported by the user). The host window is therefore exactly the card
// rectangle; DWM rounds its outer corners (DWMWCP_ROUND) and the card's own 8 DIP radius sits
// inside that clip. Because the window *is* the card:
//
//   • the +24 DIP entry slide / +16 DIP exit slide move the window frame, driven per frame by
//     CompositionTarget.Rendering with a Stopwatch clock and a 16 ms DispatcherQueueTimer
//     fallback — positions are interpolated from real elapsed time, so a 250 ms slide really
//     takes 250 ms (a frame-count driver measured ~490 ms on this machine and failed the
//     spec's ±17 ms tolerance);
//   • the fade is the window's constant alpha (WS_EX_LAYERED + LWA_ALPHA). That is the only
//     way to fade the whole card: with an opaque host window, animating the card visual's
//     Opacity would fade it over the window's black background instead of over the desktop.
//     On screen this is exactly "the card fades in/out", and the desktop stays visible through
//     the card while it fades.
//
//  • Host window (spec §1): borderless, WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW, always on top,
//    not in the taskbar/Alt+Tab, never takes focus.
//  • Placement: horizontally centred on the primary display's work area, card bottom 24 DIP
//    above the work-area bottom (the arithmetic lives in ModeOsdPlacement.cs).
//  • ONE HWND (spec §3): the window, its XAML island and the Composition visuals are created
//    on the first confirmed mode change and reused for the rest of the process. A repeated
//    Show only swaps glyph+text and replays the icon animation — it never re-plays the entry
//    animation, never re-creates the window.
//  • LIVE GEOMETRY, not a snapshot: the HWND outliving the display mode is exactly why the
//    work area, the DPI and therefore the DIP→px scale are re-read on every Show (and the
//    overlay is re-anchored when they change while it is on screen). A resolution or
//    refresh-rate switch used to leave the card on the old work-area bottom — off-screen or
//    clipped after dropping to a smaller mode. See RefreshDisplay / ApplyLayout /
//    ReanchorAfterDisplayChange and the `osd: geometry:` trace line they write.
//  • Icon motion (spec §5) is a Composition animation on the icon visual only (Translation /
//    Opacity / Scale, CenterPoint fixed at (10,10,0)), one iteration, delayed 83 ms.
//  • Dwell 3000 ms (DispatcherQueueTimer) from the end of the entry; hover pauses it; a switch
//    while holding only swaps text/icon and resets the timer; a switch during the exit aborts
//    it and restores from the live value in 167 ms (spec §3).
//  • Degraded path (spec §7) when UISettings.AnimationsEnabled == false (or --no-anim): the
//    window does not move and only the 83 ms linear fade remains, no icon animation.
//
// Evidence / verification hooks: `--trace` writes the measured duration of every phase
// (entry / dwell / exit / restore) next to the number the spec declares to shell.log;
// `--demo=osd[:<mode>]` (App.xaml.cs → ShellWindow.RunUiDemo) shows the overlay on a cycle so
// docs\osd-verify.ps1 can capture the entry/hold/exit frames and measure the card's real
// displacement from the window rect, without touching hardware.

using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Predex.WinUI.Services;
using PreySense.Frontend.Services;
using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace Predex.WinUI.Views
{
    /// <summary>
    /// Bottom-centre "mode confirmed" overlay (TOAST-ANIMATION-SPEC). One instance per process,
    /// created on the first confirmed mode change and reused afterwards.
    /// </summary>
    public sealed partial class ModeOsdWindow : Window
    {
        // ── Spec geometry (DIP) ──────────────────────────────────────────────────
        // The card rectangle (height, width bounds, padding, bottom margin) lives in
        // ModeOsdPlacement.cs so the same numbers can be unit-tested off a WinUI host.
        /// <summary>Spec §2: entry slide starts 24 DIP below the rest position.</summary>
        private const float EntrySlideDip = 24f;
        /// <summary>Spec §4: exit slide moves 16 DIP down.</summary>
        private const float ExitSlideDip = 16f;

        // ── Spec timing ──────────────────────────────────────────────────────────
        private static readonly TimeSpan EntrySlideDuration = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan EntryFadeDuration = TimeSpan.FromMilliseconds(167);
        private static readonly TimeSpan ExitDuration = TimeSpan.FromMilliseconds(167);
        private static readonly TimeSpan DwellDuration = TimeSpan.FromMilliseconds(3000);
        private static readonly TimeSpan IconDelay = TimeSpan.FromMilliseconds(83);
        private static readonly TimeSpan DegradedFadeDuration = TimeSpan.FromMilliseconds(83);
        private static readonly TimeSpan RestoreDuration = TimeSpan.FromMilliseconds(167);

        /// <summary>Fallback cadence for the window-motion driver (one frame at 60 Hz).</summary>
        private const int MotionFrameMs = 16;

        // Cubic-bezier control points (spec §2 / §4).
        private const double EntryX1 = 0.0, EntryY1 = 0.0, EntryX2 = 0.0, EntryY2 = 1.0;
        private const double ExitX1 = 0.7, ExitY1 = 0.0, ExitX2 = 1.0, ExitY2 = 0.5;
        // cubic-bezier(0, 0, 1, 1) is the identity curve, i.e. linear (spec §7).
        private const double LinearX1 = 0.0, LinearY1 = 0.0, LinearX2 = 1.0, LinearY2 = 1.0;

        // ── Process-wide instance ────────────────────────────────────────────────
        private static ModeOsdWindow? _instance;
        private static DispatcherQueue? _queue;
        private static readonly UISettings UiSettings = new();

        /// <summary>Debug override for spec §7 (<c>--no-anim</c>): pretend AnimationsEnabled=false.</summary>
        internal static bool ForceNoMotion { get; set; }

        private enum Phase { Hidden, Entering, Holding, Exiting }

        /// <summary>The five modes, named the way the spec's §5 table names them.</summary>
        private enum MotionMode { Eco, Silent, Balanced, Performance, Beast }

        /// <summary>One window-motion run: the slide (px) and/or the fade (0…1), all time-based.</summary>
        private sealed class WindowMotion
        {
            public int FromY, ToY;
            public double MoveMs;
            public double MoveX1, MoveY1, MoveX2, MoveY2;
            public double FromAlpha, ToAlpha;
            public double AlphaMs;
            public double AlphaX1 = LinearX1, AlphaY1 = LinearY1, AlphaX2 = LinearX2, AlphaY2 = LinearY2;
            public Action? OnDone;
            /// <summary>
            /// True when both endpoints were derived from the rest position (the entry slide), so a
            /// display change has to shift the whole path. False for the exit/restore slides, whose
            /// start is "wherever the card is right now" — those only retarget their end point.
            /// </summary>
            public bool RestAnchored;
        }

        private readonly IntPtr _hwnd;
        private readonly Visual _card;
        private readonly Visual _icon;
        private readonly Compositor _compositor;
        // Easings from docs\ICON-MICRO-ANIMATIONS.md §2.4.
        private readonly CompositionEasingFunction _easeDecel;     // (0.00, 0.00, 0.00, 1.00)
        private readonly CompositionEasingFunction _easeStandard;  // (0.80, 0.00, 0.20, 1.00)
        private readonly CompositionEasingFunction _easeSine;      // (0.37, 0.00, 0.63, 1.00)
        private readonly CompositionEasingFunction _easeSnap;      // (0.20, 0.00, 0.00, 1.00)
        private readonly DispatcherQueueTimer _dwellTimer;
        private readonly DispatcherQueueTimer _motionTimer;   // fallback driver for the frame motion
        /// <summary>Guards the icon batch completion against a newer icon animation.</summary>
        private int _iconToken;

        // Window rect (physical px), its resting Y and its current constant alpha.
        private int _winX, _winY, _winW, _winH;
        private int _restY;
        private double _winAlpha = 1.0;

        // Last resolved primary display (work area + effective DPI) and the DIP width the current
        // title measured to. Re-resolved before every Show — never carried across a resolution,
        // refresh-rate, DPI or display-topology change.
        private OsdDisplay _display = new(0, 0, 0, 0, ModeOsdPlacement.BaseDpi);
        private bool _displayKnown;
        private double _textWidthDip;
        private double _cardWidthDip = ModeOsdPlacement.CardMinWidthDip;

        private WindowMotion? _motion;
        private readonly Stopwatch _motionClock = new();
        private bool _renderingHooked;
        private SubclassProc? _subclass;

        private Phase _phase = Phase.Hidden;
        private bool _hovered;
        private int _session;
        private bool _chromeReady;
        /// <summary>Measures the phase that is running right now (entry / dwell / exit / restore).</summary>
        private readonly Stopwatch _clock = new();

        // ── Public surface ───────────────────────────────────────────────────────

        /// <summary>Wires the dispatcher used when the presenter is invoked off the UI thread.</summary>
        internal static void Initialize(DispatcherQueue queue) => _queue = queue;

        /// <summary>
        /// Shows "mode confirmed" for the given Acer mode byte. Matches
        /// <c>MainViewModel.ModeToastPresenter</c> (<c>Action&lt;byte, string&gt;</c>): the title
        /// is already localized by the ViewModel (e.g. 狂暴 for 0x05).
        /// </summary>
        public static void ShowMode(byte mode, string title)
        {
            DispatcherQueue? queue = _queue;
            try { queue ??= App.CurrentShell?.DispatcherQueue; } catch { queue = null; }
            if (queue is null) return;

            if (!queue.HasThreadAccess)
            {
                queue.TryEnqueue(() => ShowMode(mode, title));
                return;
            }

            try
            {
                _instance ??= new ModeOsdWindow(queue);
                _instance.Present(mode, title);
            }
            catch (Exception ex)
            {
                Trace($"show failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Closes the overlay window (application quit path). Safe to call repeatedly.</summary>
        public static void Shutdown()
        {
            ModeOsdWindow? instance = _instance;
            _instance = null;
            if (instance is null) return;
            try { instance._dwellTimer.Stop(); } catch { }
            try { instance.StopMotion(); } catch { }
            try { instance.Close(); } catch { }
        }

        // ── Construction ─────────────────────────────────────────────────────────

        private ModeOsdWindow(DispatcherQueue queue)
        {
            InitializeComponent();

            _queue = queue;
            _hwnd = WindowNative.GetWindowHandle(this);

            // The icon is the only element whose Translation is composed (spec §5). "Translation"
            // has to be enabled on the element, otherwise animating it overwrites the layout
            // offset (SHELL-CONTRACT §7.1).
            ElementCompositionPreview.SetIsTranslationEnabled(ModeIcon, true);
            _card = ElementCompositionPreview.GetElementVisual(Card);
            _icon = ElementCompositionPreview.GetElementVisual(ModeIcon);
            _compositor = _card.Compositor;

            _easeDecel = _compositor.CreateCubicBezierEasingFunction(new Vector2(0f, 0f), new Vector2(0f, 1f));
            _easeStandard = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.8f, 0f), new Vector2(0.2f, 1f));
            _easeSine = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.37f, 0f), new Vector2(0.63f, 1f));
            _easeSnap = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0f), new Vector2(0f, 1f));

            // Spec §5: icon scales grow from its own centre (20×20 icon → (10, 10, 0)).
            _icon.CenterPoint = new Vector3(10f, 10f, 0f);

            _dwellTimer = _queue.CreateTimer();
            _dwellTimer.IsRepeating = false;
            _dwellTimer.Interval = DwellDuration;
            _dwellTimer.Tick += DwellTick;

            _motionTimer = _queue.CreateTimer();
            _motionTimer.IsRepeating = true;
            _motionTimer.Interval = TimeSpan.FromMilliseconds(MotionFrameMs);
            _motionTimer.Tick += MotionTick;

            Closed += OnClosed;

            Trace($"window created (hwnd 0x{_hwnd.ToInt64():X})");
        }

        private void OnClosed(object sender, WindowEventArgs args)
        {
            Trace("window closed");
            RemoveDisplayChangeHook();
            StopMotion();
            if (ReferenceEquals(_instance, this)) _instance = null;
        }

        // ── Window chrome / placement ────────────────────────────────────────────

        private static readonly IntPtr HwndTopmost = new(-1);

        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out WinRect rect);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(WinPoint point, uint flags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
        [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint colorKey, byte alpha, uint flags);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hWnd, uint attr, ref int attrValue, int size);
        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfn, UIntPtr id, UIntPtr refData);
        [DllImport("comctl32.dll")]
        private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfn, UIntPtr id);
        [DllImport("comctl32.dll")]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr refData);

        [StructLayout(LayoutKind.Sequential)]
        private struct WinRect { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinPoint { public int X, Y; }

        /// <summary>MONITORINFO — RECT order is Left/Top/Right/Bottom, same as <see cref="WinRect"/>.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public WinRect Monitor;
            public WinRect Work;
            public uint Flags;
        }

        private const uint MonitorDefaultToPrimary = 0x00000001;
        private const int MdtEffectiveDpi = 0;

        /// <summary>Desktop size / topology changed (resolution, refresh rate, monitor plug).</summary>
        private const uint WM_DISPLAYCHANGE = 0x007E;
        /// <summary>This window moved onto a monitor with a different DPI (or that DPI changed).</summary>
        private const uint WM_DPICHANGED = 0x02E0;
        private static readonly UIntPtr SubclassId = new(0x4F5344);   // 'OSD'

        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_CAPTION = 0x00C00000;
        private const int WS_THICKFRAME = 0x00040000;
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const uint LWA_ALPHA = 0x00000002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint SWP_FRAMECHANGED = 0x0020;
        private const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const uint DWMWA_BORDER_COLOR = 34;
        private const uint DWMWA_CAPTION_COLOR = 35;
        private const int DWMWCP_ROUND = 2;
        private const int DwmwaColorNone = unchecked((int)0xFFFFFFFE);

        /// <summary>
        /// Borderless, non-activating, topmost, alpha-blendable tool window. Run once, before
        /// the first Show: the styles below are what keeps the overlay out of the taskbar, out
        /// of Alt+Tab and out of the focus chain (spec §1 / §9 "浮层不获取焦点"). DWM rounds the
        /// outer corners and draws no frame line, so the only visual edge is the card's own
        /// 1 px stroke.
        /// </summary>
        private void SetupChrome()
        {
            if (_chromeReady) return;
            _chromeReady = true;

            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsAlwaysOnTop = true;
            }

            int exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
            int newExStyle = exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_LAYERED;
            SetWindowLong(_hwnd, GWL_EXSTYLE, newExStyle);

            // Constant alpha is how the card fades (the window IS the card).
            ApplyWindowAlpha(1.0);

            // No DWM frame line; DWM owns the corner rounding because the window *is* the card
            // (the card's own 8 DIP radius sits just inside it, so no opaque corner shows).
            int none = DwmwaColorNone;
            DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref none, sizeof(int));
            DwmSetWindowAttribute(_hwnd, DWMWA_CAPTION_COLOR, ref none, sizeof(int));
            int round = DWMWCP_ROUND;
            DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

            // WS_POPUP removes the non-client frame (SHELL-CONTRACT §7.2).
            int style = GetWindowLong(_hwnd, GWL_STYLE);
            int popup = (style & ~(WS_CAPTION | WS_THICKFRAME)) | WS_POPUP;
            SetWindowLong(_hwnd, GWL_STYLE, popup);
            SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

            Trace($"chrome: ex 0x{exStyle:X8} → 0x{newExStyle:X8} " +
                  "(NOACTIVATE|TOOLWINDOW|LAYERED), " +
                  $"style 0x{style:X8} → 0x{popup:X8} (WS_POPUP), DWM round corners, LWA_ALPHA fade");

            InstallDisplayChangeHook();
        }

        /// <summary>
        /// Subclasses the OSD HWND so a display change that lands *while the card is on screen*
        /// is noticed at once. This is the half of the bug a "recompute before every Show" fix
        /// cannot cover: the card dwells for 3 s and the exit slides from the old rest position,
        /// so a resolution / DPI switch during those 3.2 s left it parked on the work area of the
        /// previous mode — measured 990 px below the new screen bottom on this machine.
        ///
        /// WM_DISPLAYCHANGE covers resolution, refresh-rate and topology changes; WM_DPICHANGED
        /// covers a DPI change (Windows rescales and moves the window itself in that case, which
        /// is why the re-anchor has to re-read the real window rect instead of trusting its own).
        /// </summary>
        private void InstallDisplayChangeHook()
        {
            if (_subclass is not null) return;
            _subclass = OnOsdWindowMessage;
            if (!SetWindowSubclass(_hwnd, _subclass, SubclassId, UIntPtr.Zero))
            {
                _subclass = null;
                Trace("display-change hook: SetWindowSubclass failed, falling back to per-Show refresh only");
            }
            else
            {
                Trace("display-change hook: WM_DISPLAYCHANGE / WM_DPICHANGED armed");
            }
        }

        private void RemoveDisplayChangeHook()
        {
            if (_subclass is null) return;
            try { RemoveWindowSubclass(_hwnd, _subclass, SubclassId); } catch { }
            _subclass = null;
        }

        private IntPtr OnOsdWindowMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
                                          UIntPtr id, UIntPtr refData)
        {
            if (msg == WM_DISPLAYCHANGE || msg == WM_DPICHANGED)
            {
                // Defer: during the message the work area and the DPI can still be the old ones,
                // and DefWindowProc applies its own rescale to the WM_DPICHANGED rect after we
                // return. Re-anchoring from the dispatcher queue sees the settled values.
                _queue?.TryEnqueue(() => OnDisplayChanged(msg == WM_DPICHANGED ? "WM_DPICHANGED" : "WM_DISPLAYCHANGE"));
            }
            return DefSubclassProc(hWnd, msg, wParam, lParam);
        }

        /// <summary>Display moved under a visible card: re-measure, re-place, retarget the slide.</summary>
        private void OnDisplayChanged(string source)
        {
            if (_phase == Phase.Hidden) return;      // the next Show refreshes anyway

            bool changed = RefreshDisplay();
            // Even when the work area reads the same, Windows may have rescaled the window on a
            // DPI change; re-applying the rect is cheap and puts the card back where it belongs.
            Trace($"{source}: card on screen (phase {_phase}), geometry changed={changed}");
            ReanchorAfterDisplayChange();
        }

        /// <summary>
        /// Live work area + effective DPI of the primary display — where the overlay is placed.
        ///
        /// WHY GetMonitorInfo AND NOT DisplayArea.Primary: the two are read from the *same*
        /// monitor handle here, so the work area and the DPI can never describe two different
        /// display states, and GetMonitorInfo is documented to reflect a mode change as soon as
        /// WM_DISPLAYCHANGE has been handled. DisplayArea.Primary is kept as a fallback only.
        ///
        /// WHY NOT GetDpiForWindow AS THE PRIMARY SOURCE: a window that has never been shown
        /// reports 96 on its first frame, which produced a 160×48 px card at the wrong
        /// coordinates on a 150 % display (one observed run). The monitor query is deterministic
        /// regardless of window state; GetDpiForWindow stays as the last-resort fallback.
        ///
        /// The primary display always owns the virtual-screen origin, so (0,0) identifies it;
        /// MONITOR_DEFAULTTOPRIMARY covers a topology where it somehow does not.
        /// </summary>
        private OsdDisplay QueryDisplay()
        {
            IntPtr monitor = MonitorFromPoint(new WinPoint { X = 0, Y = 0 }, MonitorDefaultToPrimary);
            if (monitor != IntPtr.Zero)
            {
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfo(monitor, ref info))
                {
                    int dpi = 0;
                    if (GetDpiForMonitor(monitor, MdtEffectiveDpi, out uint dpiX, out _) == 0 && dpiX >= ModeOsdPlacement.BaseDpi)
                        dpi = (int)dpiX;
                    else
                        dpi = (int)Math.Max((uint)ModeOsdPlacement.BaseDpi, GetDpiForWindow(_hwnd));

                    return new OsdDisplay(
                        info.Work.Left, info.Work.Top,
                        info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top,
                        dpi);
                }
            }

            // Fallback: the WinAppSDK's view of the primary display. Same numbers in the normal
            // case; it is only reached if the monitor query itself failed.
            var work = DisplayArea.Primary.WorkArea;
            return new OsdDisplay(
                work.X, work.Y, work.Width, work.Height,
                (int)Math.Max((uint)ModeOsdPlacement.BaseDpi, GetDpiForWindow(_hwnd)));
        }

        /// <summary>
        /// Re-resolves the display and reports whether the work area / DPI moved since the last
        /// call. Called before every Show, and by the re-anchor path while the card is visible.
        /// A degenerate result (0×0 — the monitor is momentarily gone during a mode set) keeps
        /// the last good geometry instead of placing the card at the origin.
        /// </summary>
        private bool RefreshDisplay()
        {
            OsdDisplay resolved = QueryDisplay();
            if (!resolved.IsUsable)
            {
                if (_displayKnown) return false;
                resolved = new OsdDisplay(0, 0, 1920, 1080, ModeOsdPlacement.BaseDpi);
            }

            OsdDisplay previous = _display;
            bool changed = _displayKnown && resolved != previous;
            _display = resolved;
            _displayKnown = true;

            if (changed)
            {
                Trace($"display changed: {previous.Describe()} -> {resolved.Describe()}");
            }
            return changed;
        }

        private int ScaleDip(double dip) => ModeOsdPlacement.ScaleDip(dip, _display.Dpi);

        /// <summary>Measured width of the mode name, in DIP (0 if the text has never been laid out).</summary>
        private double MeasureTextWidthDip()
        {
            try
            {
                ModeText.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                return ModeText.DesiredSize.Width;
            }
            catch (Exception ex)
            {
                Trace($"text measure failed: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Spec §1 placement from the *live* display: horizontally centred on the primary work
        /// area, card bottom 24 DIP above its bottom edge, clamped so the card never hangs off
        /// the work area (a smaller resolution or a taller taskbar shrinks the margin, then the
        /// card, instead of clipping it). Only computes the rect — the caller decides which Y to
        /// apply, so the window is never briefly shown at the wrong position.
        /// </summary>
        /// <param name="remeasureText">
        /// True on a Show (the title may have changed). False on a re-anchor: the text is the same
        /// element with the same string, and its DIP width does not depend on the display — so the
        /// cached value is reused and no XAML measure runs from inside a display-change callback.
        /// </param>
        private void ApplyLayout(bool remeasureText)
        {
            // _cardWidthDip is the card (padding included); Compute takes the *text* width and
            // applies that padding itself — passing the card width there double-counts it.
            if (remeasureText) _textWidthDip = MeasureTextWidthDip();
            _cardWidthDip = ModeOsdPlacement.CardWidthDip(_textWidthDip);
            OsdPlacement placement = ModeOsdPlacement.Compute(_textWidthDip, _display);
            _winW = placement.Width;
            _winH = placement.Height;
            _winX = placement.X;
            _restY = placement.RestY;

            // The one line that makes a resolution switch auditable: what the display is now,
            // what the card measures in DIP, and the px rect it turns into. DisplayArea.Primary
            // is printed alongside so a stale WinAppSDK view would show up as a mismatch.
            Trace($"geometry: {_display.Describe()} card={_cardWidthDip:0.#}x{ModeOsdPlacement.CardHeightDip:0} DIP " +
                  $"(text {_textWidthDip:0.#}{(remeasureText ? string.Empty : ", cached")}) -> {placement.Describe()} " +
                  $"(bottom margin {ScaleDip(ModeOsdPlacement.BottomMarginDip)} px) [{DescribeDisplayArea()}]");
        }

        /// <summary>The WinAppSDK's own view of the primary display, for the geometry trace only.</summary>
        private static string DescribeDisplayArea()
        {
            try
            {
                var work = DisplayArea.Primary.WorkArea;
                return $"DisplayArea.Primary=({work.X},{work.Y} {work.Width}x{work.Height})";
            }
            catch (Exception ex)
            {
                return $"DisplayArea.Primary=<{ex.GetType().Name}>";
            }
        }

        /// <summary>
        /// The display moved under a card that is already on screen (resolution / refresh-rate /
        /// DPI / monitor change during the 3 s dwell, or during a 250 ms slide). The window rect is
        /// recomputed from the new work area, and a running slide is retargeted — the declared
        /// curves and durations (spec §2 / §4) are untouched, only the coordinates move.
        /// </summary>
        private void ReanchorAfterDisplayChange()
        {
            int previousRest = _restY;
            ApplyLayout(remeasureText: false);
            int delta = _restY - previousRest;

            WindowMotion? motion = _motion;
            if (motion is { MoveMs: > 0 })
            {
                // Entry: both ends belong to the rest position, so shift the whole path and keep
                // the +24 DIP relationship. Exit/restore: only the end belongs to the rest
                // position — the start stays the card's live position, so it does not jump.
                if (motion.RestAnchored) motion.FromY += delta;
                motion.ToY += delta;
            }
            else
            {
                // Not sliding (holding, or a degraded fade): put the card on the new rest rect
                // now, so the next frame is not drawn at the old coordinates.
                ApplyWindowY(_restY);
            }

            Trace($"re-anchored after display change: rest {previousRest} -> {_restY} px (Δ{delta}), " +
                  $"card {_winW}x{_winH} px at x={_winX}" +
                  (motion is { MoveMs: > 0 } ? $", slide retargeted to {motion.FromY} -> {motion.ToY} px" : string.Empty));
        }

        private void ApplyWindowY(int y)
        {
            _winY = y;
            try
            {
                AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(_winX, _winY, _winW, _winH));
            }
            catch (Exception ex) { Trace($"move failed: {ex.Message}"); }
        }

        private void ApplyWindowAlpha(double alpha)
        {
            _winAlpha = Math.Clamp(alpha, 0.0, 1.0);
            try
            {
                SetLayeredWindowAttributes(_hwnd, 0, (byte)Math.Round(_winAlpha * 255), LWA_ALPHA);
            }
            catch (Exception ex) { Trace($"alpha failed: {ex.Message}"); }
        }

        /// <summary>
        /// Shows the host at alpha 0 and forces the first layout pass. WinUI sizes a never-shown
        /// window to its content's desired size during that pass, so it has to happen *before*
        /// the card rect is applied, otherwise it overwrites it (observed: a 160×48 px card at
        /// the cascade position instead of 240×72 px centred above the taskbar).
        /// </summary>
        private void ShowHostInvisible()
        {
            SetupChrome();
            ApplyWindowAlpha(0);
            if (!AppWindow.IsVisible) AppWindow.Show();
            try { OsdRoot.UpdateLayout(); } catch { }
        }

        /// <summary>Topmost without activating: the overlay must never pull focus (spec §1 / §9).</summary>
        private void ShowTopmost()
        {
            SetWindowPos(_hwnd, HwndTopmost, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        /// <summary>
        /// Re-applies the card rect once if the HWND is not where it was asked to be. Checks the
        /// whole rect, not just Y: after a resolution switch the horizontal centre can be wrong
        /// too (the old code only compared the top edge, so an off-centre card was never fixed).
        /// </summary>
        private void EnsurePlacement(int expectedY)
        {
            if (!GetWindowRect(_hwnd, out WinRect rect)) return;
            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width == _winW && height == _winH && rect.Left == _winX && rect.Top == expectedY) return;
            Trace($"placement corrected: wanted {_winW}x{_winH} @ ({_winX},{expectedY}), " +
                  $"got {width}x{height} @ ({rect.Left},{rect.Top})");
            ApplyWindowY(expectedY);
        }

        private void HideHost()
        {
            try { AppWindow.Hide(); }
            catch (Exception ex) { Trace($"hide failed: {ex.Message}"); }
        }

        // ── Show / state machine (spec §3) ───────────────────────────────────────

        /// <summary>
        /// Entry point for every confirmed mode change. Behaviour depends on where the overlay
        /// currently is (spec §3):
        ///   Hidden   → measure/resize/place, play the entry animation;
        ///   Entering → keep the running entry, only swap the text + replay the icon animation;
        ///   Holding  → swap text, replay icon animation, reset the 3000 ms dwell timer;
        ///   Exiting  → abort the exit, restore to rest in 167 ms, then behave like Holding.
        ///
        /// The display is re-resolved first, on every path. The HWND outlives the display mode,
        /// so the work area and DPI captured at the previous show are exactly the values a
        /// resolution / refresh-rate / DPI switch invalidates.
        /// </summary>
        private void Present(byte mode, string title)
        {
            bool full = MotionAllowed();
            ApplyTheme();
            SetContent(mode, title);
            MotionMode motion = MotionFor(mode);

            bool displayChanged = RefreshDisplay();

            if (_phase == Phase.Hidden)
            {
                ApplyLayout(remeasureText: true);
                // Show invisible at alpha 0, force the first layout, then place: the window
                // never appears at a stale position or size for even one frame.
                ShowHostInvisible();
                int startY = _restY + (full ? ScaleDip(EntrySlideDip) : 0);
                ApplyWindowY(startY);
                EnsurePlacement(startY);
                ShowTopmost();
                Trace($"show mode=0x{mode:X2} '{title}' animations={(full ? "full" : "degraded")} " +
                      $"card={_cardWidthDip:0.#}x{ModeOsdPlacement.CardHeightDip:0} DIP at ({_winX},{_restY})");
                BeginEntry(motion, full);
                RaiseAccessibleAnnouncement(title);
                return;
            }

            // Already on screen: a display change since the last show moves the rest position
            // under the running card, so re-anchor before touching text/icon/timers.
            if (displayChanged) ReanchorAfterDisplayChange();

            switch (_phase)
            {
                case Phase.Entering:
                    // Spec §3: no entry replay while the entry is still running.
                    Trace($"switch during entry mode=0x{mode:X2} '{title}' → text/icon only, entry not replayed");
                    PlayIcon(motion, full);
                    break;

                case Phase.Holding:
                    Trace($"switch while holding mode=0x{mode:X2} '{title}' → text/icon only, dwell reset");
                    PlayIcon(motion, full);
                    RestartDwell();
                    break;

                case Phase.Exiting:
                    Trace($"switch during exit mode=0x{mode:X2} '{title}' → exit aborted, restoring from the live value");
                    BeginRestore(motion, full);
                    break;
            }

            RaiseAccessibleAnnouncement(title);
        }

        private void SetContent(byte mode, string title)
        {
            ModeIcon.Glyph = GlyphFor(mode);
            ModeText.Text = title;
            AutomationProperties.SetName(Card, LocalizationService.Format("Switched to {0} mode", title));
        }

        private void RaiseAccessibleAnnouncement(string title)
        {
            try
            {
                // Spec §3 accessibility: announce the mode; the overlay itself never gets focus.
                // RaiseNotificationEvent lives on AutomationPeer, not on the element.
                var peer = FrameworkElementAutomationPeer.FromElement(Card)
                           ?? new FrameworkElementAutomationPeer(Card);
                peer.RaiseNotificationEvent(
                    AutomationNotificationKind.ActionCompleted,
                    AutomationNotificationProcessing.ImportantMostRecent,
                    LocalizationService.Format("Switched to {0} mode", title),
                    "PredexModeOsd");
            }
            catch (Exception ex) { Trace($"RaiseNotificationEvent failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        private void BeginEntry(MotionMode mode, bool full)
        {
            int session = ++_session;
            _phase = Phase.Entering;
            _hovered = false;
            _dwellTimer.Stop();
            _clock.Restart();

            if (full)
            {
                // Window: Y rest+24 DIP → rest in 250 ms cubic-bezier(0,0,0,1);
                //         alpha 0 → 1 in 167 ms with the same curve (spec §2).
                StartMotion(new WindowMotion
                {
                    FromY = _restY + ScaleDip(EntrySlideDip),
                    ToY = _restY,
                    RestAnchored = true,
                    MoveMs = EntrySlideDuration.TotalMilliseconds,
                    MoveX1 = EntryX1, MoveY1 = EntryY1, MoveX2 = EntryX2, MoveY2 = EntryY2,
                    FromAlpha = 0, ToAlpha = 1,
                    AlphaMs = EntryFadeDuration.TotalMilliseconds,
                    AlphaX1 = EntryX1, AlphaY1 = EntryY1, AlphaX2 = EntryX2, AlphaY2 = EntryY2,
                    OnDone = () => OnEntryComplete(session),
                });
                PlayIcon(mode, full: true);
                Trace($"enter: window Y {_restY + ScaleDip(EntrySlideDip)} → {_restY} px (+{EntrySlideDip:0} → 0 DIP) " +
                      $"over {EntrySlideDuration.TotalMilliseconds:0} ms cubic-bezier(0,0,0,1); " +
                      $"alpha 0 → 1 over {EntryFadeDuration.TotalMilliseconds:0} ms; icon delayed " +
                      $"{IconDelay.TotalMilliseconds:0} ms");
            }
            else
            {
                // Spec §7: the window does not move, no icon animation, 83 ms linear fade.
                StartMotion(new WindowMotion
                {
                    FromY = _restY, ToY = _restY, MoveMs = 0,
                    FromAlpha = 0, ToAlpha = 1,
                    AlphaMs = DegradedFadeDuration.TotalMilliseconds,
                    AlphaX1 = LinearX1, AlphaY1 = LinearY1, AlphaX2 = LinearX2, AlphaY2 = LinearY2,
                    OnDone = () => OnEntryComplete(session),
                });
                Trace($"enter (degraded): window fixed at {_restY} px, alpha 0 → 1 over " +
                      $"{DegradedFadeDuration.TotalMilliseconds:0} ms linear, scale fixed 1, icon animation skipped");
            }
        }

        private void OnEntryComplete(int session)
        {
            if (_session != session || _phase != Phase.Entering) return;
            if (ShellLog.Enabled)
                Trace($"enter completed in {_clock.Elapsed.TotalMilliseconds:0.0} ms " +
                      "(declared: slide 250 ms, fade 167 ms, icon delayed 83 ms)");
            _phase = Phase.Holding;
            // Spec §3: the 3000 ms dwell starts when the entry has finished.
            RestartDwell();
        }

        /// <summary>
        /// Spec §3 "退场途中再次切换模式": abort the exit and bring the card back to rest in
        /// 167 ms from wherever it is (the motion starts from the live window Y and alpha), then
        /// behave like the "switch while holding" rule.
        /// </summary>
        private void BeginRestore(MotionMode mode, bool full)
        {
            int session = ++_session;
            _phase = Phase.Holding;

            if (full)
            {
                StartMotion(new WindowMotion
                {
                    FromY = _winY, ToY = _restY,
                    MoveMs = RestoreDuration.TotalMilliseconds,
                    MoveX1 = EntryX1, MoveY1 = EntryY1, MoveX2 = EntryX2, MoveY2 = EntryY2,
                    FromAlpha = _winAlpha, ToAlpha = 1,
                    AlphaMs = RestoreDuration.TotalMilliseconds,
                    AlphaX1 = EntryX1, AlphaY1 = EntryY1, AlphaX2 = EntryX2, AlphaY2 = EntryY2,
                    OnDone = () => OnRestoreComplete(mode, session),
                });
            }
            else
            {
                StartMotion(new WindowMotion
                {
                    FromY = _restY, ToY = _restY, MoveMs = 0,
                    FromAlpha = _winAlpha, ToAlpha = 1,
                    AlphaMs = DegradedFadeDuration.TotalMilliseconds,
                    AlphaX1 = LinearX1, AlphaY1 = LinearY1, AlphaX2 = LinearX2, AlphaY2 = LinearY2,
                    OnDone = () => OnRestoreComplete(mode, session),
                });
            }

            Trace($"restore: live value ({_winY} px, alpha {_winAlpha:0.00}) → rest over " +
                  $"{(full ? RestoreDuration : DegradedFadeDuration).TotalMilliseconds:0} ms");
        }

        private void OnRestoreComplete(MotionMode mode, int session)
        {
            if (_session != session || _phase != Phase.Holding) return;
            // Spec §3: after the restore, "然后按上一条处理" → new icon motion + fresh timer.
            PlayIcon(mode, MotionAllowed());
            RestartDwell();
        }

        private void RestartDwell()
        {
            _dwellTimer.Stop();
            if (_hovered) return;                     // spec §3: hover pauses the timer
            _clock.Restart();                         // measured dwell for the --trace evidence
            _dwellTimer.Interval = DwellDuration;
            _dwellTimer.Start();
        }

        private void DwellTick(DispatcherQueueTimer timer, object args)
        {
            timer.Stop();
            if (_phase != Phase.Holding) return;
            Trace($"dwell {_clock.Elapsed.TotalMilliseconds:0} ms elapsed (declared {DwellDuration.TotalMilliseconds:0} ms) → exit");
            BeginExit();
        }

        private void BeginExit()
        {
            int session = ++_session;
            _phase = Phase.Exiting;
            _clock.Restart();

            bool full = MotionAllowed();

            if (full)
            {
                // Spec §4: Y 0 → +16 DIP and alpha 1 → 0, both 167 ms, cubic-bezier(0.7,0,1,0.5).
                // Started from the live position/alpha so an interrupting switch never jumps.
                StartMotion(new WindowMotion
                {
                    FromY = _winY, ToY = _restY + ScaleDip(ExitSlideDip),
                    MoveMs = ExitDuration.TotalMilliseconds,
                    MoveX1 = ExitX1, MoveY1 = ExitY1, MoveX2 = ExitX2, MoveY2 = ExitY2,
                    FromAlpha = _winAlpha, ToAlpha = 0,
                    AlphaMs = ExitDuration.TotalMilliseconds,
                    AlphaX1 = ExitX1, AlphaY1 = ExitY1, AlphaX2 = ExitX2, AlphaY2 = ExitY2,
                    OnDone = () => CompleteExit(session),
                });
                Trace($"exit: window Y {_winY} → {_restY + ScaleDip(ExitSlideDip)} px (0 → +{ExitSlideDip:0} DIP) " +
                      $"over {ExitDuration.TotalMilliseconds:0} ms cubic-bezier(0.7,0,1,0.5); alpha 1 → 0");
            }
            else
            {
                StartMotion(new WindowMotion
                {
                    FromY = _restY, ToY = _restY, MoveMs = 0,
                    FromAlpha = _winAlpha, ToAlpha = 0,
                    AlphaMs = DegradedFadeDuration.TotalMilliseconds,
                    AlphaX1 = LinearX1, AlphaY1 = LinearY1, AlphaX2 = LinearX2, AlphaY2 = LinearY2,
                    OnDone = () => CompleteExit(session),
                });
                Trace($"exit (degraded): alpha 1 → 0 over {DegradedFadeDuration.TotalMilliseconds:0} ms linear, window fixed");
            }
        }

        private void CompleteExit(int session)
        {
            if (_session != session) return;
            _phase = Phase.Hidden;
            HideHost();
            ResetVisuals();
            Trace($"exit completed in {_clock.Elapsed.TotalMilliseconds:0.0} ms (declared 167 ms) → host hidden");
        }

        /// <summary>Stops every animation and puts the visuals back on their rest values.</summary>
        private void ResetVisuals()
        {
            _card.Opacity = 1f;
            _icon.StopAnimation("Translation");
            _icon.StopAnimation("Scale");
            _icon.StopAnimation("Opacity");
            _icon.Scale = Vector3.One;
            _icon.Opacity = 1f;
            _icon.Properties.InsertVector3("Translation", Vector3.Zero);
        }

        // ── Window motion driver (slide + fade) ──────────────────────────────────

        /// <summary>
        /// Runs a window slide/fade. Progress comes from a Stopwatch rather than from the frame
        /// count, so a 250 ms slide takes 250 ms even if the dispatcher delivers late frames
        /// (acceptance: duration error ≤ 1 frame). Ticks come from
        /// <see cref="CompositionTarget.Rendering"/> (vsync) with a 16 ms timer as a fallback in
        /// case the compositor does not raise Rendering for this tiny window.
        /// </summary>
        private void StartMotion(WindowMotion motion)
        {
            StopMotion();

            _motion = motion;
            if (motion.MoveMs > 0) ApplyWindowY(motion.FromY);
            if (motion.AlphaMs > 0) ApplyWindowAlpha(motion.FromAlpha);

            _motionClock.Restart();
            if (!_renderingHooked)
            {
                CompositionTarget.Rendering += OnRendering;
                _renderingHooked = true;
            }
            _motionTimer.Start();
        }

        private void StopMotion()
        {
            _motionTimer.Stop();
            if (_renderingHooked)
            {
                CompositionTarget.Rendering -= OnRendering;
                _renderingHooked = false;
            }
            _motion = null;
        }

        private void OnRendering(object? sender, object args) => AdvanceMotion();

        private void MotionTick(DispatcherQueueTimer timer, object args) => AdvanceMotion();

        private void AdvanceMotion()
        {
            WindowMotion? motion = _motion;
            if (motion is null) return;

            double elapsed = _motionClock.Elapsed.TotalMilliseconds;

            if (motion.MoveMs > 0)
            {
                double progress = Math.Clamp(elapsed / motion.MoveMs, 0.0, 1.0);
                double eased = CubicBezier(motion.MoveX1, motion.MoveY1, motion.MoveX2, motion.MoveY2, progress);
                int y = (int)Math.Round(motion.FromY + ((motion.ToY - motion.FromY) * eased));
                if (y != _winY) ApplyWindowY(y);
            }

            if (motion.AlphaMs > 0)
            {
                double progress = Math.Clamp(elapsed / motion.AlphaMs, 0.0, 1.0);
                double eased = CubicBezier(motion.AlphaX1, motion.AlphaY1, motion.AlphaX2, motion.AlphaY2, progress);
                ApplyWindowAlpha(motion.FromAlpha + ((motion.ToAlpha - motion.FromAlpha) * eased));
            }

            if (elapsed < Math.Max(motion.MoveMs, motion.AlphaMs)) return;

            // Land exactly on the declared end values, then report completion.
            if (motion.MoveMs > 0) ApplyWindowY(motion.ToY);
            if (motion.AlphaMs > 0) ApplyWindowAlpha(motion.ToAlpha);
            Action? done = motion.OnDone;
            StopMotion();
            done?.Invoke();
        }

        /// <summary>Cubic-bezier timing function: y for a given x progress (x1,y1)-(x2,y2).</summary>
        private static double CubicBezier(double x1, double y1, double x2, double y2, double progress)
        {
            if (progress <= 0) return 0;
            if (progress >= 1) return 1;

            double low = 0, high = 1, t = progress;
            for (int i = 0; i < 24; i++)
            {
                t = (low + high) / 2;
                if (BezierAxis(x1, x2, t) < progress) low = t;
                else high = t;
            }
            return BezierAxis(y1, y2, t);
        }

        private static double BezierAxis(double first, double second, double t)
        {
            double inverse = 1 - t;
            return (3 * inverse * inverse * t * first) + (3 * inverse * t * t * second) + (t * t * t);
        }

        // ── Icon motion (docs\ICON-MICRO-ANIMATIONS.md) ──────────────────────────

        /// <summary>Easing table from ICON-MICRO-ANIMATIONS §2.4 (all y control points in [0,1]).</summary>
        private enum IconEase { Decel, Standard, Sine, Snap }

        private readonly record struct ScalarKey(float Progress, float Value, IconEase Ease);
        private readonly record struct VectorKey(float Progress, Vector3 Value, IconEase Ease);
        private readonly record struct SpringSpec(Vector3 Initial, float DampingRatio, double PeriodMs);
        private readonly record struct IconMotionSpec(
            float PivotX, float PivotY, double DurationMs,
            ScalarKey[] Opacity, VectorKey[] Scale, VectorKey[] Translation, ScalarKey[] Rotation,
            SpringSpec? TranslationSpring);

        // Pivot points are normalized glyph coordinates (§1): CenterPoint = ActualSize * pivot.
        private static readonly IconMotionSpec EcoIconSpec = new(
            0.49f, 0.70f, 560,
            new ScalarKey[] { new(0f, 0f, IconEase.Decel), new(0.30f, 1f, IconEase.Decel) },
            new VectorKey[] { new(0f, new Vector3(0.94f, 0.94f, 1f), IconEase.Decel), new(0.40f, Vector3.One, IconEase.Decel) },
            new VectorKey[] { new(0f, new Vector3(0f, -1.0f, 0f), IconEase.Decel), new(0.60f, Vector3.Zero, IconEase.Decel) },
            new ScalarKey[]
            {
                new(0f, -6f, IconEase.Sine), new(0.35f, 3f, IconEase.Sine),
                new(0.65f, -1.2f, IconEase.Sine), new(1f, 0f, IconEase.Sine),
            },
            null);

        private static readonly IconMotionSpec QuietIconSpec = new(
            0.50f, 0.50f, 600,
            new ScalarKey[] { new(0f, 0f, IconEase.Decel), new(0.45f, 1f, IconEase.Decel) },
            new VectorKey[]
            {
                new(0f, new Vector3(0.96f, 0.96f, 1f), IconEase.Sine),
                new(0.45f, new Vector3(1.02f, 1.02f, 1f), IconEase.Sine),
                new(1f, Vector3.One, IconEase.Sine),
            },
            new VectorKey[]
            {
                new(0f, new Vector3(0f, -1.5f, 0f), IconEase.Decel),
                new(0.60f, new Vector3(0f, 0.3f, 0f), IconEase.Decel),
                new(1f, Vector3.Zero, IconEase.Sine),
            },
            new ScalarKey[] { new(0f, -3f, IconEase.Decel), new(0.70f, 0f, IconEase.Decel) },
            null);

        private static readonly IconMotionSpec BalancedIconSpec = new(
            0.50f, 0.50f, 440,
            new ScalarKey[] { new(0f, 0f, IconEase.Decel), new(0.30f, 1f, IconEase.Decel) },
            new VectorKey[]
            {
                new(0f, new Vector3(0.96f, 1f, 1f), IconEase.Decel),
                new(0.50f, Vector3.One, IconEase.Decel),
            },
            new VectorKey[]
            {
                new(0f, new Vector3(-1.5f, 0f, 0f), IconEase.Snap),
                new(0.50f, new Vector3(0.3f, 0f, 0f), IconEase.Snap),
                new(0.70f, new Vector3(-0.1f, 0f, 0f), IconEase.Snap),
                new(1f, Vector3.Zero, IconEase.Snap),
            },
            Array.Empty<ScalarKey>(),        // §3.3: the slider stays level — no rotation
            null);

        private static readonly IconMotionSpec PerformanceIconSpec = new(
            0.47f, 0.58f, 520,
            new ScalarKey[] { new(0f, 0f, IconEase.Decel), new(0.30f, 1f, IconEase.Decel) },
            new VectorKey[] { new(0f, new Vector3(0.94f, 0.94f, 1f), IconEase.Decel), new(0.50f, Vector3.One, IconEase.Decel) },
            Array.Empty<VectorKey>(),        // §3.4: no translation, the needle-root pivot does the work
            new ScalarKey[]
            {
                new(0f, -12f, IconEase.Snap), new(0.60f, 2f, IconEase.Snap),
                new(0.80f, -0.5f, IconEase.Sine), new(1f, 0f, IconEase.Sine),
            },
            null);

        private static readonly IconMotionSpec BeastIconSpec = new(
            0.49f, 0.47f, 340,
            new ScalarKey[] { new(0f, 0f, IconEase.Decel), new(0.12f, 1f, IconEase.Decel) },
            new VectorKey[]
            {
                new(0f, new Vector3(0.90f, 0.90f, 1f), IconEase.Snap),
                new(0.28f, new Vector3(1.08f, 1.08f, 1f), IconEase.Snap),
                new(0.55f, new Vector3(0.98f, 0.98f, 1f), IconEase.Standard),
                new(1f, Vector3.One, IconEase.Sine),
            },
            Array.Empty<VectorKey>(),
            new ScalarKey[]
            {
                new(0f, -4f, IconEase.Snap), new(0.28f, 2f, IconEase.Snap), new(1f, 0f, IconEase.Sine),
            },
            // §3.5: spring along the bolt direction,|initial| = √(0.8²+1.2²) ≈ 1.44 DIP ≤ 1.5.
            new SpringSpec(new Vector3(0.8f, -1.2f, 0f), 0.5f, 90));

        private static IconMotionSpec IconSpecFor(MotionMode mode) => mode switch
        {
            MotionMode.Eco => EcoIconSpec,
            MotionMode.Silent => QuietIconSpec,
            MotionMode.Balanced => BalancedIconSpec,
            MotionMode.Performance => PerformanceIconSpec,
            MotionMode.Beast => BeastIconSpec,
            _ => BalancedIconSpec,
        };

        private CompositionEasingFunction EaseFor(IconEase ease) => ease switch
        {
            IconEase.Standard => _easeStandard,
            IconEase.Sine => _easeSine,
            IconEase.Snap => _easeSnap,
            _ => _easeDecel,
        };

        /// <summary>
        /// Plays the per-mode entrance motion from docs\ICON-MICRO-ANIMATIONS.md on the icon
        /// visual only (never on the card/button): pivot from §1, keyframes from §3, easings
        /// from §2.4, the global caps from §2.3. The toast spec's 83 ms icon delay (§2) is kept
        /// on top of the per-icon durations. Identity values are written back on batch
        /// completion so the glyph re-rasterizes on whole pixels (§2.5).
        ///
        /// Unlike a page tile (§2.1 "once per page instance") this overlay plays the motion on
        /// every confirmed mode switch — that is its whole job — but a re-entrant switch always
        /// restarts from the reset state, so nothing accumulate.
        /// </summary>
        private void PlayIcon(MotionMode mode, bool full)
        {
            if (!full) return;

            int token = ++_iconToken;

            // Reset first so a previous run never leaves scale/rotation/opacity behind.
            _icon.StopAnimation("Translation");
            _icon.StopAnimation("Scale");
            _icon.StopAnimation("RotationAngleInDegrees");
            _icon.StopAnimation("Opacity");
            _icon.Scale = Vector3.One;
            _icon.Opacity = 1f;
            _icon.RotationAngleInDegrees = 0f;
            _icon.Properties.InsertVector3("Translation", Vector3.Zero);

            IconMotionSpec spec = IconSpecFor(mode);

            // §1: normalized pivot × ActualSize. The FontIcon is 20×20 DIP in XAML; ActualSize is
            // only 0 before the first layout pass, which cannot happen on a visible overlay.
            double width = ModeIcon.ActualSize.X > 0 ? ModeIcon.ActualSize.X : 20.0;
            double height = ModeIcon.ActualSize.Y > 0 ? ModeIcon.ActualSize.Y : 20.0;
            _icon.CenterPoint = new Vector3((float)(spec.PivotX * width), (float)(spec.PivotY * height), 0f);

            // §2.4/§5.4: hold opacity 0 before the animation starts, otherwise the first frame
            // paints the identity glyph and then jumps to transparent.
            _icon.Opacity = 0f;

            var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            StartScalarAnimation("Opacity", spec.Opacity, spec.DurationMs);
            StartVector3Animation("Scale", spec.Scale, spec.DurationMs);
            StartScalarAnimation("RotationAngleInDegrees", spec.Rotation, spec.DurationMs);

            if (spec.TranslationSpring is { } spring)
            {
                var animation = _compositor.CreateSpringVector3Animation();
                animation.InitialValue = spring.Initial;
                animation.FinalValue = Vector3.Zero;
                animation.DampingRatio = spring.DampingRatio;
                animation.Period = TimeSpan.FromMilliseconds(spring.PeriodMs);
                animation.InitialVelocity = Vector3.Zero;
                animation.DelayTime = IconDelay;
                animation.StopBehavior = AnimationStopBehavior.SetToFinalValue;
                _icon.StartAnimation("Translation", animation);
            }
            else
            {
                StartVector3Animation("Translation", spec.Translation, spec.DurationMs);
            }

            batch.End();
            batch.Completed += (_, _) =>
            {
                if (_iconToken != token) return;
                // §2.5: land exactly on the identity transform.
                _icon.Opacity = 1f;
                _icon.Scale = Vector3.One;
                _icon.RotationAngleInDegrees = 0f;
                _icon.Properties.InsertVector3("Translation", Vector3.Zero);
                Trace($"icon {mode}: completed after {spec.DurationMs:0} ms, identity restored");
            };

            Trace($"icon {mode}: pivot=({spec.PivotX:0.00},{spec.PivotY:0.00}) duration={spec.DurationMs:0} ms " +
                  $"delay={IconDelay.TotalMilliseconds:0} ms " +
                  $"(opacity {spec.Opacity.Length}kf, scale {spec.Scale.Length}kf, " +
                  $"translation {(spec.TranslationSpring is null ? $"{spec.Translation.Length}kf" : "spring ζ0.5 T90ms")}, " +
                  $"rotation {spec.Rotation.Length}kf)");
        }

        private void StartScalarAnimation(string property, ScalarKey[] keyframes, double durationMs)
        {
            if (keyframes.Length == 0) return;
            var animation = _compositor.CreateScalarKeyFrameAnimation();
            animation.Duration = TimeSpan.FromMilliseconds(durationMs);
            animation.DelayTime = IconDelay;
            animation.StopBehavior = AnimationStopBehavior.SetToFinalValue;
            foreach (var key in keyframes)
                animation.InsertKeyFrame(key.Progress, key.Value, EaseFor(key.Ease));
            _icon.StartAnimation(property, animation);
        }

        private void StartVector3Animation(string property, VectorKey[] keyframes, double durationMs)
        {
            if (keyframes.Length == 0) return;
            var animation = _compositor.CreateVector3KeyFrameAnimation();
            animation.Duration = TimeSpan.FromMilliseconds(durationMs);
            animation.DelayTime = IconDelay;
            animation.StopBehavior = AnimationStopBehavior.SetToFinalValue;
            foreach (var key in keyframes)
                animation.InsertKeyFrame(key.Progress, key.Value, EaseFor(key.Ease));
            _icon.StartAnimation(property, animation);
        }

        // ── Hover (spec §3) ──────────────────────────────────────────────────────

        private void Card_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (_phase != Phase.Holding) return;
            _hovered = true;
            _dwellTimer.Stop();
            Trace("dwell paused (pointer over card)");
        }

        private void Card_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (!_hovered) return;
            _hovered = false;
            if (_phase != Phase.Holding) return;
            RestartDwell();
            Trace("dwell reset to 3000 ms (pointer left)");
        }

        // ── Mode → glyph / motion ────────────────────────────────────────────────

        private static MotionMode MotionFor(byte mode) => mode switch
        {
            0x06 => MotionMode.Eco,          // 节能
            0x00 => MotionMode.Silent,       // 静音
            0x01 => MotionMode.Balanced,     // 均衡
            0x04 => MotionMode.Performance,  // 性能
            0x05 => MotionMode.Beast,        // 狂暴 (ViewModel alias: "Turbo")
            _ => MotionMode.Balanced,
        };

        /// <summary>
        /// The five mode glyphs the Home tiles already draw (Pages\HomePage.xaml) — one icon per
        /// mode, 狂暴 = the lightning ⚡ the spec §1 names.
        /// </summary>
        private static string GlyphFor(byte mode) => mode switch
        {
            0x06 => Glyphs.Eco,
            0x00 => Glyphs.Quiet,
            0x01 => Glyphs.Balanced,
            0x04 => Glyphs.PerformanceTile,
            0x05 => Glyphs.Beast,
            _ => Glyphs.PerformanceMode,
        };

        private void ApplyTheme()
        {
            try
            {
                OsdRoot.RequestedTheme = ThemeManager.IsLightTheme ? ElementTheme.Light : ElementTheme.Dark;
            }
            catch { }
        }

        /// <summary>
        /// Spec §0: read <see cref="UISettings.AnimationsEnabled"/> once per Show.
        /// <c>--no-anim</c> short-circuits to the degraded path for verification (spec §7).
        /// </summary>
        private static bool MotionAllowed()
        {
            if (ForceNoMotion) return false;
            try { return UiSettings.AnimationsEnabled; }
            catch { return true; }
        }

        // ── Trace (--trace evidence) ─────────────────────────────────────────────

        /// <summary>
        /// Timing evidence for acceptance "进场位移与时长": every phase transition logs the
        /// measured wall-clock duration next to the number the spec declares, and the window
        /// rect the slide works on. docs\osd-verify.ps1 additionally measures the window Y from
        /// real frames, which is the direct displacement proof.
        /// </summary>
        private static void Trace(string message) => ShellLog.Write("osd", message);
    }
}
