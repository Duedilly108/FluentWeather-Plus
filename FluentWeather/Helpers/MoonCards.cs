using System;
using System.Globalization;
using Windows.Foundation;
using Windows.Storage;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Shapes;

namespace FluentWeather.Helpers
{
    /// <summary>
    /// Local build: the moon row of the day buttons (v54). The row sits under the temperatures and
    /// shows the moon with its lit part, the name of the phase and the lit percentage.
    ///
    /// A day button is 186 points wide (v55, 174 in v54). The width fits the app's own phase names in
    /// all 33 languages on one line. A longer name from weather.com ends with an ellipsis, and the
    /// hover text shows it in full. It is 100 high with the moon row and 70 high without it.
    /// The moon button in the title of the daily forecast turns the row on and off. The choice is
    /// kept in the local settings under "showMoonRow". The row is on by default.
    ///
    /// The moon is drawn here, so every phase has a picture, with the lit part cut to the exact
    /// percentage. The days from weather.com, the Open-Meteo days and the recorded past days all
    /// use the same code.
    /// </summary>
    internal static class MoonCards
    {
        /// <summary>The width of every day button, past days included. With 8 points between the buttons the row moves 194 points per button.</summary>
        public const double CardWidth = 186;

        public const double HeightWithMoon = 100;

        public const double HeightWithoutMoon = 70;

        public const string SettingKey = "showMoonRow";

        /// <summary>The Tag of the element that holds the moon row, so the code can find it again.</summary>
        public const string RowTag = "moon";

        public static bool IsOn()
        {
            try
            {
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(SettingKey, out var stored) && stored is bool on)
                    return on;
            }
            catch (Exception)
            {
                // The default applies.
            }

            return true;
        }

        public static void SetOn(bool on)
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[SettingKey] = on;
            }
            catch (Exception)
            {
                // The choice stays in force until the app closes.
            }
        }

        /// <summary>For example "95% lit".</summary>
        public static string LitText(int percent)
        {
            string format;
            try
            {
                format = "MoonLitFormat".GetLocalized();
            }
            catch (Exception)
            {
                format = "";
            }

            if (string.IsNullOrEmpty(format) || !format.Contains("{0}"))
                format = "{0}% lit";

            return string.Format(CultureInfo.CurrentCulture, format, percent);
        }

        /// <summary>Shows or hides the moon row of a day button and sets the height of the button to match.</summary>
        public static void SetRowVisible(Button button, bool visible)
        {
            button.Height = visible ? HeightWithMoon : HeightWithoutMoon;

            if (button.Content is not Grid root)
                return;

            foreach (var child in root.Children)
            {
                if (child is FrameworkElement element && element.Tag is string tag && tag == RowTag)
                    element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        /// <summary>The row: the moon on the left, the name over the lit percentage on the right. A name that is too long is cut with an ellipsis.</summary>
        public static Grid CreateRow(string name, int percent, bool waxing)
        {
            var row = new Grid
            {
                Tag = RowTag,
                ColumnSpacing = 6,
                Margin = new Thickness(0, 6, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var icon = CreateIcon(percent, waxing);
            Grid.SetColumn(icon, 0);
            row.Children.Add(icon);

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock
            {
                Text = name,
                FontSize = 11,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            text.Children.Add(new TextBlock
            {
                Text = LitText(percent),
                FontSize = 11,
                MaxLines = 1,
                Opacity = 0.8,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            return row;
        }

        // The moon is a disc of 18 points with an outline. The lit part is drawn over it. The
        // colors work on the dark and on the light card.

        private const double Center = 10;
        private const double Radius = 8.5;

        private static FrameworkElement CreateIcon(int percent, bool waxing)
        {
            var canvas = new Canvas { Width = 20, Height = 20 };

            var disc = new Ellipse
            {
                Width = 18,
                Height = 18,
                Fill = new SolidColorBrush(Color.FromArgb(0xFF, 0x5B, 0x62, 0x77)),
                Stroke = new SolidColorBrush(Color.FromArgb(0xFF, 0x9A, 0xA1, 0xB5)),
                StrokeThickness = 1
            };
            Canvas.SetLeft(disc, 1);
            Canvas.SetTop(disc, 1);
            canvas.Children.Add(disc);

            var lit = LitGeometry(percent, waxing);
            if (lit != null)
                canvas.Children.Add(new Path { Data = lit, Fill = new SolidColorBrush(Color.FromArgb(0xFF, 0xF2, 0xE3, 0xA8)) });

            return new Viewbox
            {
                Width = 22,
                Height = 22,
                VerticalAlignment = VerticalAlignment.Center,
                Child = canvas
            };
        }

        /// <summary>
        /// The lit part of the disc. A waxing moon is lit on the right and a waning moon on the left,
        /// as seen from the northern hemisphere. The edge of the disc is half of a circle. The other
        /// edge, the terminator, is half of an ellipse. It bulges toward the dark side for a
        /// crescent and toward the lit side for a gibbous moon.
        /// </summary>
        private static Geometry? LitGeometry(int percent, bool waxing)
        {
            var fraction = percent / 100.0;
            if (fraction < 0.02)
                return null;

            if (fraction > 0.99)
                return new EllipseGeometry { Center = new Point(Center, Center), RadiusX = Radius, RadiusY = Radius };

            var radiusX = Math.Max(0.01, Math.Abs(1 - 2 * fraction) * Radius);
            var gibbous = fraction > 0.5;
            var edge = waxing ? SweepDirection.Clockwise : SweepDirection.Counterclockwise;
            var terminator = waxing == gibbous ? SweepDirection.Clockwise : SweepDirection.Counterclockwise;

            var figure = new PathFigure
            {
                StartPoint = new Point(Center, Center - Radius),
                IsClosed = true,
                IsFilled = true
            };
            figure.Segments.Add(new ArcSegment
            {
                Point = new Point(Center, Center + Radius),
                Size = new Size(Radius, Radius),
                SweepDirection = edge,
                IsLargeArc = false
            });
            figure.Segments.Add(new ArcSegment
            {
                Point = new Point(Center, Center - Radius),
                Size = new Size(radiusX, Radius),
                SweepDirection = terminator,
                IsLargeArc = false
            });

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            return geometry;
        }
    }
}
