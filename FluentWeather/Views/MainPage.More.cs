using FluentWeather.Adapters;
using FluentWeather.Controls;
using FluentWeather.Helpers;
using FluentWeather.Models;
using FluentWeather.Services;
using FluentWeather.Utils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Shapes = Windows.UI.Xaml.Shapes;

namespace FluentWeather.Views
{
    /// <summary>
    /// Local build: the "More data" parts of the main page (v49). See MoreDataService and AlertService.
    ///   Alerts: a colored band per alert under the location name. A click opens the full text below it.
    ///   A: weather now and flight category from the nearest airport, on the Visibility card.
    ///   B to I: cards in the detail row. They can be moved, hidden and added like the other cards.
    ///   J: thunder risk and snow range on the day buttons, and more lines in the day details.
    ///   K: a chart under the hourly forecast.
    ///   L: the forecaster's discussion, below the radar map.
    /// </summary>
    public sealed partial class MainPage
    {
        private readonly List<CheckBox> _moreCheckBoxes = new();
        private MoreContext? _moreContext;
        private string _visibilityBaseTip = "";
        private List<WeatherAlert> _shownAlerts = new();
        private string? _openAlertKey;
        private bool _hourlyChartOn;
        private List<int> _chartHours = new();
        private MoreDataService.OpenMeteoMore? _chartOpenMeteo;
        private bool _discussionExpanded;
        private bool _chart12h = true;

        private static CultureInfo Ci => CultureInfo.CurrentCulture;

        private void InitializeMoreData()
        {
            // A tray "Show details" link while the app is open.
            AlertService.OpenDetailsRequestedChanged += () =>
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => _ = OpenRequestedAlertAsync());
            DiscussionToggleButton.Content = "MoreDiscussionShowAll".GetLocalized();
        }

        private DetailChipControl MoreChip(MoreItem item) => item switch
        {
            MoreItem.Snow => MoreSnowChip,
            MoreItem.Heat => MoreHeatChip,
            MoreItem.Storm => MoreStormChip,
            MoreItem.Ground => MoreGroundChip,
            MoreItem.Freezing => MoreFreezingChip,
            MoreItem.Sun => MoreSunChip,
            MoreItem.Buoy => MoreBuoyChip,
            _ => MoreGaugeChip
        };

        private IEnumerable<DetailChipControl> MoreChips => MoreDataService.Cards.Select(MoreChip);

        private static bool IsMoreCard(string key) => key.StartsWith("More", StringComparison.Ordinal);

        private static MoreItem MoreItemOf(string key) =>
            Enum.TryParse<MoreItem>(key.Substring(4), out var item) ? item : MoreItem.Snow;

        // --- Update after new data ---

