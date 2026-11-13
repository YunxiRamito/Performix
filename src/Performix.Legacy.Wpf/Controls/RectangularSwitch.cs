using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace PreySense.Frontend.Controls
{
    /// <summary>
    /// Two-segment pill switch. One source of truth: <see cref="ToggleButton.IsChecked"/>.
    /// A mouse click picks the segment under the pointer (left = Off, right = On), while
    /// Space/Enter on the keyboard always flips the state deterministically.
    /// When OnText / OffText are empty, the template draws Fluent check/dash glyphs.
    /// </summary>
    public class RectangularSwitch : ToggleButton
    {
        /// <summary>
        /// Target state selected by the current mouse press, or null when the toggle did not
        /// come from a pointer click (keyboard, access key, automation). Keeping the pointer
        /// position out of <see cref="OnToggle"/> is what makes Space/Enter predictable.
        /// </summary>
        private bool? _pointerTargetState;

        static RectangularSwitch()
        {
            DefaultStyleKeyProperty.OverrideMetadata(
                typeof(RectangularSwitch),
                new FrameworkPropertyMetadata(typeof(RectangularSwitch)));

            // A switch is an interactive control: it takes keyboard focus and is reachable
            // with Tab. Both are WPF defaults, stated here so a style cannot silently break them.
            FocusableProperty.OverrideMetadata(
                typeof(RectangularSwitch),
                new FrameworkPropertyMetadata(true));
            IsTabStopProperty.OverrideMetadata(
                typeof(RectangularSwitch),
                new FrameworkPropertyMetadata(true));
        }

        public static readonly DependencyProperty OnTextProperty =
            DependencyProperty.Register(
                nameof(OnText),
                typeof(string),
                typeof(RectangularSwitch),
                new PropertyMetadata(string.Empty, OnLabelTextChanged));

        public string OnText
        {
            get => (string)GetValue(OnTextProperty);
            set => SetValue(OnTextProperty, value);
        }

        public static readonly DependencyProperty OffTextProperty =
            DependencyProperty.Register(
                nameof(OffText),
                typeof(string),
                typeof(RectangularSwitch),
                new PropertyMetadata(string.Empty, OnLabelTextChanged));

        public string OffText
        {
            get => (string)GetValue(OffTextProperty);
            set => SetValue(OffTextProperty, value);
        }

        /// <summary>True when the switch draws glyphs instead of caller-supplied labels.</summary>
        public bool UsesGlyphs => string.IsNullOrEmpty(OnText) && string.IsNullOrEmpty(OffText);

        private static void OnLabelTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((RectangularSwitch)d).RefreshLabelParts();

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            RefreshLabelParts();
        }

        private void RefreshLabelParts()
        {
            Visibility text = UsesGlyphs ? Visibility.Collapsed : Visibility.Visible;
            Visibility glyph = UsesGlyphs ? Visibility.Visible : Visibility.Collapsed;
            if (GetTemplateChild("PART_OnText") is UIElement onText) onText.Visibility = text;
            if (GetTemplateChild("PART_OffText") is UIElement offText) offText.Visibility = text;
            if (GetTemplateChild("PART_OnGlyph") is UIElement onGlyph) onGlyph.Visibility = glyph;
            if (GetTemplateChild("PART_OffGlyph") is UIElement offGlyph) offGlyph.Visibility = glyph;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            // Remember which half was pressed; the click itself is still raised (and the
            // Command executed) by ButtonBase on mouse-up.
            _pointerTargetState = IsEnabled && ActualWidth > 0
                ? e.GetPosition(this).X > ActualWidth / 2.0
                : null;
            base.OnMouseLeftButtonDown(e);
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            try
            {
                base.OnMouseLeftButtonUp(e);
            }
            finally
            {
                // OnToggle normally consumed it already; this also covers a press that was
                // dragged off the switch and therefore never clicked.
                _pointerTargetState = null;
            }
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            // ButtonBase releases capture inside its mouse-up handling, *before* it raises the
            // click: keep the pending target there. Capture lost while the button is still
            // held down means the press was cancelled, so forget the target.
            if (Mouse.LeftButton != MouseButtonState.Released)
            {
                _pointerTargetState = null;
            }

            base.OnLostMouseCapture(e);
        }

        private static bool IsActivationKey(Key key)
            => key == Key.Space || key == Key.Enter || key == Key.Return;

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (!IsActivationKey(e.Key))
            {
                base.OnKeyDown(e);
                return;
            }

            // Alt+Space is the window system menu, not a switch toggle.
            if (e.Key == Key.Space &&
                (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == ModifierKeys.Alt)
            {
                base.OnKeyDown(e);
                return;
            }

            if (!IsEnabled)
            {
                e.Handled = true;
                return;
            }

            // Keyboard activation never guesses at pointer position.
            _pointerTargetState = null;
            if (!e.IsRepeat)
            {
                // Goes through ToggleButton.OnClick: IsChecked flips once and the bound
                // Command still runs, exactly like Enter on a standard button.
                OnClick();
            }

            e.Handled = true;
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (IsActivationKey(e.Key))
            {
                // The toggle already happened on key-down; swallow the key-up so the
                // ButtonBase spacebar path cannot click a second time.
                e.Handled = true;
                return;
            }

            base.OnKeyUp(e);
        }

        protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnGotKeyboardFocus(e);
            if (e.OriginalSource == this && GetTemplateChild("PART_Outer") is Border outer &&
                TryFindResource("ControlFocusedBorderBrush") is Brush)
            {
                outer.SetResourceReference(Border.BorderBrushProperty, "ControlFocusedBorderBrush");
            }
        }

        protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnLostKeyboardFocus(e);
            if (e.OriginalSource == this && GetTemplateChild("PART_Outer") is Border outer)
            {
                // Clearing the local value lets the template's mouse-over trigger own the brush again.
                outer.ClearValue(Border.BorderBrushProperty);
            }
        }

        protected override void OnToggle()
        {
            if (!IsEnabled) return;

            // One source of truth: the checked state. Mouse presses supply their target from
            // the click position; every other activation path is a plain toggle, so Space and
            // Enter behave exactly like one another and never depend on the last mouse position.
            bool? target = _pointerTargetState;
            _pointerTargetState = null;
            // SetCurrentValue keeps a caller's TwoWay IsChecked binding intact (ToggleButton
            // itself toggles this way); a plain SetValue would shadow the binding locally.
            SetCurrentValue(IsCheckedProperty, target ?? (IsChecked != true));
        }
    }
}
