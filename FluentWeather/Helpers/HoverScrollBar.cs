using System;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;

namespace FluentWeather.Helpers
{
    /// <summary>
    /// Local build: the sideways scroll bar of the main page rows (saved locations, detail cards,
    /// days and hours).
    ///
    /// The bar is hidden until the mouse is over the row or over the bar. It then fades in over
    /// FadeInTime and shows a left arrow, a faint track and the thumb. It fades out over
    /// FadeOutTime after the mouse leaves. The bar keeps the same thickness while it is visible.
    ///
    /// The bar is a small Grid that sits at the bottom of the row, in the same Grid cell as the
    /// row. The space under the content of the row is reserved all the time, so the page does not
    /// move when the bar appears. The content needs a bottom margin of BarHeight plus the gap.
    /// The gap under the content is 6 points, so the bottom margin is 22.
    ///
    /// The default scroll bar of the row is hidden only after this bar is in place. When anything
    /// fails here, the default scroll bar stays.
    /// </summary>
    public sealed class HoverScrollBar
    {
        public const double BarThickness = 5;
        public const double ArrowSize = 16;
        public const double BarHeight = ArrowSize;
        public const double Gap = 6;
        public static readonly TimeSpan FadeInTime = TimeSpan.FromMilliseconds(450);
        public static readonly TimeSpan FadeOutTime = TimeSpan.FromMilliseconds(150);

        private static readonly TimeSpan LeaveDelay = TimeSpan.FromMilliseconds(60);
        private const double MinThumbWidth = 24;

        private readonly FrameworkElement _host;
        private readonly ScrollViewer _scroll;
        private readonly double _stepWidth;
        private readonly Action<int>? _step;

        private readonly Grid _bar = new();
        private readonly Grid _track = new();
        private readonly Border _trackLine = new();
        private readonly Grid _thumbHost = new();
        private readonly Border _thumb = new();
        private readonly Button _left = new();
        private readonly Button _right = new();
        private readonly SolidColorBrush _trackBrush = new();
        private readonly SolidColorBrush _thumbBrush = new();
        private readonly SolidColorBrush _arrowBrush = new();
        private readonly SolidColorBrush _glyphBrush = new();
        private readonly DispatcherTimer _leaveTimer = new() { Interval = LeaveDelay };

        private Storyboard? _fade;
        private bool _hostOver;
        private bool _barOver;
        private bool _dragging;
        private bool _shown;
        private double _dragStartX;
        private double _dragStartOffset;

        /// <summary>
        /// Adds the bar to a row. The host is a ScrollViewer, or a control that has a ScrollViewer
        /// inside, such as a ListView. A click on an arrow moves the row by stepWidth points, or
        /// calls step when step is given (+1 to the right, -1 to the left).
        /// </summary>
        public static void Attach(FrameworkElement host, double stepWidth, Action<int>? step = null)
        {
            if (host == null)
                return;

            if (host.IsLoaded)
            {
                Create(host, stepWidth, step);
                return;
            }

            RoutedEventHandler? onLoaded = null;
            onLoaded = (_, _) =>
            {
                host.Loaded -= onLoaded;
                Create(host, stepWidth, step);
            };
            host.Loaded += onLoaded;
        }

        private static void Create(FrameworkElement host, double stepWidth, Action<int>? step)
        {
            try
            {
                var scroll = host as ScrollViewer ?? FindScrollViewer(host);
                if (scroll == null || host.Parent is not Grid parent)
                    return;

                var bar = new HoverScrollBar(host, scroll, stepWidth, step);
                bar.Place(parent);
            }
            catch (Exception)
            {
                // The default scroll bar of the row stays when the new bar cannot be made.
            }
        }

        private static ScrollViewer? FindScrollViewer(DependencyObject root)
        {
            var count = Windows.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = Windows.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
                if (child is ScrollViewer viewer)
                    return viewer;

                var found = FindScrollViewer(child);
                if (found != null)
                    return found;
            }

