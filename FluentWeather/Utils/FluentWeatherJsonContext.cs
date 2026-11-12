using FluentWeather.Converters;
using FluentWeather.Models;
using System.Text.Json.Serialization;

[JsonSerializable(typeof(RootV3Response))]
[JsonSerializable(typeof(SearchLocationResponse))]
[JsonSerializable(typeof(System.Collections.Generic.List<SavedLocation>))]
[JsonSerializable(typeof(CurrentConditionsResponse))]
[JsonSerializable(typeof(PastDaysStore))]
[JsonSerializable(typeof(AirQualityResponse))]
[JsonSerializable(typeof(ArchiveResponse))]
[JsonSerializable(typeof(HistoryMonthStore))]
[JsonSerializable(typeof(OmForecastResponse))]
[JsonSerializable(typeof(OmGeocodingResponse))]
[JsonSerializable(typeof(KnownLocationsStore))]
[JsonSerializable(typeof(V3LocationPoint))]
[JsonSerializable(typeof(HourlyHistoryResponse))]
[JsonSerializable(typeof(AirQualityHistoryResponse))]
[JsonSerializable(typeof(HistoryAirMonthStore))]
[JsonSerializable(typeof(HistoryHoursMonthStore))]
[JsonSerializable(typeof(UsgsFeatureCollection))]
[JsonSerializable(typeof(MarineResponse))]
[JsonSerializable(typeof(WindDetailResponse))]
[JsonSerializable(typeof(ModelsResponse))]
[JsonSerializable(typeof(FloodResponse))]
[JsonSerializable(typeof(PollenResponse))]
[JsonSerializable(typeof(UsgsFeature))]
[JsonSerializable(typeof(WaterStationStore))]
[JsonSerializable(typeof(WaterStationNameStore))]
[JsonSerializable(typeof(bool?))]
// Some weather.com fields have the type object, for example locale3 and locale4 of a place.
// They read as JsonElement. Writing them back (place list of the backup, backup bundles) needs this type.
[JsonSerializable(typeof(System.Text.Json.JsonElement))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    Converters = new[] { typeof(CustomDateTimeOffsetConverter) }
)]
public partial class FluentWeatherJsonContext : JsonSerializerContext
{
}
