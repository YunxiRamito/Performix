using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;

namespace PreySense.Frontend.Controls
{
    /// <summary>
    /// Standard caption bar for the app's borderless windows: brand + title on the left,
    /// app actions plus minimize / maximize-restore / close on the right. Drag, double-click
    /// maximize and the right-click system menu work here; the window keeps
    /// <c>WindowStyle="None"</c> and gets real resize borders from <c>WindowChrome</c>.
    /// </summary>
    public partial class WindowTitleBar : UserControl
    {
        public const string Brand = "Performix";

        private Window? _window;

        public WindowTitleBar()
        {
            InitializeComponent();
            UpdateCaption();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register(
                nameof(Title),
                typeof(string),
                typeof(WindowTitleBar),
                new PropertyMetadata(string.Empty, OnCaptionChanged));

        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        public static readonly DependencyProperty SubtitleProperty =
            DependencyProperty.Register(
                nameof(Subtitle),
                typeof(string),
                typeof(WindowTitleBar),
                new PropertyMetadata(string.Empty, OnCaptionChanged));

        public string Subtitle
        {
            get => (string)GetValue(SubtitleProperty);
            set => SetValue(SubtitleProperty, value);
        }

        public static readonly DependencyProperty ActionsProperty =
            DependencyProperty.Register(
                nameof(Actions),
                typeof(object),
                typeof(WindowTitleBar),
                new PropertyMetadata(null));

        public object? Actions
        {
            get => GetValue(ActionsProperty);
            set => SetValue(ActionsProperty, value);
        }

        public static readonly DependencyProperty ShowMinimizeButtonProperty =
            DependencyProperty.Register(
                nameof(ShowMinimizeButton),
                typeof(bool),
                typeof(WindowTitleBar),
                new PropertyMetadata(true));

        /// <summary>Hide for dialogs that must not be minimized.</summary>
        public bool ShowMinimizeButton
        {
            get => (bool)GetValue(ShowMinimizeButtonProperty);
            set => SetValue(ShowMinimizeButtonProperty, value);
        }

        public static readonly DependencyProperty ShowMaximizeButtonProperty =
            DependencyProperty.Register(
                nameof(ShowMaximizeButton),
                typeof(bool),
                typeof(WindowTitleBar),
                new PropertyMetadata(true));

        /// <summary>Hide for fixed-size windows; resizable windows also hide it automatically.</summary>
        public bool ShowMaximizeButton
        {
            get => (bool)GetValue(ShowMaximizeButtonProperty);
            set => SetValue(ShowMaximizeButtonProperty, value);
        }

        public event RoutedEventHandler? CloseClick;

        private static void OnCaptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((WindowTitleBar)d).UpdateCaption();
        }

        private void UpdateCaption()
        {
            if (TitleText == null || SubtitleText == null)
                return;

            ApplySegment(TitleText, Title);
            ApplySegment(SubtitleText, Subtitle);
        }

        private static void ApplySegment(TextBlock text, string? value)
        {
            string formatted = FormatSegment(value);
            text.SetCurrentValue(TextBlock.TextProperty, formatted);
            text.Visibility = string.IsNullOrEmpty(formatted) ? Visibility.Collapsed : Visibility.Visible;
        }

        internal static string FormatSegment(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string text = value.Trim();
            if (text.StartsWith('•') || text.StartsWith('—') || text.StartsWith('-'))
                return " " + text;

            return " • " + text;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _window = Window.GetWindow(this);
            if (_window == null) return;

            ApplyButtonVisibility();
            _window.StateChanged += Window_StateChanged;
            UpdateMaximizeGlyph();
            RefreshLocalizedChrome();
        }

        /// <summary>Re-applies translated tooltips and automation names after a language switch.</summary>
        public void RefreshLocalizedChrome()
        {
            if (_window == null) return;
            ApplyButtonText(MinimizeButton, Services.LocalizationService.Translate("Minimize"));
            ApplyButtonText(CloseButton, Services.LocalizationService.Translate("Close"));
            ApplyButtonText(MaximizeButton, Services.LocalizationService.Translate(HasMaximized ? "Restore" : "Maximize"));
        }

        private bool HasMaximized => _window?.WindowState == WindowState.Maximized;

        private static void ApplyButtonText(Button button, string name)
        {
            button.ToolTip = name;
            System.Windows.Automation.AutomationProperties.SetName(button, name);
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (_window != null)
                _window.StateChanged -= Window_StateChanged;
            _window = null;
        }

        private void ApplyButtonVisibility()
        {
            if (_window == null) return;

            bool resizable = _window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip;
            MinimizeButton.Visibility = ShowMinimizeButton ? Visibility.Visible : Visibility.Collapsed;
            MaximizeButton.Visibility = ShowMaximizeButton && resizable ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Window_StateChanged(object? sender, EventArgs e)
        {
            UpdateMaximizeGlyph();
            RefreshLocalizedChrome();
        }

        private void UpdateMaximizeGlyph()
        {
            if (_window == null || MaximizeGlyph == null) return;

            bool maximized = _window.WindowState == WindowState.Maximized;
            MaximizeGlyph.Glyph = maximized ? FluentGlyphs.Restore : FluentGlyphs.Maximize;
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_window == null || e.ChangedButton != MouseButton.Left || IsInsideButton(e.OriginalSource as DependencyObject))
                return;

            if (e.ClickCount == 2)
            {
                ToggleMaximize();
                return;
            }

            if (_window.WindowState == WindowState.Maximized)
            {
                // Dragging a maximized window restores it, matching the Windows caption.
                SystemCommands.RestoreWindow(_window);
            }

            try { _window.DragMove(); }
            catch (InvalidOperationException) { /* Mouse released before the drag began. */ }
        }

        private void TitleBar_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_window == null) return;

            Point screen = PointToScreen(e.GetPosition(this));
            SystemCommands.ShowSystemMenu(_window, screen);
            e.Handled = true;
        }

        private void ToggleMaximize()
        {
            if (_window == null || _window.ResizeMode is not (ResizeMode.CanResize or ResizeMode.CanResizeWithGrip))
                return;

            if (_window.WindowState == WindowState.Maximized)
                SystemCommands.RestoreWindow(_window);
            else
                SystemCommands.MaximizeWindow(_window);
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_window != null)
                SystemCommands.MinimizeWindow(_window);
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (CloseClick != null)
            {
                CloseClick(this, e);
                return;
            }

            if (_window != null)
                SystemCommands.CloseWindow(_window);
        }

        private static bool IsInsideButton(DependencyObject? source)
        {
            while (source != null)
            {
                if (source is Button)
                    return true;
                source = VisualTreeHelper.GetParent(source);
            }

            return false;
        }
    }
}
