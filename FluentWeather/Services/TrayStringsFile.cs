using FluentWeather.Helpers;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;

namespace FluentWeather.Services
{
    /// <summary>
    /// Local build (v55): writes the texts of the tray icons program (FluentWeatherTray.exe) in the
    /// language of the app to tray-strings.txt in the LocalState folder. Only the language in use is
    /// written, about 2 KB. The tray reads the file and shows English for a missing file or key.
    /// The texts live in Strings/&lt;language&gt;/Resources.resw like every other text of the app.
    /// </summary>
    internal static class TrayStringsFile
    {
        public const string FileName = "tray-strings.txt";

        private static readonly string[] Keys =
        {
            "TrayDetailConditions", "TrayDetailTemperature", "TrayDetailFeelsLike", "TrayDetailHighLow",
            "TrayDetailChange24h", "TrayDetailDewPoint", "TrayDetailHeatIndex", "TrayDetailWindChill", "TrayDetailHumidity",
            "TrayDetailWind", "TrayDetailUV", "TrayDetailPressure", "TrayDetailVisibility", "TrayDetailCloudCeiling",
            "TrayDetailRain1h", "TrayDetailRain6h", "TrayDetailRain24h", "TrayDetailSnow24h", "TrayDetailSunrise",
            "TrayDetailSunset", "TrayDetailMoon", "TrayDetailMoonrise", "TrayDetailMoonset", "TrayDetailObserved",
            "TrayDetailStation", "TrayDetailWaterTemp", "TrayDetailWaterWind", "TrayDetailExtraMarine", "TrayDetailExtraWind",
            "TrayDetailExtraModels", "TrayDetailExtraRiver", "TrayDetailExtraPollen", "TrayDetailAlert",
            "TrayDetailMoreAirport", "TrayDetailMoreSnow", "TrayDetailMoreStorm", "TrayDetailMoreHeat", "TrayDetailMoreFreezing",
            "TrayDetailMoreGround", "TrayDetailMoreSun", "TrayDetailMoreBuoy", "TrayDetailMoreGauge", "TrayDetailMoreHourly",
            "TrayAlertMoreOne", "TrayAlertMoreMany", "TrayAlertMoreShort", "TrayClosesIn", "TrayAlertHeader",
            "TrayShowDetails", "TrayWaiting", "TrayNoData", "TrayNoUpdateSince", "TrayFeelsLikeSuffix", "TrayFooterSameCity",
            "TrayFooterChosen", "TrayFooterUpdated", "TrayAlertsMenu", "TrayShowAlerts", "TrayAlertsOff",
            "TrayAlertsWarnings", "TrayAlertsWatches", "TrayAlertsAll", "TrayDnd", "TrayDndTip", "TrayCloseAfter",
            "TrayNever", "TraySeconds", "TrayOneMinute", "TrayMinutes", "TrayLocations", "TrayShownCity",
            "TrayAllSaved", "TrayTestAlert", "TrayTestAlertTip", "TrayTestName", "TrayTestUntil", "TrayTestPlace",
            "TrayTestSummary", "TrayAlertNote", "TrayOpen", "TrayNumberTemp", "TrayNumberFeels", "TrayHoverDetails",
            "TrayShowAll", "TrayResetDefaults", "TrayNoLocations", "TrayShowsHeader", "TraySameAsApp", "TrayRestart",
            "TrayCloseWindow", "TrayExit", "TrayExitTip", "TrayExitBoth", "TrayAppNotInstalled", "TrayRestartFailed",
            "MoonPhaseNew", "MoonPhaseWaxingCrescent", "MoonPhaseFirstQuarter", "MoonPhaseWaxingGibbous",
            "MoonPhaseFull", "MoonPhaseWaningGibbous", "MoonPhaseLastQuarter", "MoonPhaseWaningCrescent",
            "MoonLitFormat"
        };

        private static bool _done;

        /// <summary>Writes the file once per run of the app, and only when its content changed.</summary>
        public static async Task EnsureAsync()
        {
            if (_done)
                return;
            _done = true;
            try
            {
                var text = new StringBuilder();
                foreach (var key in Keys)
                {
                    var value = Loc.Get(key, "");
                    if (value.Length == 0)
                        continue;
                    text.Append(key).Append('=').Append(value.Replace("\r", "").Replace("\n", "\\n")).Append('\n');
                }

                var content = text.ToString();
                var path = Path.Combine(ApplicationData.Current.LocalFolder.Path, FileName);
                if (File.Exists(path) && await File.ReadAllTextAsync(path) == content)
                    return;
                await File.WriteAllTextAsync(path, content, new UTF8Encoding(false));
            }
            catch (Exception)
            {
                // The tray shows English until the next try.
                _done = false;
            }
        }
    }
}
