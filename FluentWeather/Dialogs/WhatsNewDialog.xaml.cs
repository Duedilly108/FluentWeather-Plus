using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using FluentWeather.Helpers;

namespace FluentWeather.Dialogs
{
    public sealed partial class WhatsNewDialog : ContentDialog
    {
        public WhatsNewDialog()
        {
            RequestedTheme = ((FrameworkElement) Window.Current.Content).RequestedTheme;
            InitializeComponent();
            // Local build: the FluentWeather-Plus feature list and the version from the package.
            WhatsNewTextBlock.Text = "PlusWhatsNewBody".GetLocalized();
            try
            {
                PlusVersionBannerText.Text = "PlusWhatsNewVersion".GetLocalized() + " " + Windows.ApplicationModel.Package.Current.Id.Version.Revision;
            }
            catch (System.Exception)
            {
                PlusVersionBannerText.Text = "";
            }
        }
    }
}