        /// <summary>Called by UpdateUi after the standard cards are filled.</summary>
        private async Task UpdateMoreAsync(RootV3Response data, string placeId)
        {
            try
            {
                var units = await VariousUtils.GetUnitsCode();
                var location = data.v3locationpoint.LocationV3;
                var part = data.v3wxforecastdaily10day?.daypart?.FirstOrDefault();
                int? thunder = null;
                string? thunderText = null;
                for (var i = 0; i < 2 && part?.thunderIndex != null && i < part.thunderIndex.Count; i++)
                {
                    if (part.thunderIndex[i] == null)
                        continue;
                    thunder = part.thunderIndex[i];
                    thunderText = part.thunderCategory != null && i < part.thunderCategory.Count ? part.thunderCategory[i] : null;
                    break;
                }

                _moreContext = new MoreContext
                {
                    Latitude = location.latitude,
                    Longitude = location.longitude,
                    Units = units,
                    CountryCode = location.countryCode,
                    Observation = data.v3wxobservationscurrent,
                    ThunderIndex = thunder,
                    ThunderCategory = thunderText,
                    Hourly = data.v3wxforecasthourly10day
                };
                _visibilityBaseTip = HoverTip.Get(VisibilityChipControl);

                var context = _moreContext;
                await Task.WhenAll(
                    UpdateAlertsAsync(location.latitude, location.longitude, location.countryCode, placeId),
                    UpdateAirportAsync(context, placeId),
                    UpdateMoreCardsAsync(context, placeId),
                    UpdateHourlyChartAsync(context, placeId),
                    UpdateDiscussionAsync(context, placeId));
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "UpdateMoreAsync");
            }
        }

        // --- Alerts ---

        private async Task UpdateAlertsAsync(double latitude, double longitude, string? countryCode, string placeId)
        {
            try
            {
                if (!await MoreDataService.IsOnAsync(MoreItem.Alerts, false))
                {
                    _shownAlerts = new List<WeatherAlert>();
                    await RenderAlertsAsync();
                    return;
                }

                var alerts = await AlertService.GetAsync(latitude, longitude, countryCode);
                if (_displayedPlaceId != placeId)
                    return;
                var level = MoreDataService.AlertLevel;
                _shownAlerts = alerts.Where(a => AlertService.Passes(a, level)).ToList();
                if (_openAlertKey != null && _shownAlerts.All(a => a.Key != _openAlertKey))
                    _openAlertKey = null;
                if (AlertService.OpenDetailsRequested && _shownAlerts.Count > 0)
                {
                    AlertService.OpenDetailsRequested = false;
                    _openAlertKey = _shownAlerts[0].Key;
                }
                await RenderAlertsAsync();
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "UpdateAlertsAsync");
            }
        }

        private async Task OpenRequestedAlertAsync()
        {
            if (!AlertService.OpenDetailsRequested || _shownAlerts.Count == 0)
                return;
            AlertService.OpenDetailsRequested = false;
            _openAlertKey = _shownAlerts[0].Key;
            await RenderAlertsAsync();
        }

        // Red for warnings, orange for watches, dark yellow for advisories, blue for statements.
        // White text has a contrast of 4.5:1 or more on each.
        private static Color AlertColor(WeatherAlert alert) => alert.Significance switch
        {
            "W" => alert.SeverityCode <= 1 ? Color.FromArgb(255, 0x8E, 0x0E, 0x12) : Color.FromArgb(255, 0xB3, 0x26, 0x1E),
            "A" => Color.FromArgb(255, 0xB2, 0x4A, 0x00),
            "Y" => Color.FromArgb(255, 0x7A, 0x5C, 0x00),
            _ => Color.FromArgb(255, 0x34, 0x55, 0x7A)
        };

        private async Task RenderAlertsAsync()
        {
            AlertPanel.Children.Clear();
            const int maxBands = 4;
            foreach (var alert in _shownAlerts.Take(maxBands))
            {
                var until = await AlertService.UntilTextAsync(alert);
                var open = alert.Key == _openAlertKey;

                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(new FontIcon
                {
                    FontFamily = Application.Current.Resources["FluentIcons"] as FontFamily,
                    Glyph = "",
                    FontSize = 15,
                    Foreground = new SolidColorBrush(Colors.White),
                    VerticalAlignment = VerticalAlignment.Center
                });
                row.Children.Add(new TextBlock
                {
                    Text = alert.Name,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Colors.White),
                    VerticalAlignment = VerticalAlignment.Center
                });
                if (until.Length > 0)
                    row.Children.Add(new TextBlock
                    {
                        Text = until,
                        Foreground = new SolidColorBrush(Colors.White),
                        Opacity = 0.9,
                        VerticalAlignment = VerticalAlignment.Center
                    });
                row.Children.Add(new TextBlock
                {
                    Text = (open ? "AlertHideDetails" : "AlertShowDetails").GetLocalized(),
                    Foreground = new SolidColorBrush(Colors.White),
                    Opacity = 0.85,
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });

                var band = new Border
                {
                    Background = new SolidColorBrush(AlertColor(alert)),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(12, 6, 14, 7),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Child = row
                };
                AutomationProperties.SetName(band, alert.Name + " " + until);
                var key = alert.Key;
                band.Tapped += async (_, _) =>
                {
                    _openAlertKey = _openAlertKey == key ? null : key;
                    await RenderAlertsAsync();
                };
                AlertPanel.Children.Add(band);

                if (open)
                    AlertPanel.Children.Add(await AlertDetailAsync(alert, until));
            }

            if (_shownAlerts.Count > maxBands)
                AlertPanel.Children.Add(new TextBlock
                {
                    Text = string.Format(Ci, "AlertMore".GetLocalized(), _shownAlerts.Count - maxBands),
                    Opacity = 0.8,
                    Margin = new Thickness(4, 0, 0, 0)
                });

            AlertPanel.Visibility = AlertPanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // The full text of an alert, in a panel with the gradient of the settings panel and the tooltips.
        private async Task<FrameworkElement> AlertDetailAsync(WeatherAlert alert, string until)
        {
            var stack = new StackPanel { Spacing = 6 };
            stack.Children.Add(new TextBlock { Text = alert.Name, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });

            var meta = new List<string>();
            if (alert.Source.Length > 0)
                meta.Add(alert.Source + ".");
            if (alert.Issued is { } issued)
                meta.Add(string.Format(Ci, "AlertIssued".GetLocalized(),
                    issued.ToString("ddd", Ci) + " " + await VariousUtils.GetTimeBasedOnUserSettings(issued.DateTime)) + ".");
            if (until.Length > 0)
                meta.Add(char.ToUpper(until[0], Ci) + until[1..] + ".");
            stack.Children.Add(new TextBlock { Text = string.Join(" ", meta), Opacity = 0.8, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            if (alert.Area.Length > 0)
                stack.Children.Add(new TextBlock
                {
                    Text = string.Format(Ci, "AlertArea".GetLocalized(), alert.Area),
                    Opacity = 0.8,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap
                });

            var body = new TextBlock
            {
                Text = "AlertLoading".GetLocalized(),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                Margin = new Thickness(0, 6, 0, 0)
            };
            stack.Children.Add(body);
            stack.Children.Add(new TextBlock
            {
                Text = (alert.FromNws ? "AlertCreditNws" : "AlertCreditWeatherCom").GetLocalized(),
                Opacity = 0.65,
                FontSize = 11.5,
                Margin = new Thickness(0, 6, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });

            var panel = new Border
            {
                Background = Resources["PanelBackgroundBrush"] as Brush,
                BorderBrush = Resources["PanelBorderBrush"] as Brush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 12, 16, 14),
                RequestedTheme = ElementTheme.Dark,
                Child = stack
            };

            _ = FillAlertTextAsync(alert, body);
            return panel;
        }

        private static async Task FillAlertTextAsync(WeatherAlert alert, TextBlock body)
        {
            var text = await AlertService.GetDetailTextAsync(alert);
            body.Text = !string.IsNullOrWhiteSpace(text) ? text : alert.Summary.Length > 0 ? alert.Summary : "AlertNoText".GetLocalized();
        }

        // --- A: airport report on the Visibility card ---

        private async Task UpdateAirportAsync(MoreContext context, string placeId)
        {
            try
            {
                if (!await MoreDataService.IsOnAsync(MoreItem.Airport, false))
                {
                    VisibilityChipControl.Note = "";
                    return;
                }
                var text = await MoreDataService.DescribeAsync(MoreItem.Airport, context);
                if (_displayedPlaceId != placeId)
                    return;
                VisibilityChipControl.Note = text?.Note ?? "";
                if (text != null)
                    HoverTip.Set(VisibilityChipControl, _visibilityBaseTip + "\n\n" + text.Tip);
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "UpdateAirportAsync");
            }
        }

        // --- B to I: the cards ---

        private async Task UpdateMoreCardsAsync(MoreContext context, string placeId)
        {
            foreach (var item in MoreDataService.Cards)
            {
                var chip = MoreChip(item);
                try
                {
                    if (!await MoreDataService.IsOnAsync(item, false))
                    {
                        chip.Visibility = Visibility.Collapsed;
                        continue;
                    }
                    var text = await MoreDataService.DescribeAsync(item, context);
                    if (_displayedPlaceId != placeId)
                        return;
                    if (text == null)
                    {
                        chip.Visibility = Visibility.Collapsed;
                        continue;
                    }
                    chip.Value = text.Value;
                    chip.Note = text.Note;
                    HoverTip.Set(chip, text.Tip);
                    chip.Visibility = Visibility.Visible;
                }
                catch (Exception ex)
                {
                    CrashLog.Write(ex, "UpdateMoreCardsAsync " + item);
                    chip.Visibility = Visibility.Collapsed;
                }
            }
        }

        // --- J: day details ---

        /// <summary>The wind text, forecast notes, amounts and thunder risk of a day. Called by LoadInsightsData.</summary>
        private async Task UpdateDayMoreAsync(int dayIndex)
        {
            try
            {
                if (_lastApiData == null || dayIndex < 0 || !await MoreDataService.IsOnAsync(MoreItem.Days, false))
                {
                    DayMoreText.Visibility = Visibility.Collapsed;
                    return;
                }

                var units = await VariousUtils.GetUnitsCode();
                var part = _lastApiData.v3wxforecastdaily10day.daypart[0];
                int dayPart = dayIndex * 2, nightPart = dayIndex * 2 + 1;
                T? At<T>(List<T>? list, int i) => list != null && i >= 0 && i < list.Count ? list[i] : default;

                var lines = new List<string>();
                var dayWind = At(part.windPhrase, dayPart);
                var nightWind = At(part.windPhrase, nightPart);
                if (!string.IsNullOrWhiteSpace(dayWind))
                    lines.Add(string.Format(Ci, "MoreDayWind".GetLocalized(), dayWind));
                if (!string.IsNullOrWhiteSpace(nightWind))
                    lines.Add(string.Format(Ci, "MoreDayWindNight".GetLocalized(), nightWind));

                foreach (var note in new[] { At(part.qualifierPhrase, dayPart), At(part.qualifierPhrase, nightPart) }
                             .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct())
                    lines.Add(string.Format(Ci, "MoreDayNote".GetLocalized(), note));

                double Sum(List<double?>? list) => (At(list, dayPart) ?? 0) + (At(list, nightPart) ?? 0);
                var rain = part.qpfRain != null ? Sum(part.qpfRain) : Sum(part.qpf);
                var snow = Sum(part.qpfSnow);
                var ice = Sum(part.qpfIce);
                // weather.com gives a snow range ("1-3", "< 1") when it expects snow. It is shown in place of the amount.
                var range = new[] { At(part.snowRange, dayPart), At(part.snowRange, nightPart) }.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r));
                var amounts = MoreDataService.AmountsText(rain, range != null ? 0 : snow, ice, units);
                if (range != null)
                    amounts = (amounts.Length > 0 ? amounts + ", " : "") +
                              string.Format(Ci, "MoreDaySnowRange".GetLocalized(), range.Trim() + " " + MeasureUnitUtils.GetSnowPrecipitationUnits(units));
                lines.Add(amounts.Length > 0
                    ? string.Format(Ci, "MoreDayAmounts".GetLocalized(), amounts)
                    : "MoreDayNoAmounts".GetLocalized());

                var thunder = new[] { At(part.thunderIndex, dayPart), At(part.thunderIndex, nightPart) }.Where(t => t != null).Max();
                if (thunder != null)
                {
                    var category = new[] { At(part.thunderCategory, dayPart), At(part.thunderCategory, nightPart) }
                        .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
                    lines.Add(string.Format(Ci, "MoreThunderRisk".GetLocalized(), thunder) +
                              (category != null && thunder > 0 ? " (" + category + ")" : ""));
                }

                DayMoreText.Text = string.Join("\n", lines);
                DayMoreText.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "UpdateDayMoreAsync");
                DayMoreText.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>Makes the day buttons again, so they show or hide the thunder and snow marks.</summary>
        private async Task RebuildDayButtonsAsync()
        {
            if (_lastApiData == null)
                return;
            DayButtonAdapter.ShowBadges = await MoreDataService.IsOnAsync(MoreItem.Days, false);
            DayButtonAdapter.Units = await VariousUtils.GetUnitsCode();
            var adapters = new List<DayButtonAdapter>();
            for (var i = 0; i < _lastApiData.v3wxforecastdaily10day.dayOfWeek.Count; i++)
                adapters.Add(new DayButtonAdapter(_lastApiData.v3wxforecastdaily10day, i));
            RepeaterDays.ItemsSource = adapters;
            EmulateDayButtonClick(0);
        }

        // --- K: hourly chart ---

        private async Task UpdateHourlyChartAsync(MoreContext context, string placeId)
        {
            try
            {
                _hourlyChartOn = await MoreDataService.IsOnAsync(MoreItem.Hourly, false);
                _chart12h = await VariousUtils.Is12HourAsync();
                if (!_hourlyChartOn)
                {
                    HourlyChartHost.Visibility = Visibility.Collapsed;
                    return;
                }
                HourlyChartHost.Visibility = Visibility.Visible;
                DrawHourlyChart();
                // Sunshine per hour comes from Open-Meteo. The chart is drawn again when it arrives.
                var openMeteo = await MoreDataService.GetOpenMeteoAsync(context.Latitude, context.Longitude);
                if (_displayedPlaceId != placeId)
                    return;
                _chartOpenMeteo = openMeteo;
                DrawHourlyChart();
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "UpdateHourlyChartAsync");
            }
        }

        /// <summary>Called by LoadHourlyData with the hours shown in the list.</summary>
        private void SetChartHours(IEnumerable<HourDataAdapter> hours)
        {
            _chartHours = hours.Select(h => h.Index).ToList();
            DrawHourlyChart();
        }

        private void HourlyChartHost_SizeChanged(object sender, SizeChangedEventArgs e) => DrawHourlyChart();

        private static readonly Color RainColor = Color.FromArgb(255, 0x4F, 0xA8, 0xF0);
        private static readonly Color SnowColor = Color.FromArgb(255, 0xDD, 0xE6, 0xF2);
        private static readonly Color IceColor = Color.FromArgb(255, 0x7F, 0xE0, 0xF0);
        private static readonly Color SunColor = Color.FromArgb(255, 0xF5, 0xC8, 0x42);
        private static readonly Color SevereColor = Color.FromArgb(255, 0xE5, 0x48, 0x4D);

        /// <summary>
        /// Stacked bars per hour for rain, snow and ice, the chance of precipitation as a dashed line,
        /// sunshine minutes as a yellow bar along the top, and a red mark on hours that weather.com rates
        /// as severe (wxSeverity 3 or more). Snow is drawn as water: 10 units of snow count as 1 of rain.
        /// </summary>
        private void DrawHourlyChart()
        {
            var canvas = HourlyChartCanvas;
            canvas.Children.Clear();
            var h = _lastApiData?.v3wxforecasthourly10day;
            var width = HourlyChartHost.ActualWidth;
            var height = HourlyChartHost.ActualHeight;
            if (!_hourlyChartOn || h == null || _chartHours.Count == 0 || width < 60 || height < 60)
                return;

            var units = _moreContext?.Units ?? WetUnits.e;
            double Get(List<double?>? list, int i) => list != null && i < list.Count ? list[i] ?? 0 : 0;
            double Rain(int i) => h.qpfRain != null ? Get(h.qpfRain, i) : i < h.qpf.Count ? h.qpf[i] : 0;
            double Snow(int i) => i < h.qpfSnow.Count ? h.qpfSnow[i] : 0;
            double Ice(int i) => Get(h.qpfIce, i);
            // Snow is in in (e) or cm, rain in in or mm. As water, 1 in of snow is 0.1 in, 1 cm is 1 mm.
            double SnowAsWater(int i) => units == WetUnits.e ? Snow(i) / 10 : Snow(i);

            const double sunBand = 6, top = 14, bottomText = 34;
            var plotHeight = height - top - bottomText;
            var n = _chartHours.Count;
            var slot = width / n;
            var barWidth = Math.Max(2, slot * 0.6);
            var minScale = units == WetUnits.e ? 0.1 : 2.5;
            var max = Math.Max(minScale, _chartHours.Max(i => Rain(i) + SnowAsWater(i) + Ice(i)));
            var scale = plotHeight / max;
            var text = new SolidColorBrush(Colors.Gray);

            // Base line.
            canvas.Children.Add(new Shapes.Line { X1 = 0, X2 = width, Y1 = top + plotHeight, Y2 = top + plotHeight, Stroke = text, StrokeThickness = 1, Opacity = 0.5 });

            var chance = new PointCollection();
            double totalRain = 0, totalSnow = 0, totalIce = 0;
            var labelEvery = Math.Max(1, (int) Math.Ceiling(44 / slot));
            for (var k = 0; k < n; k++)
            {
                var i = _chartHours[k];
                var x = k * slot + (slot - barWidth) / 2;
                var y = top + plotHeight;
                totalRain += Rain(i);
                totalSnow += Snow(i);
                totalIce += Ice(i);
                foreach (var (value, color) in new[] { (Rain(i), RainColor), (SnowAsWater(i), SnowColor), (Ice(i), IceColor) })
                {
                    if (value <= 0)
                        continue;
                    var barHeight = Math.Max(1, value * scale);
                    y -= barHeight;
                    var bar = new Shapes.Rectangle { Width = barWidth, Height = barHeight, Fill = new SolidColorBrush(color), RadiusX = 1, RadiusY = 1 };
                    Canvas.SetLeft(bar, x);
                    Canvas.SetTop(bar, y);
                    canvas.Children.Add(bar);
                }

                if (h.precipChance != null && i < h.precipChance.Count)
                    chance.Add(new Windows.Foundation.Point(k * slot + slot / 2, top + plotHeight * (1 - h.precipChance[i] / 100.0)));

                // Sunshine minutes of the hour, as a bar along the top.
                var key = h.validTimeLocal[i].DateTime.ToString("yyyy-MM-dd'T'HH:00", CultureInfo.InvariantCulture);
                if (_chartOpenMeteo != null && _chartOpenMeteo.SunshineByHour.TryGetValue(key, out var sunSeconds) && sunSeconds > 0)
                {
                    var sun = new Shapes.Rectangle
                    {
                        Width = Math.Max(1, slot - 2),
                        Height = sunBand * Math.Min(1, sunSeconds / 3600),
                        Fill = new SolidColorBrush(SunColor),
                        Opacity = 0.9
                    };
                    Canvas.SetLeft(sun, k * slot + 1);
                    Canvas.SetTop(sun, 0);
                    canvas.Children.Add(sun);
                }

                if (h.wxSeverity != null && i < h.wxSeverity.Count && h.wxSeverity[i] >= 3)
                {
                    var mark = new TextBlock { Text = "▲", FontSize = 10, Foreground = new SolidColorBrush(SevereColor) };
                    Canvas.SetLeft(mark, k * slot + slot / 2 - 4);
                    Canvas.SetTop(mark, top - 2);
                    canvas.Children.Add(mark);
                }

                if (k % labelEvery == 0)
                {
                    var label = new TextBlock
                    {
                        Text = h.validTimeLocal[i].DateTime.ToString(_chart12h ? "htt" : "HH", Ci).ToLower(Ci),
                        FontSize = 11,
                        Opacity = 0.75
                    };
                    Canvas.SetLeft(label, k * slot + 1);
                    Canvas.SetTop(label, top + plotHeight + 3);
                    canvas.Children.Add(label);
                }
            }

            if (chance.Count > 1)
                canvas.Children.Add(new Shapes.Polyline
                {
                    Points = chance,
                    Stroke = new SolidColorBrush(Colors.White),
                    StrokeThickness = 1.5,
                    StrokeDashArray = new DoubleCollection { 3, 2 },
                    Opacity = 0.8
                });

            var legend = new TextBlock { Text = "MoreChartLegend".GetLocalized(), FontSize = 11, Opacity = 0.7 };
            Canvas.SetLeft(legend, 0);
            Canvas.SetTop(legend, height - 15);
            canvas.Children.Add(legend);

            var amounts = MoreDataService.AmountsText(totalRain, totalSnow, totalIce, units);
            HoverTip.Set(HourlyChartHost, "MoreChartTip".GetLocalized() + "\n\n" +
                                          string.Format(Ci, "MoreChartTotal".GetLocalized(), amounts.Length > 0 ? amounts : "MoreChartNone".GetLocalized()));
        }

        // --- L: forecaster's discussion ---

        private async Task UpdateDiscussionAsync(MoreContext context, string placeId)
        {
            try
            {
                var us = string.Equals(context.CountryCode, "US", StringComparison.OrdinalIgnoreCase);
                if (!us || !await MoreDataService.IsOnAsync(MoreItem.Discussion, false))
                {
                    DiscussionSection.Visibility = Visibility.Collapsed;
                    return;
                }
                var discussion = await MoreDataService.GetDiscussionAsync(context.Latitude, context.Longitude);
                if (_displayedPlaceId != placeId)
                    return;
                if (discussion == null || discussion.Text.Length == 0)
                {
                    DiscussionSection.Visibility = Visibility.Collapsed;
                    return;
                }

                var issued = discussion.Issued.ToLocalTime();
                DiscussionTitleText.Text = string.Format(Ci, "MoreDiscussionTitle".GetLocalized(), discussion.Office);
                DiscussionMetaText.Text = string.Format(Ci, "MoreDiscussionIssued".GetLocalized(),
                    issued.ToString("ddd", Ci) + " " + await VariousUtils.GetTimeBasedOnUserSettings(issued.DateTime)) + " " +
                    "MoreDiscussionCredit".GetLocalized();
                DiscussionText.Text = discussion.Text;
                ApplyDiscussionExpanded();
                DiscussionSection.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "UpdateDiscussionAsync");
                DiscussionSection.Visibility = Visibility.Collapsed;
            }
        }

        private void ApplyDiscussionExpanded()
        {
            DiscussionText.MaxLines = _discussionExpanded ? 0 : 8;
            DiscussionToggleButton.Content = (_discussionExpanded ? "MoreDiscussionShowLess" : "MoreDiscussionShowAll").GetLocalized();
        }

        private void DiscussionToggleButton_OnClick(object sender, RoutedEventArgs e)
        {
            _discussionExpanded = !_discussionExpanded;
            ApplyDiscussionExpanded();
        }

        // --- Settings: the "More data" rows ---

        private void MoreDataGrid_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Grid grid || grid.Children.Count > 0)
                return;

            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            var row = 0;

            void AddRow() => grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            void Place(FrameworkElement element, int r, int column, int span = 1)
            {
                Grid.SetRow(element, r);
                Grid.SetColumn(element, column);
                Grid.SetColumnSpan(element, span);
                grid.Children.Add(element);
            }

            // The heading and the Main and Tray labels are in the Open-Meteo rows above, in the XAML.
            // These rows follow them directly, so both lists read as one section, "More data options".

            foreach (var item in MoreDataService.All)
            {
                AddRow();
                var label = new TextBlock
                {
                    Text = ("More" + item + "Label").GetLocalized(),
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.WrapWholeWords
                };
                ToolTipService.SetToolTip(label, ("CardInfoMore" + item).GetLocalized());
                Place(label, row, 0);
                foreach (var tray in new[] { false, true })
                {
                    if (tray ? !MoreDataService.HasTray(item) : !MoreDataService.HasMain(item))
                        continue;
                    var box = new CheckBox
                    {
                        Tag = item + (tray ? ".tray" : ".main"),
                        MinWidth = 0,
                        Padding = new Thickness(6, 0, 0, 0),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        IsChecked = MoreDataService.IsOnAsync(item, tray).Result
                    };
                    AutomationProperties.SetName(box, label.Text + " " + (tray ? "MoreTrayHeader" : "MoreMainHeader").GetLocalized());
                    box.Click += MoreCheckBox_Click;
                    _moreCheckBoxes.Add(box);
                    Place(box, row, tray ? 2 : 1);
                }
                row++;
            }

            // Which alerts the main window shows. The tray has its own choice in its menu.
            AddRow();
            var levelLabel = new TextBlock
            {
                Text = "MoreAlertLevelLabel".GetLocalized(),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 0)
            };
            Place(levelLabel, row, 0);
            var levels = new ComboBox { Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 0 };
            foreach (var key in new[] { "MoreAlertLevelAll", "MoreAlertLevelWatches", "MoreAlertLevelWarnings" })
                levels.Items.Add(key.GetLocalized());
            levels.SelectedIndex = 2 - Math.Max(0, Math.Min(2, MoreDataService.AlertLevel));
            AutomationProperties.SetName(levels, levelLabel.Text);
            levels.SelectionChanged += async (_, _) =>
            {
                MoreDataService.AlertLevel = 2 - Math.Max(0, levels.SelectedIndex);
                if (_moreContext != null && _displayedPlaceId != null)
                    await UpdateAlertsAsync(_moreContext.Latitude, _moreContext.Longitude, _moreContext.CountryCode, _displayedPlaceId);
            };
            Place(levels, row, 1, 2);
            row++;

            AddRow();
            Place(new TextBlock { Text = "MoreHint".GetLocalized(), Style = Resources["SettingsHintStyle"] as Style }, row, 0, 3);
        }

        private async void MoreCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox box || box.Tag is not string tag)
                return;
            var parts = tag.Split('.');
            if (parts.Length != 2 || !Enum.TryParse<MoreItem>(parts[0], out var item))
                return;
            var tray = parts[1] == "tray";
            await MoreDataService.SetAsync(item, tray, box.IsChecked == true);
            if (tray)
            {
                _ = TrayWeatherFile.RefreshCurrentAsync();
                _ = TrayWeatherFile.RefreshAllSavedAsync(true);
                return;
            }
            await RefreshMoreMainAsync(item);
        }

        /// <summary>Shows or hides the main window part of an item after its check box changed.</summary>
        private async Task RefreshMoreMainAsync(MoreItem item)
        {
            if (_lastApiData == null || _displayedPlaceId == null)
                return;
            if (item == MoreItem.Days)
            {
                await RebuildDayButtonsAsync();
                return;
            }
            if (item == MoreItem.Airport && !await MoreDataService.IsOnAsync(MoreItem.Airport, false))
                HoverTip.Set(VisibilityChipControl, _visibilityBaseTip);
            await UpdateMoreAsync(_lastApiData, _displayedPlaceId);
            await ApplyCardLayoutAsync();
        }

        private void RefreshMoreCheckBoxes()
        {
            foreach (var box in _moreCheckBoxes)
            {
                var parts = (box.Tag as string)?.Split('.');
                if (parts is { Length: 2 } && Enum.TryParse<MoreItem>(parts[0], out var item))
                    box.IsChecked = MoreDataService.IsOnAsync(item, parts[1] == "tray").Result;
            }
        }
    }
}
