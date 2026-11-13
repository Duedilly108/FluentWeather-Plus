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
