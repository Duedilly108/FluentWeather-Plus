using System;
using System.Runtime.CompilerServices;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace FluentWeather.Helpers
{
    /// <summary>
    /// Local build: the hover panel of the detail cards. It looks like the app's tooltips (the
    /// ToolTip style in App.xaml: the grey to blue grey gradient, a thin light border, white text),
    /// but it opens after ShowDelay. Windows does not let an app change the delay of a ToolTip.
    /// The panel closes when the mouse leaves the card, on a click and when a drag starts.
    /// </summary>
    public static class HoverTip
    {
        public static readonly TimeSpan ShowDelay = TimeSpan.FromMilliseconds(1200);

        private static readonly ConditionalWeakTable<UIElement, Entry> Entries = new();
        private static Popup? _popup;
        private static TextBlock? _text;
        private static DispatcherTimer? _timer;
        private static UIElement? _pending;

        private sealed class Entry
        {
            public string Text = "";
        }

        /// <summary>Local build: the hover text of an element, or an empty string.</summary>
        public static string Get(UIElement element) =>
            Entries.TryGetValue(element, out var entry) ? entry.Text : "";

        /// <summary>Sets or changes the hover text of an element.</summary>
        public static void Set(UIElement element, string text)
        {
            if (Entries.TryGetValue(element, out var entry))
            {
                entry.Text = text;
                if (_pending == element && _popup is { IsOpen: true } && _text != null)
                    _text.Text = text;
                return;
            }

            Entries.Add(element, new Entry { Text = text });
            element.PointerEntered += OnPointerEntered;
            element.PointerExited += OnPointerLeft;
            element.PointerCanceled += OnPointerLeft;
            element.PointerCaptureLost += OnPointerLeft;
            element.PointerPressed += OnPointerLeft;
            element.RightTapped += (_, _) => Hide();
            element.DragStarting += (_, _) => Hide();
        }

        private static void OnPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not UIElement element)
                return;
            Hide();
            _pending = element;
            if (_timer == null)
            {
                _timer = new DispatcherTimer { Interval = ShowDelay };
                _timer.Tick += (_, _) =>
                {
                    _timer.Stop();
                    if (_pending != null)
                        Show(_pending);
                };
            }
            _timer.Stop();
            _timer.Start();
        }

        private static void OnPointerLeft(object sender, PointerRoutedEventArgs e)
        {
            if (sender is UIElement element && ReferenceEquals(element, _pending))
                Hide();
        }

        public static void Hide()
        {
            _timer?.Stop();
            _pending = null;
            if (_popup != null)
                _popup.IsOpen = false;
        }

        private static void EnsurePopup()
        {
            if (_popup != null)
                return;

            var background = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            background.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0xFF, 0x2B, 0x2B, 0x2D), Offset = 0 });
            background.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0xFF, 0x2B, 0x35, 0x47), Offset = 1 });

            _text = new TextBlock
            {
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Colors.White)
            };
            var border = new Border
            {
                Background = background,
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(9, 6, 9, 8),
                MaxWidth = 320,
                RequestedTheme = ElementTheme.Dark,
                Child = _text,
                IsHitTestVisible = false
            };
            _popup = new Popup { Child = border, IsLightDismissEnabled = false, IsHitTestVisible = false };
        }

        private static void Show(UIElement element)
        {
            try
            {
                if (!Entries.TryGetValue(element, out var entry) || string.IsNullOrEmpty(entry.Text))
                    return;
                if (element is FrameworkElement { IsLoaded: false } || element.Visibility != Visibility.Visible)
                    return;

                EnsurePopup();
                _text!.Text = entry.Text;

                var root = Window.Current.Content as UIElement;
                var bounds = Window.Current.Bounds;
                var topLeft = element.TransformToVisual(root).TransformPoint(new Point(0, 0));
                var size = element.RenderSize;

                var border = (FrameworkElement) _popup!.Child;
                border.Measure(new Size(320, double.PositiveInfinity));
                var tip = border.DesiredSize;

                // Below the card, or above it when there is no room below. Kept inside the window.
                var x = Math.Max(4, Math.Min(topLeft.X, bounds.Width - tip.Width - 4));
                var y = topLeft.Y + size.Height + 6;
                if (y + tip.Height > bounds.Height - 4)
                    y = Math.Max(4, topLeft.Y - tip.Height - 6);

                _popup.HorizontalOffset = x;
                _popup.VerticalOffset = y;
                _popup.IsOpen = true;
            }
            catch (Exception)
            {
                // No hover panel this time.
            }
        }
    }
}
