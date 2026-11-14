using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FluentWeather.Models
{
    /// <summary>
    /// Small response with the current conditions, the place name and the moon times of the next days.
    /// Used to keep the tray icons up to date for every saved location.
    /// </summary>
    public class CurrentConditionsResponse
    {
        [JsonPropertyName("v3-wx-observations-current")]
        public V3WxObservationsCurrent? v3wxobservationscurrent { get; set; }

        [JsonPropertyName("v3-location-point")]
        public V3LocationPoint? v3locationpoint { get; set; }

        [JsonPropertyName("v3-wx-forecast-daily-3day")]
        public MoonTimes? v3wxforecastdaily3day { get; set; }

        // Local build: the pollen forecast, for the Pollen card and tray line. Null where weather.com
        // has no pollen data.
        [JsonPropertyName("v2idxPollenDaypart10")]
        public WeatherComPollenResult? pollen { get; set; }
    }

    /// <summary>
    /// The weather.com pollen forecast in 12-hour parts (today, tonight, tomorrow and so on). Every
    /// field can be missing, so a location without pollen data never breaks the request.
    /// Indexes run from 0 (none) to 4 (very high).
    /// </summary>
    public class WeatherComPollenResult
    {
        [JsonPropertyName("pollenForecast12hour")]
        public WeatherComPollen? Forecast { get; set; }
    }

    public class WeatherComPollen
    {
        public List<string?>? daypartName { get; set; }
        public List<int?>? treePollenIndex { get; set; }
        public List<string?>? treePollenCategory { get; set; }
        public List<int?>? grassPollenIndex { get; set; }
        public List<string?>? grassPollenCategory { get; set; }
        public List<int?>? ragweedPollenIndex { get; set; }
        public List<string?>? ragweedPollenCategory { get; set; }
    }

    /// <summary>
    /// The parts of the 3-day forecast that the small request needs: the moonrise and moonset times,
    /// and the day and night conditions for the "Past days" record. All lists can hold nulls.
    /// </summary>
    public class MoonTimes
    {
        public List<DateTimeOffset?>? moonriseTimeLocal { get; set; }
        public List<DateTimeOffset?>? moonsetTimeLocal { get; set; }
        public List<DaypartConditions>? daypart { get; set; }
    }

    /// <summary>Day and night conditions of the forecast: entry 0 is today's day part, entry 1 tonight.</summary>
    public class DaypartConditions
    {
        public List<int?>? iconCode { get; set; }
        public List<string?>? wxPhraseLong { get; set; }
        public List<int?>? precipChance { get; set; }

        // Local build: the thunder risk (0 to 5) for "Thunder risk today" in the tray.
        public List<int?>? thunderIndex { get; set; }
        public List<string?>? thunderCategory { get; set; }
    }
}
