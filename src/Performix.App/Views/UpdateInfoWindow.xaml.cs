// UpdateInfoWindow — bottom-right update status window (see the XAML header for the "why a
// window" argument and the "not topmost" rule).
//
// Lifecycle: one HWND per process, created on the first status that needs showing and then
// reused forever (the shell destroys its page tree when it hides to the tray; a status window
// owned by a page would die with it). A repeated Present only swaps the text, so switching
// between 检查中 → 下载中 → 校验中 does not re-run the entry animation or resize anything: that
// is the "多个状态切换不要闪" requirement, and it is why the size is fixed.
//
// Interaction: the window never activates (WS_EX_NOACTIVATE), but a click is still delivered.
// Clicking opens the Update page — that is "点一下可以看详情" without building a second detail UI.
//
// Dismissal: a busy state (checking / downloading / verifying) stays until it is replaced. A
// terminal state (up to date / failed / ready to install) fades out after
// <see cref="TerminalDwell"/>; the failure text is also written to shell.log and update.log by
// the caller, so nothing is lost when the window goes away.

using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Predex.WinUI.Services;
using PreySense.Frontend.Services;
using System;
using System.Runtime.InteropServices;
using WinRT.Interop;

namespace Predex.WinUI.Views
{
    /// <summary>One rendered state of the status window.</summary>
    internal sealed record UpdateInfoContent(
        string Title,
        string? Detail,
        bool Busy,
        bool ProgressVisible,
        bool ProgressIndeterminate,
        double ProgressPercent,
        string? ProgressText,
        bool IsFailure,
        bool AutoDismiss);

    /// <summary>Bottom-right, non-topmost status card for the automatic update.</summary>
    public sealed partial class UpdateInfoWindow : Window
    {
        // ── Geometry (DIP) ───────────────────────────────────────────────────────
        /// <summary>Fixed card size for every state: no resize means no flicker.</summary>
        private const float CardWidthDip = 360f;
        private const float CardHeightDip = 104f;
        /// <summary>Distance from the work-area right/bottom edges. The taskbar (and with it the
        /// notification area) is outside the work area, so the card never covers tray icons.</summary>
        private const float EdgeMarginDip = 24f;

        // ── Timing ───────────────────────────────────────────────────────────────
        private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(167);
        /// <summary>How long a terminal state stays before fading out ("过一会儿自动淡出").</summary>
        private static readonly TimeSpan TerminalDwell = TimeSpan.FromSeconds(8);
        private const int TickMs = 16;

        private enum Phase { Hidden, Showing, Visible, Hiding }

        // ── Process-wide instance ────────────────────────────────────────────────
        private static UpdateInfoWindow? _instance;
        private static DispatcherQueue? _queue;
        private static Action? _openDetails;

        private readonly IntPtr _hwnd;
        private readonly DispatcherQueueTimer _tick;
        private readonly System.Diagnostics.Stopwatch _clock = new();
        private Phase _phase = Phase.Hidden;
        private double _alpha;
        private double _alphaFrom, _alphaTo, _alphaMs;
        private int _session;
        private bool _dismissPending;
        private TimeSpan _dwellElapsed;
        private int _winX, _winY, _winW, _winH;

        // ── Win32 ────────────────────────────────────────────────────────────────
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out WinRect rect);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(WinPoint point, uint flags);
        [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
        [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint colorKey, byte alpha, uint flags);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hWnd, uint attr, ref int attrValue, int size);

        [StructLayout(LayoutKind.Sequential)]
        private struct WinRect { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinPoint { public int X, Y; }

        private const uint MonitorDefaultToNearest = 0x00000002;
        private const int MdtEffectiveDpi = 0;
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
        /// <summary>HWND_NOTOPMOST: explicitly *below* the topmost band (the brief: not always-on-top).</summary>
        private static readonly IntPtr HwndNotTopmost = new(-2);

        // ── Public surface ───────────────────────────────────────────────────────

        /// <summary>Wires the dispatcher and the "open details" action (the Update page).</summary>
        internal static void Initialize(DispatcherQueue queue, Action? openDetails)
        {
            _queue = queue;
            _openDetails = openDetails;
        }