            return null;
        }

        private HoverScrollBar(FrameworkElement host, ScrollViewer scroll, double stepWidth, Action<int>? step)
        {
            _host = host;
            _scroll = scroll;
            _stepWidth = stepWidth;
            _step = step;

            _bar.Height = BarHeight;
            _bar.VerticalAlignment = VerticalAlignment.Bottom;
            _bar.HorizontalAlignment = HorizontalAlignment.Stretch;
            _bar.ColumnSpacing = 6;
            _bar.Opacity = 0;
            _bar.IsHitTestVisible = false;
            _bar.Visibility = Visibility.Collapsed;
            _bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // The track is as tall as the bar, so a click anywhere in that strip pages the row.
            _track.Background = new SolidColorBrush(Colors.Transparent);
            Grid.SetColumn(_track, 1);

            _trackLine.Height = BarThickness;
            _trackLine.VerticalAlignment = VerticalAlignment.Center;
            _trackLine.CornerRadius = new CornerRadius(BarThickness / 2);
            _trackLine.Background = _trackBrush;
            _trackLine.IsHitTestVisible = false;

            // The thumb is drawn BarThickness points tall. Its host is as tall as the bar, so the
            // mouse can grab it easily.
            _thumbHost.Background = new SolidColorBrush(Colors.Transparent);
            _thumbHost.HorizontalAlignment = HorizontalAlignment.Left;
            _thumbHost.Width = MinThumbWidth;

            _thumb.Height = BarThickness;
            _thumb.VerticalAlignment = VerticalAlignment.Center;
            _thumb.CornerRadius = new CornerRadius(BarThickness / 2);
            _thumb.Background = _thumbBrush;
            _thumb.IsHitTestVisible = false;

            _thumbHost.Children.Add(_thumb);
            _track.Children.Add(_trackLine);
            _track.Children.Add(_thumbHost);

            ConfigureArrow(_left, "\uE76B", "ScrollBarLeftName", "Scroll left");
            ConfigureArrow(_right, "\uE76C", "ScrollBarRightName", "Scroll right");
            Grid.SetColumn(_left, 0);
            Grid.SetColumn(_right, 2);

            _bar.Children.Add(_left);
            _bar.Children.Add(_track);
            _bar.Children.Add(_right);

            _leaveTimer.Tick += LeaveTimer_Tick;
        }

        private void ConfigureArrow(Button button, string glyph, string nameKey, string fallbackName)
        {
            button.Width = ArrowSize;
            button.Height = ArrowSize;
            button.MinWidth = 0;
            button.MinHeight = 0;
            button.Padding = new Thickness(0);
            button.BorderThickness = new Thickness(0);
            button.CornerRadius = new CornerRadius(4);
            button.Background = _arrowBrush;
            button.IsTabStop = false;
            button.Content = new FontIcon
            {
                FontFamily = IconFont(),
                Glyph = glyph,
                FontSize = 10,
                Foreground = _glyphBrush
            };

            var name = nameKey.GetLocalized();
            AutomationProperties.SetName(button, string.IsNullOrEmpty(name) ? fallbackName : name);
        }

        private static FontFamily IconFont() =>
            Application.Current.Resources.TryGetValue("FluentIcons", out var value) && value is FontFamily family
                ? family
                : new FontFamily("Segoe Fluent Icons");

        private void Place(Grid parent)
        {
            Grid.SetRow(_bar, Grid.GetRow(_host));
            Grid.SetColumn(_bar, Grid.GetColumn(_host));
            Grid.SetRowSpan(_bar, Grid.GetRowSpan(_host));
            Grid.SetColumnSpan(_bar, Grid.GetColumnSpan(_host));
            _bar.Margin = new Thickness(_host.Margin.Left, 0, _host.Margin.Right, _host.Margin.Bottom);

            ApplyTheme();
            parent.Children.Add(_bar);

            _host.PointerEntered += Host_PointerEntered;
            _host.PointerExited += Host_PointerExited;
            _bar.PointerEntered += Bar_PointerEntered;
            _bar.PointerExited += Bar_PointerExited;
            _host.ActualThemeChanged += (_, _) => ApplyTheme();
            _host.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => HostVisibilityChanged());

