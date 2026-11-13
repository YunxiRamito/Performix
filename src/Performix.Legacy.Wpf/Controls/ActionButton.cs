using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PreySense.Frontend.Controls
{
    public class ActionButton : Button
    {
        static ActionButton()
        {
            DefaultStyleKeyProperty.OverrideMetadata(
                typeof(ActionButton),
                new FrameworkPropertyMetadata(typeof(ActionButton)));
        }

        public static readonly DependencyProperty GlyphProperty =
            DependencyProperty.Register(
                nameof(Glyph),
                typeof(string),
                typeof(ActionButton),
                new PropertyMetadata(string.Empty));

        /// <summary>Segoe Fluent Icons glyph shown before the content; empty hides it.</summary>
        public string Glyph
        {
            get => (string)GetValue(GlyphProperty);
            set => SetValue(GlyphProperty, value);
        }

        public static readonly DependencyProperty CornerRadiusProperty =
            DependencyProperty.Register(
                nameof(CornerRadius),
                typeof(CornerRadius),
                typeof(ActionButton),
                new PropertyMetadata(new CornerRadius(4)));

        public CornerRadius CornerRadius
        {
            get => (CornerRadius)GetValue(CornerRadiusProperty);
            set => SetValue(CornerRadiusProperty, value);
        }
    }
}
