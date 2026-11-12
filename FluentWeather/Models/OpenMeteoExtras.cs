using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FluentWeather.Models
{
    // Replies of the Open-Meteo APIs that OpenMeteoExtrasService uses. Only the fields the app reads.
    // Values are metric: Celsius, km/h, m, m3/s, grains/m3. A value is null when the point has no data,
    // for example a marine value on land, or pollen outside Europe.

    // --- marine-api.open-meteo.com/v1/marine ---
    public class MarineResponse
    {
        [JsonPropertyName("current")]
        public MarineCurrent? Current { get; set; }
    }

    public class MarineCurrent
    {
        [JsonPropertyName("time")]
        public string? Time { get; set; }

        [JsonPropertyName("wave_height")]
        public double? WaveHeight { get; set; }

        [JsonPropertyName("wave_direction")]
        public double? WaveDirection { get; set; }

        [JsonPropertyName("wave_period")]
        public double? WavePeriod { get; set; }

        [JsonPropertyName("swell_wave_height")]
        public double? SwellHeight { get; set; }

        [JsonPropertyName("swell_wave_period")]
        public double? SwellPeriod { get; set; }

        [JsonPropertyName("swell_wave_direction")]
        public double? SwellDirection { get; set; }

        [JsonPropertyName("sea_surface_temperature")]
        public double? SeaSurfaceTemperature { get; set; }
    }

    // --- api.open-meteo.com/v1/forecast, wind detail ---
    public class WindDetailResponse
    {
        [JsonPropertyName("current")]
        public WindDetailCurrent? Current { get; set; }

        [JsonPropertyName("hourly")]
        public WindDetailHourly? Hourly { get; set; }
    }

    public class WindDetailCurrent
    {
        [JsonPropertyName("wind_speed_10m")]
        public double? WindSpeed { get; set; }

        [JsonPropertyName("wind_gusts_10m")]
        public double? WindGusts { get; set; }

        [JsonPropertyName("wind_direction_10m")]
        public double? WindDirection { get; set; }
    }

    public class WindDetailHourly
    {
        [JsonPropertyName("wind_speed_80m")]
        public List<double?>? WindSpeed80m { get; set; }

        [JsonPropertyName("wind_gusts_10m")]
        public List<double?>? WindGusts { get; set; }
    }

    // --- api.open-meteo.com/v1/forecast, other models. The API adds the model name to each field. ---
    public class ModelsResponse
    {
        [JsonPropertyName("daily")]
        public ModelsDaily? Daily { get; set; }
    }

    public class ModelsDaily
    {
        [JsonPropertyName("temperature_2m_max_ecmwf_ifs025")]
        public List<double?>? EcmwfMax { get; set; }

        [JsonPropertyName("temperature_2m_min_ecmwf_ifs025")]
        public List<double?>? EcmwfMin { get; set; }

        [JsonPropertyName("precipitation_sum_ecmwf_ifs025")]
        public List<double?>? EcmwfPrecip { get; set; }

        [JsonPropertyName("temperature_2m_max_gfs_seamless")]
        public List<double?>? GfsMax { get; set; }

        [JsonPropertyName("temperature_2m_min_gfs_seamless")]
        public List<double?>? GfsMin { get; set; }

        [JsonPropertyName("precipitation_sum_gfs_seamless")]
        public List<double?>? GfsPrecip { get; set; }

        [JsonPropertyName("temperature_2m_max_icon_seamless")]
        public List<double?>? IconMax { get; set; }

        [JsonPropertyName("temperature_2m_min_icon_seamless")]
        public List<double?>? IconMin { get; set; }

        [JsonPropertyName("precipitation_sum_icon_seamless")]
        public List<double?>? IconPrecip { get; set; }
    }

    // --- flood-api.open-meteo.com/v1/flood ---
    public class FloodResponse
    {
        [JsonPropertyName("daily")]
        public FloodDaily? Daily { get; set; }
    }

    public class FloodDaily
    {
        [JsonPropertyName("river_discharge")]
        public List<double?>? Discharge { get; set; }

        [JsonPropertyName("river_discharge_median")]
        public List<double?>? Median { get; set; }
    }

    // --- air-quality-api.open-meteo.com/v1/air-quality, pollen (Europe only) ---
    public class PollenResponse
    {
        [JsonPropertyName("current")]
        public PollenCurrent? Current { get; set; }
    }

    public class PollenCurrent
    {
        [JsonPropertyName("alder_pollen")]
        public double? Alder { get; set; }

        [JsonPropertyName("birch_pollen")]
        public double? Birch { get; set; }

        [JsonPropertyName("grass_pollen")]
        public double? Grass { get; set; }

        [JsonPropertyName("mugwort_pollen")]
        public double? Mugwort { get; set; }

        [JsonPropertyName("olive_pollen")]
        public double? Olive { get; set; }

        [JsonPropertyName("ragweed_pollen")]
        public double? Ragweed { get; set; }
    }

    // --- The app's own result ---

    public class MarineData
    {
        public double? WaveHeightM { get; set; }
        public double? WavePeriodS { get; set; }
        public double? WaveDirection { get; set; }
        public double? SwellHeightM { get; set; }
        public double? SwellPeriodS { get; set; }
        public double? SwellDirection { get; set; }
        public double? SeaTemperatureC { get; set; }

        public bool HasWaves => WaveHeightM != null;
    }

    public class WindDetailData
    {
        public double? SpeedKmh { get; set; }
        public double? GustKmh { get; set; }
        public double? Direction { get; set; }
        public double? Speed80mKmh { get; set; }

        // The highest gust of the next 6 hours.
        public double? MaxGustNext6hKmh { get; set; }
    }

    public class ModelDay
    {
        public string Name { get; set; } = "";
        public double? MaxC { get; set; }
        public double? MinC { get; set; }
        public double? PrecipMm { get; set; }
    }

    public class RiverData
    {
        public double DischargeM3s { get; set; }
        public double? MedianM3s { get; set; }
    }

    public class PollenData
    {
        // The pollen type with the highest count, its count in grains/m3, and all counts above 0.
        public string Highest { get; set; } = "";
        public double HighestCount { get; set; }
        public List<(string Name, double Count)> All { get; set; } = [];
    }
}
