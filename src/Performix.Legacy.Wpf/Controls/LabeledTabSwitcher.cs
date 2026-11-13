using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PreySense.Frontend.Controls
{
    public class LabeledTabSwitcher : Control
    {
        static LabeledTabSwitcher()
        {
            DefaultStyleKeyProperty.OverrideMetadata(
                typeof(LabeledTabSwitcher),
                new FrameworkPropertyMetadata(typeof(LabeledTabSwitcher)));
        }

        public static readonly DependencyProperty LeftTextProperty =
            DependencyProperty.Register(nameof(LeftText), typeof(string), typeof(LabeledTabSwitcher), new PropertyMetadata("Option 1"));

        public string LeftText
        {
            get => (string)GetValue(LeftTextProperty);
            set => SetValue(LeftTextProperty, value);
        }

        public static readonly DependencyProperty RightTextProperty =
            DependencyProperty.Register(nameof(RightText), typeof(string), typeof(LabeledTabSwitcher), new PropertyMetadata("Option 2"));

        public string RightText
        {
            get => (string)GetValue(RightTextProperty);
            set => SetValue(RightTextProperty, value);
        }

        public static readonly DependencyProperty LeftGlyphProperty =
            DependencyProperty.Register(nameof(LeftGlyph), typeof(string), typeof(LabeledTabSwitcher), new PropertyMetadata(string.Empty));

        /// <summary>Segoe Fluent Icons glyph shown before <see cref="LeftText"/>; empty hides it.</summary>
        public string LeftGlyph
        {
            get => (string)GetValue(LeftGlyphProperty);
            set => SetValue(LeftGlyphProperty, value);
        }

        public static readonly DependencyProperty RightGlyphProperty =
            DependencyProperty.Register(nameof(RightGlyph), typeof(string), typeof(LabeledTabSwitcher), new PropertyMetadata(string.Empty));

        /// <summary>Segoe Fluent Icons glyph shown before <see cref="RightText"/>; empty hides it.</summary>
        public string RightGlyph
        {
            get => (string)GetValue(RightGlyphProperty);
            set => SetValue(RightGlyphProperty, value);
        }

        public static readonly DependencyProperty SelectedIndexProperty =
            DependencyProperty.Register(nameof(SelectedIndex), typeof(int), typeof(LabeledTabSwitcher),
                new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedIndexChanged));

        public int SelectedIndex
        {
            get => (int)GetValue(SelectedIndexProperty);
            set => SetValue(SelectedIndexProperty, value);
        }

        public static readonly RoutedEvent SelectionChangedEvent =
            EventManager.RegisterRoutedEvent(nameof(SelectionChanged), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(LabeledTabSwitcher));

        public event RoutedEventHandler SelectionChanged
        {
            add => AddHandler(SelectionChangedEvent, value);
            remove => RemoveHandler(SelectionChangedEvent, value);
        }

        private RadioButton? _leftRadio;
        private RadioButton? _rightRadio;

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            if (_leftRadio != null) _leftRadio.Checked -= OnRadioChecked;
            if (_rightRadio != null) _rightRadio.Checked -= OnRadioChecked;

            _leftRadio = GetTemplateChild("PART_LeftRadio") as RadioButton;
            _rightRadio = GetTemplateChild("PART_RightRadio") as RadioButton;

            string group = "LabeledTabSwitcher_" + Guid.NewGuid().ToString("N");
            if (_leftRadio != null)
            {
                _leftRadio.GroupName = group;
                _leftRadio.Checked += OnRadioChecked;
            }
            if (_rightRadio != null)
            {
                _rightRadio.GroupName = group;
                _rightRadio.Checked += OnRadioChecked;
            }

            UpdateRadioStates();
        }

        private void OnRadioChecked(object sender, RoutedEventArgs e)
        {
            if (sender == _leftRadio && SelectedIndex != 0)
            {
                SelectedIndex = 0;
            }
            else if (sender == _rightRadio && SelectedIndex != 1)
            {
                SelectedIndex = 1;
            }
        }

        private void UpdateRadioStates()
        {
            if (_leftRadio != null) _leftRadio.IsChecked = (SelectedIndex == 0);
            if (_rightRadio != null) _rightRadio.IsChecked = (SelectedIndex == 1);
        }

        private static void OnSelectedIndexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is LabeledTabSwitcher switcher)
            {
                switcher.UpdateRadioStates();
                switcher.RaiseEvent(new RoutedEventArgs(SelectionChangedEvent));
            }
        }
    }
}