        /// <summary>
        /// Shows (or updates) the status card. Safe from any thread; a failure to create the
        /// window is logged and swallowed — a status window must never break an update.
        /// </summary>
        internal static void Present(UpdateInfoContent content)
        {
            DispatcherQueue? queue = _queue;
            try { queue ??= App.CurrentShell?.DispatcherQueue; } catch { queue = null; }
            if (queue is null) return;

            if (!queue.HasThreadAccess)
            {
                queue.TryEnqueue(() => Present(content));
                return;
            }

            try
            {
                _instance ??= new UpdateInfoWindow(queue);
                _instance.Apply(content);
            }
            catch (Exception ex)
            {
                Trace($"present failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Fades the card out now (used when the feature is switched off mid-flight).</summary>
        internal static void Dismiss()
        {
            UpdateInfoWindow? instance = _instance;
            if (instance is null) return;
            DispatcherQueue? queue = _queue;
            if (queue is null) return;
            if (!queue.HasThreadAccess) { queue.TryEnqueue(Dismiss); return; }
            try { instance.BeginHide(); } catch { }
        }

        /// <summary>Closes the window (application quit path). Safe to call repeatedly.</summary>
        internal static void Shutdown()
        {
            UpdateInfoWindow? instance = _instance;
            _instance = null;
            if (instance is null) return;
            try { instance._tick.Stop(); } catch { }
            try { instance.Close(); } catch { }
        }

        // ── Construction ─────────────────────────────────────────────────────────
        private UpdateInfoWindow(DispatcherQueue queue)
        {
            InitializeComponent();

            _queue = queue;
            _hwnd = WindowNative.GetWindowHandle(this);

            _tick = _queue.CreateTimer();
            _tick.IsRepeating = true;
            _tick.Interval = TimeSpan.FromMilliseconds(TickMs);
            _tick.Tick += Tick;

            Closed += (_, _) => { if (ReferenceEquals(_instance, this)) _instance = null; };

            Trace($"window created (hwnd 0x{_hwnd.ToInt64():X})");
        }

        // ── Chrome / placement ───────────────────────────────────────────────────

        /// <summary>
        /// Borderless, non-activating tool window that is deliberately NOT topmost. The DWM
        /// frame is removed so the only visual edge is the card's own 1 px stroke.
        /// </summary>
        private void SetupChrome()
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                // The brief: the status card must not float above everything.
                presenter.IsAlwaysOnTop = false;
            }

            int exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
            SetWindowLong(_hwnd, GWL_EXSTYLE,
                exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_LAYERED);

            ApplyAlpha(0);

            int none = DwmwaColorNone;
            DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref none, sizeof(int));
            DwmSetWindowAttribute(_hwnd, DWMWA_CAPTION_COLOR, ref none, sizeof(int));
            int round = DWMWCP_ROUND;
            DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

            int style = GetWindowLong(_hwnd, GWL_STYLE);
            SetWindowLong(_hwnd, GWL_STYLE, (style & ~(WS_CAPTION | WS_THICKFRAME)) | WS_POPUP);
            SetWindowPos(_hwnd, HwndNotTopmost, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);

            Trace($"chrome: not topmost, NOACTIVATE|TOOLWINDOW|LAYERED, WS_POPUP, DWM round corners");
        }

        private int Dpi
        {
            get
            {
                try
                {
                    var work = DisplayArea.Primary.WorkArea;
                    var centre = new WinPoint { X = work.X + (work.Width / 2), Y = work.Y + (work.Height / 2) };
                    IntPtr monitor = MonitorFromPoint(centre, MonitorDefaultToNearest);
                    if (monitor != IntPtr.Zero &&
                        GetDpiForMonitor(monitor, MdtEffectiveDpi, out uint dpiX, out _) == 0 && dpiX >= 96)
                        return (int)dpiX;
                }
                catch { /* fall through to the window DPI */ }
                return (int)Math.Max(96u, GetDpiForWindow(_hwnd));
            }
        }

        private int ScaleDip(double dip) => (int)Math.Round(dip * Dpi / 96.0);

        /// <summary>
        /// Bottom-right of the primary work area. The work area excludes the taskbar, so the
        /// card sits above the notification area rather than on top of it.
        /// </summary>
        private void ComputePlacement()
        {
            _winW = ScaleDip(CardWidthDip);
            _winH = ScaleDip(CardHeightDip);

            var work = DisplayArea.Primary.WorkArea;
            int margin = ScaleDip(EdgeMarginDip);
            _winX = work.X + work.Width - _winW - margin;
            _winY = work.Y + work.Height - _winH - margin;
        }

        private void ApplyRect()
        {
            try
            {
                AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(_winX, _winY, _winW, _winH));
            }
            catch (Exception ex) { Trace($"move failed: {ex.Message}"); }
        }

        private void ApplyAlpha(double alpha)
        {
            _alpha = Math.Clamp(alpha, 0.0, 1.0);
            try { SetLayeredWindowAttributes(_hwnd, 0, (byte)Math.Round(_alpha * 255), LWA_ALPHA); }
            catch (Exception ex) { Trace($"alpha failed: {ex.Message}"); }
        }

        private void ApplyTheme()
        {
            try { InfoRoot.RequestedTheme = ThemeManager.IsLightTheme ? ElementTheme.Light : ElementTheme.Dark; }
            catch { }
        }