            _scroll.ViewChanged += (_, _) => RefreshIfShown();
            _scroll.SizeChanged += (_, _) => RefreshIfShown();
            if (_scroll.Content is FrameworkElement content)
                content.SizeChanged += (_, _) => RefreshIfShown();
            _track.SizeChanged += (_, _) => RefreshIfShown();

            _thumbHost.PointerPressed += Thumb_PointerPressed;
            _thumbHost.PointerMoved += Thumb_PointerMoved;
            _thumbHost.PointerReleased += Thumb_PointerReleased;
            _thumbHost.PointerCanceled += Thumb_PointerReleased;
            _thumbHost.PointerCaptureLost += Thumb_PointerReleased;
            _track.PointerPressed += Track_PointerPressed;

            _left.Click += (_, _) => Arrow(-1);
            _right.Click += (_, _) => Arrow(1);

            // Only now is the default scroll bar hidden. The row still scrolls with the wheel,
            // the keys and touch.
            ScrollViewer.SetHorizontalScrollBarVisibility(_host, ScrollBarVisibility.Hidden);
        }

        private void ApplyTheme()
        {
            var light = _host.ActualTheme == ElementTheme.Light;
            var ink = light ? Colors.Black : Colors.White;
            _thumbBrush.Color = Color.FromArgb(0x99, ink.R, ink.G, ink.B);
            _trackBrush.Color = Color.FromArgb(0x24, ink.R, ink.G, ink.B);
            _arrowBrush.Color = Color.FromArgb(0x24, ink.R, ink.G, ink.B);
            _glyphBrush.Color = light ? Color.FromArgb(0xFF, 0x20, 0x20, 0x20) : Color.FromArgb(0xFF, 0xE1, 0xE4, 0xEE);
        }

        // Pointer state. The bar is a sibling of the row, so moving the mouse from the row onto the
        // bar leaves the row first. The short delay keeps the bar from flickering at that moment.

        private void Host_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            _hostOver = true;
            HoverChanged();
        }

        private void Host_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            _hostOver = false;
            HoverChanged();
        }

        private void Bar_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            _barOver = true;
            HoverChanged();
        }

        private void Bar_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            _barOver = false;
            HoverChanged();
        }

        private void HoverChanged()
        {
            _leaveTimer.Stop();
            if (_hostOver || _barOver || _dragging)
                Show();
            else
                _leaveTimer.Start();
        }

        private void LeaveTimer_Tick(object? sender, object e)
        {
            _leaveTimer.Stop();
            if (!_hostOver && !_barOver && !_dragging)
                Hide();
        }

        private void HostVisibilityChanged()
        {
            if (_host.Visibility == Visibility.Visible)
                return;

            // The row is gone. Drop the state, so the bar is not left open when the row returns.
            _hostOver = false;
            _barOver = false;
            _dragging = false;
            _leaveTimer.Stop();
            _shown = false;
            _fade?.Stop();
            _bar.Opacity = 0;
            _bar.IsHitTestVisible = false;
            _bar.Visibility = Visibility.Collapsed;
        }

        private void Show()
        {
            Refresh();
            if (_bar.Visibility != Visibility.Visible || _shown)
                return;

            _shown = true;
            _bar.IsHitTestVisible = true;
            FadeTo(1, FadeInTime);
        }

        private void Hide()
        {
            if (!_shown)
                return;

            _shown = false;
            FadeTo(0, FadeOutTime);
        }

        private void FadeTo(double target, TimeSpan full)
        {
            // Stop() puts the local value back, so keep the value that is on screen now.
            var current = _bar.Opacity;
            _fade?.Stop();
            _bar.Opacity = current;

            var distance = Math.Abs(target - current);
            if (distance < 0.01)
            {
                _bar.Opacity = target;
                if (!_shown)
                    _bar.IsHitTestVisible = false;
                return;
            }

            var animation = new DoubleAnimation
            {
                From = current,
                To = target,
                Duration = new Duration(TimeSpan.FromMilliseconds(full.TotalMilliseconds * distance)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(animation, _bar);
            Storyboard.SetTargetProperty(animation, "Opacity");

            var board = new Storyboard();
            board.Children.Add(animation);
            board.Completed += (_, _) =>
            {
                if (_fade == board && !_shown)
                    _bar.IsHitTestVisible = false;
            };
            _fade = board;
            board.Begin();
        }

        // Size and place of the thumb, and the state of the arrows.

        private void RefreshIfShown()
        {
            if (_shown)
                Refresh();
        }

        private void Refresh()
        {
            var scrollable = _scroll.ScrollableWidth;
            var canScroll = _host.Visibility == Visibility.Visible && scrollable > 0.5;
            _bar.Visibility = canScroll ? Visibility.Visible : Visibility.Collapsed;
            if (!canScroll)
                return;

            var trackWidth = _track.ActualWidth;
            if (trackWidth <= 0)
            {
                _bar.UpdateLayout();
                trackWidth = _track.ActualWidth;
            }

            var extent = _scroll.ExtentWidth;
            if (trackWidth <= 0 || extent <= 0)
                return;

            var width = Math.Min(trackWidth, Math.Max(MinThumbWidth, trackWidth * _scroll.ViewportWidth / extent));
            var room = trackWidth - width;
            var left = room * Math.Clamp(_scroll.HorizontalOffset / scrollable, 0, 1);
            _thumbHost.Width = width;
            _thumbHost.Margin = new Thickness(left, 0, 0, 0);

            _left.Opacity = _scroll.HorizontalOffset <= 0.5 ? 0.35 : 1;
            _right.Opacity = _scroll.HorizontalOffset >= scrollable - 0.5 ? 0.35 : 1;
        }

        // Arrows, a click on the track and a drag of the thumb.

        private void Arrow(int direction)
        {
            if (_step != null)
            {
                _step(direction);
                return;
            }

            var target = Math.Clamp(_scroll.HorizontalOffset + direction * _stepWidth, 0, _scroll.ScrollableWidth);
            _scroll.ChangeView(target, null, null, false);
        }

        private void Track_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var x = e.GetCurrentPoint(_track).Position.X;
            var direction = x < _thumbHost.Margin.Left ? -1 : 1;
            var target = Math.Clamp(_scroll.HorizontalOffset + direction * _scroll.ViewportWidth * 0.9, 0, _scroll.ScrollableWidth);
            _scroll.ChangeView(target, null, null, false);
            e.Handled = true;
        }

        private void Thumb_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_scroll.ScrollableWidth <= 0.5 || !_thumbHost.CapturePointer(e.Pointer))
                return;

            _dragging = true;
            _dragStartX = e.GetCurrentPoint(_track).Position.X;
            _dragStartOffset = _scroll.HorizontalOffset;
            e.Handled = true;
        }

        private void Thumb_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_dragging)
                return;

            var room = _track.ActualWidth - _thumbHost.ActualWidth;
            if (room <= 0)
                return;

            var dx = e.GetCurrentPoint(_track).Position.X - _dragStartX;
            var target = Math.Clamp(_dragStartOffset + dx * (_scroll.ScrollableWidth / room), 0, _scroll.ScrollableWidth);
            _scroll.ChangeView(target, null, null, true);
            e.Handled = true;
        }

        private void Thumb_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_dragging)
                return;

            _dragging = false;
            _thumbHost.ReleasePointerCaptures();
            HoverChanged();
        }
    }
}
