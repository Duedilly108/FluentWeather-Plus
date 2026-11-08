using System.Text.Json.Serialization;

namespace FluentWeather.Models
{
    /// <summary>The current air quality from the Open-Meteo air quality API. All values can be null.</summary>
    public class AirQualityResponse
    {
        [JsonPropertyName("current")]
        public AirQualityCurrent? Current { get; set; }
    }

    public class AirQualityCurrent
    {
        [JsonPropertyName("us_aqi")]
        public double? UsAqi { get; set; }

        [JsonPropertyName("european_aqi")]
        public double? EuropeanAqi { get; set; }

        // Concentrations in micrograms per cubic meter
        [JsonPropertyName("pm2_5")]
        public double? Pm25 { get; set; }

        [JsonPropertyName("pm10")]
        public double? Pm10 { get; set; }

        [JsonPropertyName("ozone")]
        public double? Ozone { get; set; }

        [JsonPropertyName("nitrogen_dioxide")]
        public double? NitrogenDioxide { get; set; }
    }
}
