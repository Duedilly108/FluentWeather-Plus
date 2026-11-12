using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FluentWeather.Models
{
    // --- Replies of the Open-Meteo APIs that HistoryService uses. Values are metric:
    // Celsius, mm, cm, km/h, hPa, percent, seconds. A value is null when the source has no data. ---

    /// <summary>archive-api.open-meteo.com/v1/archive, daily part.</summary>
    public class ArchiveResponse
    {
        [JsonPropertyName("daily")]
        public ArchiveDaily? Daily { get; set; }
    }

    public class ArchiveDaily
    {
        [JsonPropertyName("time")]
        public List<string>? Time { get; set; }

        [JsonPropertyName("weather_code")]
        public List<int?>? WeatherCode { get; set; }

        [JsonPropertyName("temperature_2m_max")]
        public List<double?>? TemperatureMax { get; set; }

        [JsonPropertyName("temperature_2m_min")]
        public List<double?>? TemperatureMin { get; set; }

        [JsonPropertyName("precipitation_sum")]
        public List<double?>? PrecipitationSum { get; set; }

        [JsonPropertyName("snowfall_sum")]
        public List<double?>? SnowfallSum { get; set; }

        [JsonPropertyName("wind_speed_10m_max")]
        public List<double?>? WindSpeedMax { get; set; }

        [JsonPropertyName("wind_gusts_10m_max")]
        public List<double?>? WindGustsMax { get; set; }

        [JsonPropertyName("wind_direction_10m_dominant")]
        public List<double?>? WindDirection { get; set; }

        [JsonPropertyName("relative_humidity_2m_mean")]
        public List<double?>? HumidityMean { get; set; }

        [JsonPropertyName("dew_point_2m_mean")]
        public List<double?>? DewPointMean { get; set; }

        [JsonPropertyName("pressure_msl_mean")]
        public List<double?>? PressureMean { get; set; }

        [JsonPropertyName("cloud_cover_mean")]
        public List<double?>? CloudCoverMean { get; set; }

        [JsonPropertyName("sunshine_duration")]
        public List<double?>? SunshineSeconds { get; set; }
    }

    /// <summary>The hourly reply of the archive API, or of the forecast API for recent days.</summary>
    public class HourlyHistoryResponse
    {
        [JsonPropertyName("hourly")]
        public HourlyHistory? Hourly { get; set; }

        [JsonPropertyName("daily")]
        public HourlyHistoryDaily? Daily { get; set; }
    }

    public class HourlyHistory
    {
        [JsonPropertyName("time")]
        public List<string>? Time { get; set; }

        [JsonPropertyName("temperature_2m")]
        public List<double?>? Temperature { get; set; }

        [JsonPropertyName("relative_humidity_2m")]
        public List<double?>? Humidity { get; set; }

        [JsonPropertyName("dew_point_2m")]
        public List<double?>? DewPoint { get; set; }

        [JsonPropertyName("precipitation")]
        public List<double?>? Precipitation { get; set; }

        [JsonPropertyName("snowfall")]
        public List<double?>? Snowfall { get; set; }

        [JsonPropertyName("weather_code")]
        public List<int?>? WeatherCode { get; set; }

        [JsonPropertyName("pressure_msl")]
        public List<double?>? Pressure { get; set; }

        [JsonPropertyName("cloud_cover")]
        public List<double?>? CloudCover { get; set; }

        [JsonPropertyName("wind_speed_10m")]
        public List<double?>? WindSpeed { get; set; }

        [JsonPropertyName("wind_gusts_10m")]
        public List<double?>? WindGusts { get; set; }

        [JsonPropertyName("wind_direction_10m")]
        public List<double?>? WindDirection { get; set; }
    }

    public class HourlyHistoryDaily
    {
        [JsonPropertyName("time")]
        public List<string>? Time { get; set; }

        [JsonPropertyName("sunrise")]
        public List<string>? Sunrise { get; set; }

        [JsonPropertyName("sunset")]
        public List<string>? Sunset { get; set; }
    }

    /// <summary>air-quality-api.open-meteo.com, hourly. Data from about August 2022.</summary>
    public class AirQualityHistoryResponse
    {
        [JsonPropertyName("hourly")]
        public AirQualityHistoryHourly? Hourly { get; set; }
    }

    public class AirQualityHistoryHourly
    {
        [JsonPropertyName("time")]
        public List<string>? Time { get; set; }

        [JsonPropertyName("us_aqi")]
        public List<double?>? UsAqi { get; set; }

        [JsonPropertyName("european_aqi")]
        public List<double?>? EuropeanAqi { get; set; }

        [JsonPropertyName("pm2_5")]
        public List<double?>? Pm25 { get; set; }
    }

    // --- The app's own stores in LocalFolder ---

    /// <summary>
    /// One month of archive days for one location: history-{PlaceId}-{yyyy-MM}.json. Metric values.
    /// Version 2 added the gust, direction, humidity, dew point, pressure, cloud and sun fields.
    /// A store of an older version is downloaded again.
    /// </summary>
    public class HistoryMonthStore
    {
        public const int CurrentVersion = 2;

        public int Version { get; set; }
        public string PlaceId { get; set; } = "";

        // yyyy-MM
        public string Month { get; set; } = "";

        public long FetchedUtcTicks { get; set; }

        // True when every day of the month is present. A complete month is never downloaded again.
        public bool Complete { get; set; }

        public List<HistoryArchiveDay> Days { get; set; } = [];
    }

    public class HistoryArchiveDay
    {
        // yyyy-MM-dd
        public string Date { get; set; } = "";
        public double? High { get; set; }
        public double? Low { get; set; }
        public double? Precip { get; set; }
        public double? Snow { get; set; }
        public double? WindMax { get; set; }
        public double? GustMax { get; set; }
        public double? WindDirection { get; set; }
        public double? Humidity { get; set; }
        public double? DewPoint { get; set; }
        public double? Pressure { get; set; }
        public double? CloudCover { get; set; }
        public double? SunshineSeconds { get; set; }
        public int? WeatherCode { get; set; }
    }

    /// <summary>One month of daily air quality for one location: historyaq-{PlaceId}-{yyyy-MM}.json.</summary>
    public class HistoryAirMonthStore
    {
        public long FetchedUtcTicks { get; set; }
        public bool Complete { get; set; }
        public List<HistoryAirDay> Days { get; set; } = [];
    }

    public class HistoryAirDay
    {
        public string Date { get; set; } = "";
        public double? UsAqiMax { get; set; }
        public double? EuropeanAqiMax { get; set; }
        public double? Pm25Mean { get; set; }
    }

    /// <summary>The hours of one month for one location: historyhours-{PlaceId}-{yyyy-MM}.json.</summary>
    public class HistoryHoursMonthStore
    {
        public const int CurrentVersion = 1;

        public int Version { get; set; }
        public long FetchedUtcTicks { get; set; }

        // True when every hour of the month is present. A complete month is never downloaded again.
        public bool Complete { get; set; }

        public List<HistorySunDay> Days { get; set; } = [];
        public List<HistoryArchiveHour> Hours { get; set; } = [];
    }

    public class HistorySunDay
    {
        // yyyy-MM-dd, and the times as yyyy-MM-ddTHH:mm in the local time of the location
        public string Date { get; set; } = "";
        public string? Sunrise { get; set; }
        public string? Sunset { get; set; }
    }

    public class HistoryArchiveHour
    {
        // yyyy-MM-ddTHH:mm, local time of the location
        public string Time { get; set; } = "";
        public double? Temperature { get; set; }
        public double? Humidity { get; set; }
        public double? DewPoint { get; set; }
        public double? Precip { get; set; }
        public double? Snow { get; set; }
        public double? Pressure { get; set; }
        public double? CloudCover { get; set; }
        public double? Wind { get; set; }
        public double? Gust { get; set; }
        public double? WindDirection { get; set; }
        public int? WeatherCode { get; set; }
        public double? UsAqi { get; set; }
        public double? EuropeanAqi { get; set; }
        public double? Pm25 { get; set; }
    }

    /// <summary>Where a history day came from.</summary>
    public enum HistorySource
    {
        Recorded,
        Archive
    }

    /// <summary>One day in the history page, in the units the page shows.</summary>
    public class HistoryDay
    {
        public string Date { get; set; } = "";
        public int? High { get; set; }
        public int? Low { get; set; }
        public double? Precip { get; set; }
        public double? Snow { get; set; }
        public int? WindMax { get; set; }
        public int? GustMax { get; set; }
        public double? WindDirection { get; set; }
        public int? Humidity { get; set; }
        public int? DewPoint { get; set; }
        public double? Pressure { get; set; }
        public int? CloudCover { get; set; }
        public double? SunHours { get; set; }
        public int? UsAqi { get; set; }
        public int? EuropeanAqi { get; set; }
        public double? Pm25 { get; set; }
        public int? IconCode { get; set; }
        public string? Phrase { get; set; }
        public HistorySource Source { get; set; }

        public bool HasTemperature => High != null || Low != null;
    }

    /// <summary>One hour in the hourly view of the history page, in the units the page shows.</summary>
    public class HistoryHour
    {
        public DateTime Time { get; set; }
        public int? Temperature { get; set; }
        public int? DewPoint { get; set; }
        public int? Humidity { get; set; }
        public double? Precip { get; set; }
        public double? Snow { get; set; }
        public double? Pressure { get; set; }
        public int? CloudCover { get; set; }
        public int? Wind { get; set; }
        public int? Gust { get; set; }
        public double? WindDirection { get; set; }
        public int? UsAqi { get; set; }
        public int? EuropeanAqi { get; set; }
        public double? Pm25 { get; set; }
        public string? Phrase { get; set; }
        public bool IsNight { get; set; }
    }
}
