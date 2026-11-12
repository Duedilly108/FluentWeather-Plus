using FluentWeather.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace FluentWeather.Services
{
    /// <summary>
    /// Local build: airport weather reports (METAR) from the Aviation Weather Center of the US
    /// National Weather Service (aviationweather.gov). The service is free, needs no key and covers
    /// airports worldwide. Its rules allow 100 requests per minute and ask for a user agent that
    /// names the program. The app asks once per location every 30 minutes.
    ///
    /// The Open-Meteo backup uses the nearest report for values that a model grid does not give:
    /// the cloud ceiling, the station name and code, and the measured visibility and pressure.
    /// Within MeasuredKm it also uses the measured temperature, dew point, wind and gusts.
    /// Data from weather.com does not use these reports.
    /// </summary>
    public static class MetarService
    {
        /// <summary>Reports from stations farther away are not used.</summary>
        public const double SearchKm = 50;

        /// <summary>Temperature, dew point, wind and gusts come from stations within this distance.</summary>
        public const double MeasuredKm = 25;

        // Reports older than this are not used. Most stations report every hour.
        private static readonly TimeSpan MaxAge = TimeSpan.FromHours(2);
        private static readonly TimeSpan CacheTime = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan EmptyCacheTime = TimeSpan.FromMinutes(5);

        private static readonly HttpClient Http = CreateClient();
        private static readonly ConcurrentDictionary<string, (DateTime FetchedAt, MetarReport? Report)> Cache = new();

        private static HttpClient CreateClient()
        {
            var client = ApiUtils.CreateClient("https://aviationweather.gov/");
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "FluentWeather-Plus (https://github.com/Duedilly108/FluentWeather-Plus)");
            return client;
        }

        private static string Key(double latitude, double longitude) =>
            latitude.ToString("0.00", CultureInfo.InvariantCulture) + "," + longitude.ToString("0.00", CultureInfo.InvariantCulture);

        /// <summary>The nearest station within SearchKm with a report from the last 2 hours, or null.</summary>
        public static async Task<MetarReport?> NearestAsync(double latitude, double longitude)
        {
            var key = Key(latitude, longitude);
            if (Cache.TryGetValue(key, out var hit) &&
                DateTime.UtcNow - hit.FetchedAt < (hit.Report == null ? EmptyCacheTime : CacheTime))
                return hit.Report;

            MetarReport? report = null;
            try
            {
                using var response = await ApiUtils.GetWithRetryAsync(Http, BoxQuery(latitude, longitude));
                if (response.IsSuccessStatusCode)
                {
                    // The reply is empty (status 204) when no station is in the box.
                    var json = await response.Content.ReadAsStringAsync();
                    report = Parse(json, latitude, longitude, DateTimeOffset.UtcNow).FirstOrDefault();
                }
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "MetarService");
            }

            Cache[key] = (DateTime.UtcNow, report);
            return report;
        }

        /// <summary>The last report found for a location, without a new request. For the data note.</summary>
        public static MetarReport? Cached(double latitude, double longitude) =>
            Cache.TryGetValue(Key(latitude, longitude), out var hit) ? hit.Report : null;

        /// <summary>The request for all latest reports in a box of SearchKm around a point.</summary>
        public static string BoxQuery(double latitude, double longitude)
        {
            var ci = CultureInfo.InvariantCulture;
            var dLat = SearchKm / 111.0;
            var dLon = SearchKm / (111.0 * Math.Max(0.1, Math.Cos(latitude * Math.PI / 180)));
            string F(double v) => v.ToString("0.000", ci);
            return "api/data/metar?bbox=" +
                   F(Math.Max(-90, latitude - dLat)) + "," + F(Math.Max(-180, longitude - dLon)) + "," +
                   F(Math.Min(90, latitude + dLat)) + "," + F(Math.Min(180, longitude + dLon)) +
                   "&format=json";
        }

        /// <summary>
        /// The reports of a reply that are recent and within SearchKm, nearest first.
        /// Separate from the download, so it can be tested.
        /// </summary>
        public static List<MetarReport> Parse(string json, double latitude, double longitude, DateTimeOffset now)
        {
            var list = new List<MetarReport>();
            if (string.IsNullOrWhiteSpace(json))
                return list;

            // JsonDocument needs no generated types, so it works with the trimmed (AOT) build.
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var id = Text(e, "icaoId");
                var lat = Number(e, "lat");
                var lon = Number(e, "lon");
                var obsTime = Number(e, "obsTime");
                if (string.IsNullOrEmpty(id) || lat == null || lon == null || obsTime == null)
                    continue;

                var time = DateTimeOffset.FromUnixTimeSeconds((long) obsTime.Value);
                if (now - time > MaxAge)
                    continue;

                var km = DistanceKm(latitude, longitude, lat.Value, lon.Value);
                if (km > SearchKm)
                    continue;

                // The ceiling is the lowest layer that covers more than half of the sky: broken (BKN),
                // overcast (OVC) or a sky hidden by fog (OVX, reported as vertical visibility).
                int? ceiling = null;
                if (e.TryGetProperty("clouds", out var clouds) && clouds.ValueKind == JsonValueKind.Array)
                {
                    foreach (var layer in clouds.EnumerateArray())
                    {
                        var layerCover = Text(layer, "cover");
                        var layerBase = Number(layer, "base");
                        if (layerCover is "BKN" or "OVC" or "OVX" && layerBase != null)
                            ceiling = Math.Min(ceiling ?? int.MaxValue, (int) layerBase.Value);
                    }
                }

                // A report without a sky group, or with a ceiling layer of unknown height, does not tell the ceiling.
                var cover = Text(e, "cover");
                var skyKnown = cover != null && (ceiling != null || cover is not ("BKN" or "OVC" or "OVX"));

                int? windDegrees = null;
                if (e.TryGetProperty("wdir", out var wdir) && wdir.ValueKind == JsonValueKind.Number)
                    windDegrees = (int) Math.Round(wdir.GetDouble());

                list.Add(new MetarReport
                {
                    Id = id!,
                    Name = Text(e, "name") ?? id!,
                    Latitude = lat.Value,
                    Longitude = lon.Value,
                    DistanceKm = km,
                    Time = time,
                    TemperatureC = Number(e, "temp"),
                    DewPointC = Number(e, "dewp"),
                    WindKnots = Number(e, "wspd"),
                    WindDegrees = windDegrees,
                    GustKnots = Number(e, "wgst"),
                    VisibilityMiles = Miles(e),
                    AltimeterHpa = Number(e, "altim"),
                    SeaLevelHpa = Number(e, "slp"),
                    TendencyHpa = Number(e, "presTend"),
                    CeilingFeet = ceiling,
                    SkyKnown = skyKnown
                });
            }

            return list.OrderBy(r => r.DistanceKm).ToList();
        }

        private static string? Text(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static double? Number(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

        // Visibility in statute miles. It is a number, or text such as "10+" (US, 10 miles or more)
        // or "6+" (other countries, 10 km or more).
        private static double? Miles(JsonElement e)
        {
            if (!e.TryGetProperty("visib", out var v))
                return null;
            if (v.ValueKind == JsonValueKind.Number)
                return v.GetDouble();
            if (v.ValueKind != JsonValueKind.String)
                return null;

            var text = v.GetString()?.Trim() ?? "";
            if (text == "6+")
                return 10 / 1.609344;
            text = text.TrimEnd('+').Trim();

            // A fraction such as "1/2" or "1 1/2".
            double whole = 0;
            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out whole))
                text = parts[1];
            var slash = text.IndexOf('/');
            if (slash > 0 &&
                double.TryParse(text[..slash], NumberStyles.Float, CultureInfo.InvariantCulture, out var top) &&
                double.TryParse(text[(slash + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var bottom) && bottom > 0)
                return whole + top / bottom;
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var miles) ? whole + miles : null;
        }

        private static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
        {
            const double r = 6371.0;
            var dLat = (lat2 - lat1) * Math.PI / 180;
            var dLon = (lon2 - lon1) * Math.PI / 180;
            var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                    Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            return 2 * r * Math.Asin(Math.Min(1, Math.Sqrt(a)));
        }
    }

    /// <summary>One airport weather report. Values the station did not send are null.</summary>
    public sealed class MetarReport
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public double Latitude { get; init; }
        public double Longitude { get; init; }
        public double DistanceKm { get; init; }
        public DateTimeOffset Time { get; init; }
        public double? TemperatureC { get; init; }
        public double? DewPointC { get; init; }
        public double? WindKnots { get; init; }

        /// <summary>Null when the wind direction is variable (VRB) or missing.</summary>
        public int? WindDegrees { get; init; }

        public double? GustKnots { get; init; }
        public double? VisibilityMiles { get; init; }
        public double? AltimeterHpa { get; init; }
        public double? SeaLevelHpa { get; init; }

        /// <summary>The pressure change of the last 3 hours in hPa, sent by some stations.</summary>
        public double? TendencyHpa { get; init; }

        /// <summary>The height of the lowest broken or overcast layer in feet. Null when there is none.</summary>
        public int? CeilingFeet { get; init; }

        /// <summary>False when the report does not tell the ceiling.</summary>
        public bool SkyKnown { get; init; }

        public bool IsNear => DistanceKm <= MetarService.MeasuredKm;
    }
}
