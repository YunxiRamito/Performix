using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PreySense.Frontend.Controls
{
    public partial class SettingSwitchRow : UserControl
    {
        public SettingSwitchRow()
        {
            InitializeComponent();
        }

        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register(
                nameof(Title),
                typeof(string),
                typeof(SettingSwitchRow),
                new PropertyMetadata(string.Empty));

        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        public static readonly DependencyProperty SubtitleProperty =
            DependencyProperty.Register(
                nameof(Subtitle),
                typeof(string),
                typeof(SettingSwitchRow),
                new PropertyMetadata(string.Empty));

        /// <summary>One-line explanation under the label; empty hides the line.</summary>
        public string Subtitle
        {
            get => (string)GetValue(SubtitleProperty);
            set => SetValue(SubtitleProperty, value);
        }

        public static readonly DependencyProperty HelpTextProperty =
            DependencyProperty.Register(
                nameof(HelpText),
                typeof(string),
                typeof(SettingSwitchRow),
                new PropertyMetadata(string.Empty));

        /// <summary>Long explanation; shown as the row tooltip next to an info glyph.</summary>
        public string HelpText
        {
            get => (string)GetValue(HelpTextProperty);
            set => SetValue(HelpTextProperty, value);
        }

        public static readonly DependencyProperty SwitchAutomationIdProperty =
            DependencyProperty.Register(
                nameof(SwitchAutomationId),
                typeof(string),
                typeof(SettingSwitchRow),
                new PropertyMetadata(string.Empty));

        /// <summary>Stable UI Automation id for the inner switch.</summary>
        public string SwitchAutomationId
        {
            get => (string)GetValue(SwitchAutomationIdProperty);
            set => SetValue(SwitchAutomationIdProperty, value);
        }

        public static readonly DependencyProperty IsCheckedProperty =
            DependencyProperty.Register(
                nameof(IsChecked),
                typeof(bool?),
                typeof(SettingSwitchRow),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

        public bool? IsChecked
        {
            get => (bool?)GetValue(IsCheckedProperty);
            set => SetValue(IsCheckedProperty, value);
        }

        public static readonly DependencyProperty OnTextProperty =
            DependencyProperty.Register(
                nameof(OnText),
                typeof(string),
                typeof(SettingSwitchRow),
                new PropertyMetadata(string.Empty));

        public string OnText
        {
            get => (string)GetValue(OnTextProperty);
            set => SetValue(OnTextProperty, value);
        }

        public static readonly DependencyProperty OffTextProperty =
            DependencyProperty.Register(
                nameof(OffText),
                typeof(string),
                typeof(SettingSwitchRow),
                new PropertyMetadata(string.Empty));

        public string OffText
        {
            get => (string)GetValue(OffTextProperty);
            set => SetValue(OffTextProperty, value);
        }

        public static readonly DependencyProperty CommandProperty =
            DependencyProperty.Register(
                nameof(Command),
                typeof(ICommand),
                typeof(SettingSwitchRow),
                new PropertyMetadata(null));

        public ICommand? Command
        {
            get => (ICommand?)GetValue(CommandProperty);
            set => SetValue(CommandProperty, value);
        }

        public static readonly DependencyProperty CommandParameterProperty =
            DependencyProperty.Register(
                nameof(CommandParameter),
                typeof(object),
                typeof(SettingSwitchRow),
                new PropertyMetadata(null));

        public object? CommandParameter
        {
            get => GetValue(CommandParameterProperty);
            set => SetValue(CommandParameterProperty, value);
        }

        public static readonly DependencyProperty SwitchWidthProperty =
            DependencyProperty.Register(
                nameof(SwitchWidth),
                typeof(double),
                typeof(SettingSwitchRow),
                new PropertyMetadata(56.0));

        public double SwitchWidth
        {
            get => (double)GetValue(SwitchWidthProperty);
            set => SetValue(SwitchWidthProperty, value);
        }
    }
}
