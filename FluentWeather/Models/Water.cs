using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FluentWeather.Models
{
    // --- USGS Water Data (api.waterdata.usgs.gov) ---

    /// <summary>A GeoJSON reply from the USGS OGC API. Only the fields the app uses.</summary>
    public class UsgsFeatureCollection
    {
        [JsonPropertyName("features")]
        public List<UsgsFeature>? Features { get; set; }
    }

    public class UsgsFeature
    {
        [JsonPropertyName("properties")]
        public UsgsProperties? Properties { get; set; }

        [JsonPropertyName("geometry")]
        public UsgsGeometry? Geometry { get; set; }
    }

    public class UsgsProperties
    {
        [JsonPropertyName("monitoring_location_id")]
        public string? MonitoringLocationId { get; set; }

        [JsonPropertyName("monitoring_location_name")]
        public string? MonitoringLocationName { get; set; }

        [JsonPropertyName("parameter_code")]
        public string? ParameterCode { get; set; }

        // ISO 8601 with offset
        [JsonPropertyName("time")]
        public string? Time { get; set; }

        [JsonPropertyName("value")]
        public string? Value { get; set; }

        [JsonPropertyName("unit_of_measure")]
        public string? UnitOfMeasure { get; set; }
    }

    public class UsgsGeometry
    {
        // [longitude, latitude]
        [JsonPropertyName("coordinates")]
        public List<double>? Coordinates { get; set; }
    }

    // --- The app's own stores and result ---

    /// <summary>The NDBC station names, kept in LocalFolder for a month.</summary>
    public class WaterStationStore
    {
        public long FetchedUtcTicks { get; set; }
        public List<WaterStation> Stations { get; set; } = [];
    }

    public class WaterStation
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public double Latitude { get; set; }
        public double Longitude { get; set; }
    }

    /// <summary>USGS station names, looked up once each and kept in LocalFolder.</summary>
    public class WaterStationNameStore
    {
        public List<WaterStation> Stations { get; set; } = [];
    }

    public enum WaterSource
    {
        NoaaNdbc,
        Usgs,
        OpenMeteoMarine
    }

    /// <summary>The nearest water station with a current reading. Values are metric.</summary>
    public class WaterReading
    {
        public WaterSource Source { get; set; }
        public string StationId { get; set; } = "";
        public string StationName { get; set; } = "";
        public double DistanceKm { get; set; }
        public double WaterTemperatureC { get; set; }
        public System.DateTimeOffset ObservedUtc { get; set; }

        // Only NDBC stations report these, and not all of them. Null otherwise.
        public double? AirTemperatureC { get; set; }
        public double? WindSpeedKmh { get; set; }
        public double? WindGustKmh { get; set; }
        public string? WindDirection { get; set; }
        public double? WaveHeightM { get; set; }
    }
}
