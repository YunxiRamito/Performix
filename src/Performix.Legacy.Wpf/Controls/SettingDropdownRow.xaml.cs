using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace PreySense.Frontend.Controls
{
    [ContentProperty(nameof(Items))]
    public partial class SettingDropdownRow : UserControl
    {
        /// <summary>
        /// Guards the SelectedItem &lt;-&gt; ComboBox mirror so a change made on one side cannot
        /// bounce back through the other. The inner ComboBox remains the single source of truth;
        /// only SelectedIndex is bound in XAML (W8).
        /// </summary>
        private bool _isSynchronizingSelection;

        public SettingDropdownRow()
        {
            InitializeComponent();
        }

        public ItemCollection Items => InnerComboBox.Items;
        public ComboBox ComboBox => InnerComboBox;

        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register(
                nameof(Title),
                typeof(string),
                typeof(SettingDropdownRow),
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
                typeof(SettingDropdownRow),
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
                typeof(SettingDropdownRow),
                new PropertyMetadata(string.Empty));

        /// <summary>Long explanation; shown as the row tooltip next to an info glyph.</summary>
        public string HelpText
        {
            get => (string)GetValue(HelpTextProperty);
            set => SetValue(HelpTextProperty, value);
        }

        public static readonly DependencyProperty ComboBoxAutomationIdProperty =
            DependencyProperty.Register(
                nameof(ComboBoxAutomationId),
                typeof(string),
                typeof(SettingDropdownRow),
                new PropertyMetadata(string.Empty));

        /// <summary>Stable UI Automation id for the inner combo box.</summary>
        public string ComboBoxAutomationId
        {
            get => (string)GetValue(ComboBoxAutomationIdProperty);
            set => SetValue(ComboBoxAutomationIdProperty, value);
        }

        public static readonly DependencyProperty ItemsSourceProperty =
            DependencyProperty.Register(
                nameof(ItemsSource),
                typeof(IEnumerable),
                typeof(SettingDropdownRow),
                new PropertyMetadata(null));

        public IEnumerable ItemsSource
        {
            get => (IEnumerable)GetValue(ItemsSourceProperty);
            set => SetValue(ItemsSourceProperty, value);
        }

        public static readonly DependencyProperty SelectedItemProperty =
            DependencyProperty.Register(
                nameof(SelectedItem),
                typeof(object),
                typeof(SettingDropdownRow),
                new FrameworkPropertyMetadata(
                    null,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                    OnSelectedItemChanged));

        public object? SelectedItem
        {
            get => GetValue(SelectedItemProperty);
            set => SetValue(SelectedItemProperty, value);
        }

        private static void OnSelectedItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var row = (SettingDropdownRow)d;
            if (row._isSynchronizingSelection) return;

            row._isSynchronizingSelection = true;
            try
            {
                row.InnerComboBox.SelectedItem = e.NewValue;
            }
            finally
            {
                row._isSynchronizingSelection = false;
            }
        }

        public static readonly DependencyProperty SelectedIndexProperty =
            DependencyProperty.Register(
                nameof(SelectedIndex),
                typeof(int),
                typeof(SettingDropdownRow),
                new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

        public int SelectedIndex
        {
            get => (int)GetValue(SelectedIndexProperty);
            set => SetValue(SelectedIndexProperty, value);
        }

        public static readonly DependencyProperty DropdownWidthProperty =
            DependencyProperty.Register(
                nameof(DropdownWidth),
                typeof(double),
                typeof(SettingDropdownRow),
                new PropertyMetadata(double.NaN));

        public double DropdownWidth
        {
            get => (double)GetValue(DropdownWidthProperty);
            set => SetValue(DropdownWidthProperty, value);
        }

        public static readonly DependencyProperty DropdownMinWidthProperty =
            DependencyProperty.Register(
                nameof(DropdownMinWidth),
                typeof(double),
                typeof(SettingDropdownRow),
                new PropertyMetadata(110.0));

        public double DropdownMinWidth
        {
            get => (double)GetValue(DropdownMinWidthProperty);
            set => SetValue(DropdownMinWidthProperty, value);
        }

        public event SelectionChangedEventHandler? SelectionChanged;

        private void InnerComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isSynchronizingSelection)
            {
                // Programmatic SelectedItem change: the item already matches, but the index
                // binding must be current before consumers observe the event.
                SetCurrentValue(SelectedIndexProperty, InnerComboBox.SelectedIndex);
            }
            else
            {
                // User (or ItemsSource) change: publish the ComboBox selection to both
                // dependency properties so they never disagree.
                _isSynchronizingSelection = true;
                try
                {
                    SetCurrentValue(SelectedItemProperty, InnerComboBox.SelectedItem);
                    SetCurrentValue(SelectedIndexProperty, InnerComboBox.SelectedIndex);
                }
                finally
                {
                    _isSynchronizingSelection = false;
                }
            }

            SelectionChanged?.Invoke(this, e);
        }
    }
}
