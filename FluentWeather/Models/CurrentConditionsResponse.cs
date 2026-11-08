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
    }
}
