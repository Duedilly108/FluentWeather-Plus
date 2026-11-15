using System;
using System.Numerics;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Shapes;
using FluentWeather.Services;

namespace FluentWeather.Helpers
{
    /// <summary>
    /// Local build: a soft drop shadow under the text of a title, such as "Daily forecast".
    ///
    /// XAML has no text shadow. The shadow is a composition DropShadow that uses the shape of the
    /// text (its alpha mask). It is drawn in an empty element that sits behind every other child of
    /// the parent of the title, and it follows the place and the size of the title.
    /// </summary>
    public static class TitleShadow
    {
        private const float BlurRadius = 4f;
        private const float Opacity = 0.65f;
        private static readonly Vector3 Offset = new(0, 2, 0);

        public static void Attach(TextBlock title)
        {
            if (title == null)
                return;
            if (title.IsLoaded)
                Add(title);
            else
                title.Loaded += Title_Loaded;
        }

        private static void Title_Loaded(object sender, RoutedEventArgs e)
        {
            var title = (TextBlock) sender;
            title.Loaded -= Title_Loaded;
            Add(title);
        }

        private static void Add(TextBlock title)
        {
            try
            {
                if (title.Parent is not Panel parent || parent is StackPanel)
                    return;

                // An empty element at the top left corner of the parent, behind the other children.
                var host = new Rectangle
                {
                    Width = 0,
                    Height = 0,
                    IsHitTestVisible = false,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top
                };
                parent.Children.Insert(0, host);

                var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
                var shadow = compositor.CreateDropShadow();
                shadow.BlurRadius = BlurRadius;
                shadow.Opacity = Opacity;
                shadow.Color = Colors.Black;
                shadow.Offset = Offset;
                shadow.Mask = title.GetAlphaMask();

                var sprite = compositor.CreateSpriteVisual();
                sprite.Shadow = shadow;
                ElementCompositionPreview.SetElementChildVisual(host, sprite);

                void Place()
                {
                    try
                    {
                        var at = title.TransformToVisual(host).TransformPoint(new Windows.Foundation.Point(0, 0));
                        sprite.Offset = new Vector3((float) at.X, (float) at.Y, 0);
                        sprite.Size = new Vector2((float) title.ActualWidth, (float) title.ActualHeight);
                        sprite.IsVisible = title.Visibility == Visibility.Visible && title.ActualWidth > 0;
                    }
                    catch (Exception)
                    {
                        // The title is not in the visual tree at this moment.
                    }
                }

                // The title can move when the window or a neighbor changes size, and it can change
                // its text. The place is checked after each layout pass.
                title.LayoutUpdated += (_, _) => Place();
                Place();
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "TitleShadow");
            }
        }
    }
}
