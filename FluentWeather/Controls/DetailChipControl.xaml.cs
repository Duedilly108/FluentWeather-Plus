using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace FluentWeather.Controls
{
    public sealed partial class DetailChipControl : UserControl
    {
        public string Title
        {
            get { return (string) GetValue(TitleProperty); }
            set { SetValue(TitleProperty, value); }
        }

        private static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register(
                nameof(Title),
                typeof(string),
                typeof(DetailChipControl),
                new PropertyMetadata(null, new PropertyChangedCallback(OnTitleChanged)));

        private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var currentInstance = (DetailChipControl) d;

            var newValue = (string) e.NewValue;

            currentInstance.TitleText.Text = newValue;
        }


        public string Value
        {
            get { return (string) GetValue(ValueProperty); }
            set { SetValue(ValueProperty, value); }
        }

        private static readonly DependencyProperty ValueProperty =
            DependencyProperty.Register(
                nameof(Value),
                typeof(string),
                typeof(DetailChipControl),
                new PropertyMetadata(null, new PropertyChangedCallback(OnValueChanged)));

        private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var currentInstance = (DetailChipControl) d;

            var newValue = (string) e.NewValue;

            currentInstance.ValueText.Text = newValue;

            // Local build: a plain value replaces the lines of SetLabeledLines.
            if (currentInstance.LinesGrid.Visibility == Visibility.Visible &&
                !string.Equals(newValue, currentInstance._linesValue))
            {
                currentInstance.LinesGrid.Visibility = Visibility.Collapsed;
                currentInstance.ValueText.Visibility = Visibility.Visible;
                RelativePanel.SetBelow(currentInstance.NoteText, currentInstance.ValueText);
            }
        }

        /// <summary>
        /// Local build: shows the value as lines of a plain label and a bold value, for example
        /// "Highs 36-39" and "Lows 26-29", with the values in one column. Setting Value later shows
        /// a plain value again.
        /// </summary>
        public void SetLabeledLines(params (string Label, string Value)[] lines)
        {
            // Value holds the same text, so a later plain Value differs from it and shows it again.
            var joined = string.Join("\n", System.Linq.Enumerable.Select(lines, l => l.Label + " " + l.Value));
            _linesValue = joined;
            SetValue(ValueProperty, joined);

            LinesGrid.Children.Clear();
            LinesGrid.RowDefinitions.Clear();
            LinesGrid.ColumnDefinitions.Clear();
            LinesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            LinesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var bodyStyle = (Style) Application.Current.Resources["BodyTextBlockStyle"];
            var strongStyle = (Style) Application.Current.Resources["BodyStrongTextBlockStyle"];
            for (var i = 0; i < lines.Length; i++)
            {
                LinesGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var label = new TextBlock { Text = lines[i].Label, Style = bodyStyle };
                var value = new TextBlock { Text = lines[i].Value, Style = strongStyle };
                Grid.SetRow(label, i);
                Grid.SetRow(value, i);
                Grid.SetColumn(value, 1);
                LinesGrid.Children.Add(label);
                LinesGrid.Children.Add(value);
            }
            LinesGrid.Visibility = Visibility.Visible;
            ValueText.Visibility = Visibility.Collapsed;
            RelativePanel.SetBelow(NoteText, LinesGrid);
        }

        /// <summary>Local build: an optional smaller line under the value. Empty or null hides it.</summary>
        public string Note
        {
            get { return NoteText.Text; }
            set
            {
                NoteText.Text = value ?? "";
                NoteText.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        private string? _linesValue;

        private readonly Windows.UI.Xaml.Media.Brush _originalBackground;

        public DetailChipControl()
        {
            InitializeComponent();
            _originalBackground = ChipPanel.Background;
        }

        // Local build: the "Card tint" setting gives the chip a tinted background. Null restores the original.
        public void SetCardBackground(Windows.UI.Xaml.Media.Brush brush)
        {
            ChipPanel.Background = brush ?? _originalBackground;
        }
    }
}