        // ── State → content ──────────────────────────────────────────────────────
        private void Apply(UpdateInfoContent content)
        {
            ApplyTheme();

            StatusText.Text = content.Title;
            DetailText.Text = content.Detail ?? string.Empty;
            DetailText.Visibility = string.IsNullOrWhiteSpace(content.Detail) ? Visibility.Collapsed : Visibility.Visible;
            DetailText.Foreground = content.IsFailure
                ? ResolveBrush("DangerTextBrush")
                : ResolveBrush("TextFillColorSecondaryBrush");

            BusyRing.IsActive = content.Busy;
            BusyRing.Visibility = content.Busy ? Visibility.Visible : Visibility.Collapsed;

            // Collapsed, but the row itself is a fixed 14 DIP tall in XAML, so hiding the bar
            // does not re-flow the card: the geometry is identical in every state.
            Progress.Visibility = content.ProgressVisible ? Visibility.Visible : Visibility.Collapsed;
            Progress.IsIndeterminate = content.ProgressIndeterminate;
            Progress.Value = content.ProgressIndeterminate ? 0 : Math.Clamp(content.ProgressPercent, 0, 100);
            ProgressText.Text = content.ProgressText ?? string.Empty;

            // A terminal state gets a fresh dwell; a busy state cancels any pending fade.
            _dismissPending = content.AutoDismiss;
            _dwellElapsed = TimeSpan.Zero;

            if (_phase is Phase.Hidden or Phase.Hiding)
                BeginShow();
            else if (!content.AutoDismiss)
                _dismissPending = false;

            Trace($"state '{content.Title}' busy={content.Busy} progress={content.ProgressVisible} " +
                  $"dismiss={_dismissPending} phase={_phase}");
        }

        private void BeginShow()
        {
            int session = ++_session;
            _phase = Phase.Showing;

            if (_winW <= 0) ComputePlacement();
            SetupChrome();
            ApplyRect();

            // Size to the fixed card and force the first layout BEFORE showing, so the window is
            // never visible at a stale size for one frame.
            try { InfoRoot.UpdateLayout(); } catch { }
            ApplyRect();

            if (!AppWindow.IsVisible) AppWindow.Show();
            // Show() can activate; the style above keeps it out of the taskbar, and the explicit
            // NOTOPMOST call keeps it out of the topmost band.
            SetWindowPos(_hwnd, HwndNotTopmost, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);

            StartFade(from: 0, to: 1, session, onDone: () =>
            {
                _phase = Phase.Visible;
                _clock.Restart();
            });
        }

        private void BeginHide()
        {
            if (_phase is Phase.Hidden or Phase.Hiding) return;
            int session = ++_session;
            _phase = Phase.Hiding;
            StartFade(from: _alpha, to: 0, session, onDone: () =>
            {
                _phase = Phase.Hidden;
                try { AppWindow.Hide(); } catch (Exception ex) { Trace($"hide failed: {ex.Message}"); }
            });
        }

        private void StartFade(double from, double to, int session, Action onDone)
        {
            _alphaFrom = from;
            _alphaTo = to;
            _alphaMs = FadeDuration.TotalMilliseconds;
            _clock.Restart();
            ApplyAlpha(from);
            _tick.Start();
            _fadeDone = onDone;
            _fadeSession = session;
        }

        private Action? _fadeDone;
        private int _fadeSession;

        private void Tick(DispatcherQueueTimer timer, object args)
        {
            double elapsed = _clock.Elapsed.TotalMilliseconds;

            if (_fadeDone is { } done)
            {
                double progress = Math.Clamp(elapsed / Math.Max(1, _alphaMs), 0.0, 1.0);
                ApplyAlpha(_alphaFrom + ((_alphaTo - _alphaFrom) * progress));
                if (progress < 1.0) return;

                _fadeDone = null;
                ApplyAlpha(_alphaTo);
                done();
                if (_phase == Phase.Visible) _clock.Restart();
                return;
            }

            if (_phase != Phase.Visible) return;
            if (!_dismissPending) return;

            _dwellElapsed = _clock.Elapsed;
            if (_dwellElapsed < TerminalDwell) return;

            Trace($"dwell {_dwellElapsed.TotalMilliseconds:0} ms elapsed → fade out");
            BeginHide();
        }

        // ── Interaction ──────────────────────────────────────────────────────────
        private void Card_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            Trace("clicked → open the Update page");
            try { _openDetails?.Invoke(); } catch (Exception ex) { Trace($"open details failed: {ex.Message}"); }
            BeginHide();
        }

        private static Brush ResolveBrush(string key)
            => Application.Current?.Resources.TryGetValue(key, out object? value) == true && value is Brush brush
                ? brush
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        private static void Trace(string message) => ShellLog.Write("update", "info-window: " + message);
    }
}
