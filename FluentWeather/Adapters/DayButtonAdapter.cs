using FluentWeather.Models;
using FluentWeather.Utils;
using System;
using System.Globalization;
using Windows.UI.Xaml.Media.Imaging;

namespace FluentWeather.Adapters
{
    internal class DayButtonAdapter(V3WxForecastDaily fcst, int itemIndex)
    {
        public readonly V3WxForecastDaily CurrentObject = fcst;

        public readonly int ItemIndex = itemIndex;

        public string Temperature
        {
            get
            {
                // In the evening weather.com leaves today's daytime high empty, because only the night
                // part of the day is left. The high of the whole calendar day stays, so use that one.
                var maxTemp = CurrentObject.temperatureMax[ItemIndex]
                              ?? CurrentObject.calendarDayTemperatureMax?[ItemIndex];
                var minTemp = CurrentObject.temperatureMin[ItemIndex];


                return (maxTemp == null ? "--" : maxTemp) + "°" + " / " + minTemp + "°";
            }
        }

        public SvgImageSource SvgImageIcon
        {
            get
            {
                var iconCode = CurrentObject.daypart[0].iconCode[ItemIndex * 2];

                if (iconCode == null && ItemIndex == 0)
                {
                    //tonight's icon
                    iconCode = CurrentObject.daypart[0].iconCode[ItemIndex * 2 + 1];
                }
                else
                {
                    //days's icon
                    iconCode = CurrentObject.daypart[0].iconCode[ItemIndex * 2];
                }

                return new SvgImageSource
                {
                    UriSource = new Uri("ms-appx:///Assets/weticons/" + iconCode + ".svg")
                };
            }
        }

        private int? PrecipChancePercentage
        {
            get
            {
                var precipChance = CurrentObject.daypart[0].precipChance[ItemIndex * 2];

                //if precipchange is null and it is today
                if (precipChance == null && ItemIndex == 0)
                {
                    //return "--";

                    //return this evening's precip chance
                    return CurrentObject.daypart[0].precipChance[ItemIndex * 2 + 1];
                }

                return precipChance;
            }
        }

        public string PrecipitationChance
        {
            get
            {
                return PrecipChancePercentage + "%";
            }
        }

        public SvgImageSource SvgPrecipIcon
        {
            get
            {
                return new SvgImageSource
                {
                    UriSource = new Uri("ms-appx:///Assets/varicons/" + VariousUtils.GetPrecipIconChance(PrecipChancePercentage) + ".svg")
                };
            }
        }

        /// <summary>Local build: set by the main page. When true, the buttons show the thunder risk and snow range.</summary>
        public static bool ShowBadges;

        /// <summary>Local build: the unit setting, for the snow range unit.</summary>
        public static WetUnits Units = WetUnits.e;

        /// <summary>
        /// Local build: "❄ 1-3 in" when weather.com expects snow, and "⚡ 3/5" when the thunder risk
        /// of the day or night is 1 or more (weather.com thunder index, 0 to 5). Empty otherwise.
        /// </summary>
        public string Badge
        {
            get
            {
                if (!ShowBadges)
                    return "";
                try
                {
                    var part = CurrentObject.daypart[0];
                    var parts = new System.Collections.Generic.List<string>();

                    string? snow = null;
                    foreach (var i in new[] { ItemIndex * 2, ItemIndex * 2 + 1 })
                    {
                        var range = part.snowRange != null && i < part.snowRange.Count ? part.snowRange[i] : null;
                        if (!string.IsNullOrWhiteSpace(range))
                        {
                            snow = range.Trim();
                            break;
                        }
                    }
                    // Without a range, the forecast amount of the day and night, when there is some.
                    if (snow == null)
                    {
                        double amount = 0;
                        foreach (var i in new[] { ItemIndex * 2, ItemIndex * 2 + 1 })
                            if (part.qpfSnow != null && i < part.qpfSnow.Count && part.qpfSnow[i] != null)
                                amount += part.qpfSnow[i]!.Value;
                        if (amount >= 0.1)
                            snow = amount.ToString("0.#", CultureInfo.CurrentCulture);
                    }
                    if (snow != null)
                        parts.Add("\u2744 " + snow + " " + MeasureUnitUtils.GetSnowPrecipitationUnits(Units));

                    int? thunder = null;
                    foreach (var i in new[] { ItemIndex * 2, ItemIndex * 2 + 1 })
                    {
                        var value = part.thunderIndex != null && i < part.thunderIndex.Count ? part.thunderIndex[i] : null;
                        if (value != null)
                            thunder = Math.Max(thunder ?? 0, value.Value);
                    }
                    if (thunder is >= 1)
                        parts.Add("\u26A1 " + thunder + "/5");

                    return string.Join("  ", parts);
                }
                catch (Exception)
                {
                    return "";
                }
            }
        }

        public string ShortDayName
        {
            get
            {
                if (ItemIndex == 0)
                {
                    var dayText = CurrentObject.daypart[0].daypartName[ItemIndex * 2];

                    return (string.IsNullOrEmpty(dayText)
                        ? CurrentObject.daypart[0].daypartName[ItemIndex * 2 + 1]
                        : dayText);
                }
                else
                {
                    //get windows current culture\language
                    var language = Windows.System.UserProfile.GlobalizationPreferences.Languages[0];


                    var abbname = new CultureInfo(language).DateTimeFormat.GetAbbreviatedDayName(CurrentObject
                        .validTimeLocal[ItemIndex]
                        .DayOfWeek);

                    //VariousUtils.UppercaseFirst(CurrentObject.dayOfWeek[ItemIndex]);


                    //return short day name + day complete date
                    return VariousUtils.UppercaseFirst(abbname) + " " + CurrentObject.validTimeLocal[ItemIndex].Day;
                }
            }
        }
    }
}
