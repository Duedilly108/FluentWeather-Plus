using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FluentWeather.Models
{
    // Local build: the replies that BackupWeatherService reads. Units follow the request parameters
    // (temperature_unit, wind_speed_unit, precipitation_unit). Times are local ISO times of the
    // location, without an offset. utc_offset_seconds gives the offset.

    /// <summary>api.open-meteo.com/v1/forecast with current, hourly and daily parts.</summary>
    public class OmForecastResponse
    {
        [JsonPropertyName("latitude")]
        public double Latitude { get; set; }

        [JsonPropertyName("longitude")]
        public double Longitude { get; set; }

        [JsonPropertyName("utc_offset_seconds")]
        public int UtcOffsetSeconds { get; set; }

        [JsonPropertyName("timezone")]
        public string? Timezone { get; set; }

        [JsonPropertyName("current")]
        public OmCurrent? Current { get; set; }

        [JsonPropertyName("hourly_units")]
        public OmHourlyUnits? HourlyUnits { get; set; }

        [JsonPropertyName("hourly")]
        public OmHourly? Hourly { get; set; }

        [JsonPropertyName("daily")]
        public OmDaily? Daily { get; set; }
    }

    public class OmCurrent
    {
        [JsonPropertyName("time")]
        public string? Time { get; set; }

        [JsonPropertyName("temperature_2m")]
        public double? Temperature { get; set; }

        [JsonPropertyName("relative_humidity_2m")]
        public double? Humidity { get; set; }

        [JsonPropertyName("dew_point_2m")]
        public double? DewPoint { get; set; }

        [JsonPropertyName("apparent_temperature")]
        public double? FeelsLike { get; set; }

        [JsonPropertyName("is_day")]
        public int? IsDay { get; set; }

        [JsonPropertyName("weather_code")]
        public int? WeatherCode { get; set; }

        [JsonPropertyName("cloud_cover")]
        public double? CloudCover { get; set; }

        [JsonPropertyName("pressure_msl")]
        public double? PressureMsl { get; set; }

        [JsonPropertyName("wind_speed_10m")]
        public double? WindSpeed { get; set; }

        [JsonPropertyName("wind_direction_10m")]
        public double? WindDirection { get; set; }

        [JsonPropertyName("wind_gusts_10m")]
        public double? WindGusts { get; set; }
    }

    public class OmHourlyUnits
    {
        [JsonPropertyName("visibility")]
        public string? Visibility { get; set; }
    }

    public class OmHourly
    {
        [JsonPropertyName("time")]
        public List<string>? Time { get; set; }

        [JsonPropertyName("temperature_2m")]
        public List<double?>? Temperature { get; set; }

        [JsonPropertyName("relative_humidity_2m")]
        public List<double?>? Humidity { get; set; }

        [JsonPropertyName("dew_point_2m")]
        public List<double?>? DewPoint { get; set; }

        [JsonPropertyName("apparent_temperature")]
        public List<double?>? FeelsLike { get; set; }

        [JsonPropertyName("precipitation_probability")]
        public List<double?>? PrecipChance { get; set; }

        [JsonPropertyName("precipitation")]
        public List<double?>? Precipitation { get; set; }

        [JsonPropertyName("snowfall")]
        public List<double?>? Snowfall { get; set; }

        [JsonPropertyName("weather_code")]
        public List<int?>? WeatherCode { get; set; }

        [JsonPropertyName("pressure_msl")]
        public List<double?>? PressureMsl { get; set; }

        [JsonPropertyName("cloud_cover")]
        public List<double?>? CloudCover { get; set; }

        [JsonPropertyName("visibility")]
        public List<double?>? Visibility { get; set; }

        [JsonPropertyName("wind_speed_10m")]
        public List<double?>? WindSpeed { get; set; }

        [JsonPropertyName("wind_direction_10m")]
        public List<double?>? WindDirection { get; set; }

        [JsonPropertyName("wind_gusts_10m")]
        public List<double?>? WindGusts { get; set; }

        [JsonPropertyName("uv_index")]
        public List<double?>? UvIndex { get; set; }

        [JsonPropertyName("is_day")]
        public List<int?>? IsDay { get; set; }
    }

    public class OmDaily
    {
        [JsonPropertyName("time")]
        public List<string>? Time { get; set; }

        [JsonPropertyName("sunrise")]
        public List<string?>? Sunrise { get; set; }

        [JsonPropertyName("sunset")]
        public List<string?>? Sunset { get; set; }
    }

    /// <summary>geocoding-api.open-meteo.com/v1/search</summary>
    public class OmGeocodingResponse
    {
        [JsonPropertyName("results")]
        public List<OmPlace>? Results { get; set; }
    }

    public class OmPlace
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("latitude")]
        public double Latitude { get; set; }

        [JsonPropertyName("longitude")]
        public double Longitude { get; set; }

        [JsonPropertyName("country_code")]
        public string? CountryCode { get; set; }

        [JsonPropertyName("country")]
        public string? Country { get; set; }

        [JsonPropertyName("admin1")]
        public string? Admin1 { get; set; }

        [JsonPropertyName("admin2")]
        public string? Admin2 { get; set; }

        [JsonPropertyName("timezone")]
        public string? Timezone { get; set; }

        [JsonPropertyName("postcodes")]
        public List<string>? Postcodes { get; set; }
    }

    /// <summary>The places the app has seen, so the backup can name a weather.com place ID and find its coordinates.</summary>
    public class KnownLocationsStore
    {
        public List<LocationV3> Locations { get; set; } = [];
    }
}
