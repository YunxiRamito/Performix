using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace PreySense.Frontend.Controls
{
    [ContentProperty(nameof(CardContent))]
    public partial class SettingCard : UserControl
    {
        public SettingCard()
        {
            InitializeComponent();
        }

        public static readonly DependencyProperty GlyphProperty =
            DependencyProperty.Register(
                nameof(Glyph),
                typeof(string),
                typeof(SettingCard),
                new PropertyMetadata(string.Empty));

        /// <summary>Segoe Fluent Icons glyph shown in the card header; empty hides it.</summary>
        public string Glyph
        {
            get => (string)GetValue(GlyphProperty);
            set => SetValue(GlyphProperty, value);
        }

        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register(
                nameof(Title),
                typeof(string),
                typeof(SettingCard),
                new PropertyMetadata(string.Empty));

        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        public static readonly DependencyProperty HeaderRightContentProperty =
            DependencyProperty.Register(
                nameof(HeaderRightContent),
                typeof(object),
                typeof(SettingCard),
                new PropertyMetadata(null));

        public object? HeaderRightContent
        {
            get => GetValue(HeaderRightContentProperty);
            set => SetValue(HeaderRightContentProperty, value);
        }

        public static readonly DependencyProperty CardMarginProperty =
            DependencyProperty.Register(
                nameof(CardMargin),
                typeof(Thickness),
                typeof(SettingCard),
                new PropertyMetadata(new Thickness(0, 0, 0, 10)));

        public Thickness CardMargin
        {
            get => (Thickness)GetValue(CardMarginProperty);
            set => SetValue(CardMarginProperty, value);
        }

        public static readonly DependencyProperty CardContentProperty =
            DependencyProperty.Register(
                nameof(CardContent),
                typeof(object),
                typeof(SettingCard),
                new PropertyMetadata(null));

        public object? CardContent
        {
            get => GetValue(CardContentProperty);
            set => SetValue(CardContentProperty, value);
        }
    }
}
