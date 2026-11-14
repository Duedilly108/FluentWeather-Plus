using FluentWeather.Helpers;
using FluentWeather.Models;
using FluentWeather.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage;

namespace FluentWeather.Services
{
    /// <summary>Local build: the "More data" items. Each has a Main and a Tray check box in the settings.</summary>
    public enum MoreItem
    {
        Alerts,     // severe weather alerts: the band in the main window, the tray bubble
        Airport,    // A: weather now and flight category from the nearest airport report, airport forecast
        Snow,       // B: snow of the last 1, 6 and 24 hours
        Heat,       // C: heat stress (wet bulb globe temperature)
        Storm,      // D: storm energy (CAPE) and thunder risk
        Ground,     // E: soil temperature, soil moisture and snow depth
        Freezing,   // F: freezing level
        Sun,        // G: sunshine, daylight, solar radiation and evaporation
        Buoy,       // H: waves, air, pressure and tide at the nearest NOAA buoy or tide station
        Gauge,      // I: river height and flow at the nearest USGS gauge
        Days,       // J: thunder risk and snow range on the day buttons, more day details
        Hourly,     // K: the hourly chart (main), rain, snow and ice of the next 12 hours (tray)
        Discussion  // L: the forecaster's discussion of the National Weather Service (US)
    }

    /// <summary>Local build: the text of one item. Value and Note go on the card, Tip on hover.</summary>
    public sealed class MoreText
    {
        public string Value = "";
        public string Note = "";
        public string Tip = "";
        public string TrayLine = "";
    }

    /// <summary>Local build: what the app already has for a location when it asks for the items.</summary>
    public sealed class MoreContext
    {
        public double Latitude;
        public double Longitude;
        public WetUnits Units;
        public string? CountryCode;
        public V3WxObservationsCurrent? Observation;
        public int? ThunderIndex;
        public string? ThunderCategory;
        public V3WxForecastHourly? Hourly;
    }

    /// <summary>
    /// Local build: data that the sources send or offer but the app did not show before v49.
    ///   weather.com: snow of the last 1 and 6 hours, the wet bulb globe temperature, the thunder risk,
    ///     rain, snow and ice amounts, wind text and forecast notes. These come with the normal request.
    ///   Open-Meteo (open-meteo.com, CC BY 4.0): storm energy (CAPE), soil temperature and moisture,
    ///     snow depth, freezing level, sunshine, daylight, solar radiation and evaporation.
    ///   Aviation Weather Center: weather codes, flight category and the airport forecast (TAF).
    ///   NOAA NDBC: waves, wave period and direction, air temperature, pressure and tide.
    ///   USGS: river height and flow, with the last 7 days.
    ///   National Weather Service: the forecaster's discussion (US only).
    /// Each source is asked only when an item that needs it is ticked, and at most every 30 minutes.
    /// </summary>
    public static class MoreDataService
    {
        public static readonly MoreItem[] All = Enum.GetValues<MoreItem>();

        private static readonly TimeSpan CacheTime = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan EmptyCacheTime = TimeSpan.FromMinutes(5);
        private static readonly ConcurrentDictionary<string, (DateTime FetchedUtc, object? Data)> Cache = new();

        private static readonly HttpClient OpenMeteo = ApiUtils.CreateClient("https://api.open-meteo.com/");
        private static readonly HttpClient Usgs = ApiUtils.CreateClient("https://api.waterdata.usgs.gov/");
        private static readonly HttpClient Nws = CreateNwsClient();

        private static HttpClient CreateNwsClient()
        {
            var client = ApiUtils.CreateClient("https://api.weather.gov/");
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "FluentWeather-Plus (https://github.com/Duedilly108/FluentWeather-Plus)");
            return client;
        }

        // --- Settings: "more.Alerts.main", "more.Alerts.tray" and so on ---

        /// <summary>Whether the item has a part in the main window.</summary>
        public static bool HasMain(MoreItem item) => true;

        /// <summary>Whether the item has a tray line. Day details and the discussion are too long for the tray.</summary>
        public static bool HasTray(MoreItem item) => item is not (MoreItem.Days or MoreItem.Discussion);

        /// <summary>Whether the item is a card in the detail row.</summary>
        public static bool IsCard(MoreItem item) =>
            item is MoreItem.Snow or MoreItem.Heat or MoreItem.Storm or MoreItem.Ground or MoreItem.Freezing
                or MoreItem.Sun or MoreItem.Buoy or MoreItem.Gauge;

        public static readonly MoreItem[] Cards = All.Where(IsCard).ToArray();

        public static string SettingKey(MoreItem item, bool tray) => "more." + item + (tray ? ".tray" : ".main");

        /// <summary>Alerts are on by default. Every other item is off by default.</summary>
        public static Task<bool> IsOnAsync(MoreItem item, bool tray)
        {
            if ((tray && !HasTray(item)) || (!tray && !HasMain(item)))
                return Task.FromResult(false);
            try
            {
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(SettingKey(item, tray), out var stored) && stored is bool on)
                    return Task.FromResult(on);
            }
            catch (Exception)
            {
                // The default applies.
            }
            return Task.FromResult(item == MoreItem.Alerts);
        }

        public static Task SetAsync(MoreItem item, bool tray, bool on)
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[SettingKey(item, tray)] = on;
            }
            catch (Exception)
            {
                // The setting stays as it was.
            }
            return Task.CompletedTask;
        }

        /// <summary>Which alerts the main window band shows: 0 warnings, 1 warnings and watches, 2 all. Default all.</summary>
        public static int AlertLevel
        {
            get
            {
                try
                {
                    return ApplicationData.Current.LocalSettings.Values.TryGetValue("more.AlertLevel", out var v) && v is int level ? level : 2;
                }
                catch (Exception)
                {
                    return 2;
                }
            }
            set
            {
                try
                {
                    ApplicationData.Current.LocalSettings.Values["more.AlertLevel"] = value;
                }
                catch (Exception)
                {
                    // The setting stays as it was.
                }
            }
        }

        // --- Cache ---

        private static string Place(double lat, double lon) =>
            lat.ToString("0.00", CultureInfo.InvariantCulture) + "," + lon.ToString("0.00", CultureInfo.InvariantCulture);

        private static async Task<T?> CachedAsync<T>(string name, double lat, double lon, Func<Task<T?>> fetch) where T : class
        {
            var key = name + "|" + Place(lat, lon);
            if (Cache.TryGetValue(key, out var hit) &&
                DateTime.UtcNow - hit.FetchedUtc < (hit.Data == null ? EmptyCacheTime : CacheTime))
                return hit.Data as T;

            T? data = null;
            try
            {
                data = await fetch();
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "MoreDataService " + name);
            }
            Cache[key] = (DateTime.UtcNow, data);
            return data;
        }

        private static async Task<string?> GetTextAsync(HttpClient client, string url)
        {
            using var response = await ApiUtils.GetWithRetryAsync(client, url);
            if (!response.IsSuccessStatusCode)
                return null;
            return await response.Content.ReadAsStringAsync();
        }

        private static string Point(double lat, double lon)
        {
            var ci = CultureInfo.InvariantCulture;
            return "latitude=" + lat.ToString("0.0000", ci) + "&longitude=" + lon.ToString("0.0000", ci);
        }

        // --- Open-Meteo ---

        public sealed class OpenMeteoMore
        {
            public double? Elevation;
            public double? Cape;
            public double? FreezingLevelM;
            public double? SoilTemperatureC;
            public double? SoilMoisture;     // m³/m³
            public double? SnowDepthM;
            public double? RadiationWm2;
            public double? CapeMaxToday;
            public double? SunshineTodayS;
            public double? DaylightTodayS;
            public double? EvaporationTodayMm;

            /// <summary>Sunshine in seconds per hour, by local hour "yyyy-MM-ddTHH:00".</summary>
            public Dictionary<string, double> SunshineByHour = new();
        }

        public static Task<OpenMeteoMore?> GetOpenMeteoAsync(double lat, double lon) =>
            CachedAsync("openmeteo", lat, lon, () => FetchOpenMeteoAsync(lat, lon));

        private static async Task<OpenMeteoMore?> FetchOpenMeteoAsync(double lat, double lon)
        {
            var json = await GetTextAsync(OpenMeteo, "v1/forecast?" + Point(lat, lon) +
                "&current=cape,freezing_level_height,soil_temperature_6cm,soil_moisture_3_to_9cm,snow_depth,shortwave_radiation" +
                "&hourly=cape,sunshine_duration&daily=sunshine_duration,daylight_duration,et0_fao_evapotranspiration" +
                "&forecast_days=3&timezone=auto");
            return json == null ? null : ParseOpenMeteo(json);
        }

        /// <summary>Separate from the download, so it can be tested.</summary>
        public static OpenMeteoMore? ParseOpenMeteo(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("current", out var c))
                return null;

            var data = new OpenMeteoMore
            {
                Elevation = Number(root, "elevation"),
                Cape = Number(c, "cape"),
                FreezingLevelM = Number(c, "freezing_level_height"),
                SoilTemperatureC = Number(c, "soil_temperature_6cm"),
                SoilMoisture = Number(c, "soil_moisture_3_to_9cm"),
                SnowDepthM = Number(c, "snow_depth"),
                RadiationWm2 = Number(c, "shortwave_radiation")
            };
            var today = (Text(c, "time") ?? "").Split('T')[0];

            if (root.TryGetProperty("hourly", out var h) && h.TryGetProperty("time", out var times))
            {
                var cape = Array(h, "cape");
                var sun = Array(h, "sunshine_duration");
                var i = 0;
                foreach (var t in times.EnumerateArray())
                {
                    var time = t.GetString() ?? "";
                    if (i < sun.Count && sun[i] != null)
                        data.SunshineByHour[time] = sun[i]!.Value;
                    if (time.StartsWith(today, StringComparison.Ordinal) && i < cape.Count && cape[i] != null)
                        data.CapeMaxToday = Math.Max(data.CapeMaxToday ?? 0, cape[i]!.Value);
                    i++;
                }
            }

            if (root.TryGetProperty("daily", out var d))
            {
                data.SunshineTodayS = Array(d, "sunshine_duration").FirstOrDefault();
                data.DaylightTodayS = Array(d, "daylight_duration").FirstOrDefault();
                data.EvaporationTodayMm = Array(d, "et0_fao_evapotranspiration").FirstOrDefault();
            }
            return data;
        }

        // --- weather.com 2-day hourly forecast, for the tray's "Next 12 h" ---

        public static Task<V3WxForecastHourly?> GetHourlyAsync(double lat, double lon, WetUnits units) =>
            CachedAsync("hourly" + units, lat, lon, async () =>
            {
                var json = await GetTextAsync(ApiUtils.WeatherComClient, "v3/wx/forecast/hourly/2day?geocode=" + Place(lat, lon) +
                    "&units=" + units + "&language=" + ApiUtils.SystemLanguage + "&format=json&apiKey=" + ApiUtils.WeatherComKey);
                return json == null ? null : JsonSerializer.Deserialize(json, FluentWeatherJsonContext.Default.V3WxForecastHourly);
            });

        // --- NOAA NDBC buoys and tide stations ---

        public sealed class BuoyData
        {
            public string Id = "";
            public string Name = "";
            public double DistanceKm;
            public DateTimeOffset Observed;
            public double? WaveHeightM;
            public double? WavePeriodS;
            public double? WaveDirection;
            public double? AirC;
            public double? WaterC;
            public double? PressureHpa;
            public double? PressureChangeHpa;
            public double? TideFt;
        }

        public static Task<BuoyData?> GetBuoyAsync(double lat, double lon) =>
            CachedAsync("buoy", lat, lon, async () =>
            {
                var lines = await WaterService.GetNdbcLatestAsync();
                var best = lines == null ? null : ParseBuoy(lines, lat, lon, DateTimeOffset.UtcNow);
                if (best != null)
                    best.Name = await WaterService.GetNdbcNameAsync(best.Id);
                return best;
            });

        /// <summary>
        /// The nearest station within 50 km with waves or tide from the last 3 hours.
        /// Columns: STN LAT LON YYYY MM DD hh mm WDIR WSPD GST WVHT DPD APD MWD PRES PTDY ATMP WTMP DEWP VIS TIDE.
        /// </summary>
        public static BuoyData? ParseBuoy(IEnumerable<string> lines, double lat, double lon, DateTimeOffset now)
        {
            BuoyData? best = null;
            foreach (var line in lines)
            {
                if (line.Length == 0 || line[0] == '#')
                    continue;
                var c = line.Split((char[]?) null, StringSplitOptions.RemoveEmptyEntries);
                if (c.Length < 22 || !TryNumber(c[1], out var sLat) || !TryNumber(c[2], out var sLon))
                    continue;
                double? N(int i) => TryNumber(c[i], out var v) ? v : null;
                var waves = N(11);
                var tide = N(21);
                if (waves == null && tide == null)
                    continue;
                var km = WaterService.DistanceKm(lat, lon, sLat, sLon);
                if (km > WaterService.MaxDistanceKm || (best != null && km >= best.DistanceKm))
                    continue;
                DateTimeOffset observed;
                try
                {
                    observed = new DateTimeOffset(int.Parse(c[3], CultureInfo.InvariantCulture), int.Parse(c[4], CultureInfo.InvariantCulture),
                        int.Parse(c[5], CultureInfo.InvariantCulture), int.Parse(c[6], CultureInfo.InvariantCulture),
                        int.Parse(c[7], CultureInfo.InvariantCulture), 0, TimeSpan.Zero);
                }
                catch (Exception)
                {
                    continue;
                }
                if (now - observed > TimeSpan.FromHours(3))
                    continue;

                best = new BuoyData
                {
                    Id = c[0].ToUpperInvariant(),
                    DistanceKm = km,
                    Observed = observed,
                    WaveHeightM = waves,
                    WavePeriodS = N(12) ?? N(13),
                    WaveDirection = N(14),
                    PressureHpa = N(15),
                    PressureChangeHpa = N(16),
                    AirC = N(17),
                    WaterC = N(18),
                    TideFt = tide
                };
            }
            return best;
        }

        // --- USGS river gauges ---

        public sealed class GaugeData
        {
            public string Id = "";
            public string Name = "";
            public double DistanceKm;
            public DateTimeOffset Observed;
            public double HeightFt;
            public double? FlowCfs;

            /// <summary>The mean height of each of the last 7 days, oldest first.</summary>
            public List<double> DailyFt = new();
            public double? ChangeFt3h;
        }

        public static Task<GaugeData?> GetGaugeAsync(double lat, double lon) =>
            CachedAsync("gauge", lat, lon, () => FetchGaugeAsync(lat, lon));

        private const double GaugeKm = 40;

        private static string Bbox(double lat, double lon, double km)
        {
            var ci = CultureInfo.InvariantCulture;
            var dLat = km / 111.0;
            var dLon = km / (111.0 * Math.Max(0.2, Math.Cos(lat * Math.PI / 180)));
            return (lon - dLon).ToString("0.0000", ci) + "," + (lat - dLat).ToString("0.0000", ci) + "," +
                   (lon + dLon).ToString("0.0000", ci) + "," + (lat + dLat).ToString("0.0000", ci);
        }

        private static async Task<GaugeData?> FetchGaugeAsync(double lat, double lon)
        {
            const string latest = "ogcapi/v0/collections/latest-continuous/items?f=json&limit=200&parameter_code=";
            var heights = await GetTextAsync(Usgs, latest + "00065&bbox=" + Bbox(lat, lon, GaugeKm));
            if (heights == null)
                return null;
            var flows = await GetTextAsync(Usgs, latest + "00060&bbox=" + Bbox(lat, lon, GaugeKm));

            var heightById = ParseLatest(heights, lat, lon);
            var flowById = flows == null ? new Dictionary<string, (double Value, DateTimeOffset Time, double Km)>() : ParseLatest(flows, lat, lon);

            // The nearest gauge with a recent height. A gauge that also measures the flow is a river
            // gauge, so it comes before a lake or reservoir level within 1.5 times its distance.
            var candidates = heightById
                .Where(kv => DateTimeOffset.UtcNow - kv.Value.Time < TimeSpan.FromHours(6) && kv.Value.Km <= GaugeKm)
                .OrderBy(kv => kv.Value.Km * (flowById.ContainsKey(kv.Key) ? 1 : 1.5))
                .ToList();
            if (candidates.Count == 0)
                return null;

            var pick = candidates[0];
            var gauge = new GaugeData
            {
                Id = pick.Key,
                DistanceKm = pick.Value.Km,
                Observed = pick.Value.Time,
                HeightFt = pick.Value.Value,
                FlowCfs = flowById.TryGetValue(pick.Key, out var flow) ? flow.Value : null
            };

            var series = await GetTextAsync(Usgs, "ogcapi/v0/collections/continuous/items?f=json&skipGeometry=true&limit=5000" +
                "&properties=time,value&parameter_code=00065&time=P7D&monitoring_location_id=" + Uri.EscapeDataString(pick.Key));
            if (series != null)
                FillSeries(gauge, series);
            gauge.Name = await WaterService.GetUsgsNameAsync(pick.Key);
            return gauge;
        }

        private static Dictionary<string, (double Value, DateTimeOffset Time, double Km)> ParseLatest(string json, double lat, double lon)
        {
            var result = new Dictionary<string, (double Value, DateTimeOffset Time, double Km)>();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
                return result;
            foreach (var f in features.EnumerateArray())
            {
                if (!f.TryGetProperty("properties", out var p) || !f.TryGetProperty("geometry", out var g) ||
                    g.ValueKind != JsonValueKind.Object || !g.TryGetProperty("coordinates", out var xy) || xy.GetArrayLength() < 2)
                    continue;
                var id = Text(p, "monitoring_location_id");
                if (string.IsNullOrEmpty(id) || !TryNumber(Text(p, "value"), out var value) ||
                    !DateTimeOffset.TryParse(Text(p, "time"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var time))
                    continue;
                var km = WaterService.DistanceKm(lat, lon, xy[1].GetDouble(), xy[0].GetDouble());
                if (!result.TryGetValue(id!, out var old) || old.Time < time)
                    result[id!] = (value, time, km);
            }
            return result;
        }

        /// <summary>Fills the 7 daily means and the change of the last 3 hours from a USGS series reply.</summary>
        public static void FillSeries(GaugeData gauge, string json)
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
                return;
            var points = new List<(DateTimeOffset Time, double Value)>();
            foreach (var f in features.EnumerateArray())
            {
                if (!f.TryGetProperty("properties", out var p))
                    continue;
                if (TryNumber(Text(p, "value"), out var v) &&
                    DateTimeOffset.TryParse(Text(p, "time"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var t))
                    points.Add((t, v));
            }
            if (points.Count == 0)
                return;
            points.Sort((a, b) => a.Time.CompareTo(b.Time));
            var last = points[^1];
            var earlier = points.LastOrDefault(x => x.Time <= last.Time.AddHours(-3));
            if (earlier.Time != default)
                gauge.ChangeFt3h = last.Value - earlier.Value;
            for (var day = 6; day >= 0; day--)
            {
                var from = last.Time.AddDays(-day - 1);
                var to = last.Time.AddDays(-day);
                var inDay = points.Where(x => x.Time > from && x.Time <= to).Select(x => x.Value).ToList();
                if (inDay.Count > 0)
                    gauge.DailyFt.Add(inDay.Average());
            }
        }

        /// <summary>Seven values as a small bar picture, for example "▂▃▃▅▇▆▅".</summary>
        public static string Spark(IReadOnlyList<double> values)
        {
            if (values.Count < 2)
                return "";
            const string bars = "▁▂▃▄▅▆▇█";
            var min = values.Min();
            var max = values.Max();
            var sb = new StringBuilder();
            foreach (var v in values)
                sb.Append(max - min < 1e-6 ? bars[3] : bars[(int) Math.Round((v - min) / (max - min) * (bars.Length - 1))]);
            return sb.ToString();
        }

        // --- National Weather Service forecaster's discussion (US) ---

        public sealed class Discussion
        {
            public string Office = "";
            public DateTimeOffset Issued;
            public string Text = "";
        }

        public static Task<Discussion?> GetDiscussionAsync(double lat, double lon) =>
            CachedAsync("afd", lat, lon, async () =>
            {
                var ci = CultureInfo.InvariantCulture;
                var point = await GetTextAsync(Nws, "points/" + lat.ToString("0.####", ci) + "," + lon.ToString("0.####", ci));
                if (point == null)
                    return null;
                string? office;
                using (var doc = JsonDocument.Parse(point))
                    office = doc.RootElement.TryGetProperty("properties", out var p) ? Text(p, "cwa") : null;
                if (string.IsNullOrEmpty(office))
                    return null;

                var list = await GetTextAsync(Nws, "products/types/AFD/locations/" + office);
                if (list == null)
                    return null;
                string? id;
                using (var doc = JsonDocument.Parse(list))
                    id = doc.RootElement.TryGetProperty("@graph", out var g) && g.ValueKind == JsonValueKind.Array && g.GetArrayLength() > 0
                        ? Text(g[0], "id")
                        : null;
                if (string.IsNullOrEmpty(id))
                    return null;

                var product = await GetTextAsync(Nws, "products/" + id);
                if (product == null)
                    return null;
                using var productDoc = JsonDocument.Parse(product);
                var r = productDoc.RootElement;
                return new Discussion
                {
                    Office = office!,
                    Issued = DateTimeOffset.TryParse(Text(r, "issuanceTime"), ci, DateTimeStyles.None, out var t) ? t : DateTimeOffset.UtcNow,
                    Text = CleanDiscussion(Text(r, "productText") ?? "")
                };
            });

        /// <summary>
        /// The discussion without its message header, as paragraphs. The text is written in lines of about
        /// 70 characters. Lines of one paragraph are joined. Section names such as ".KEY MESSAGES..." stay
        /// on their own line. Lists that start with "-" keep their line breaks.
        /// </summary>
        public static string CleanDiscussion(string text)
        {
            var lines = text.Replace("\r", "").Split('\n').ToList();
            var start = lines.FindIndex(l => l.StartsWith(".", StringComparison.Ordinal) && l.TrimEnd().EndsWith("...", StringComparison.Ordinal));
            if (start < 0)
                start = Math.Min(lines.Count, 8);
            var sb = new StringBuilder();
            var paragraph = new StringBuilder();
            void Flush()
            {
                if (paragraph.Length == 0)
                    return;
                if (sb.Length > 0)
                    sb.Append("\n\n");
                sb.Append(paragraph.ToString().Trim());
                paragraph.Clear();
            }
            for (var i = start; i < lines.Count; i++)
            {
                var line = lines[i].Trim();
                if (line == "&&" || line == "$$")
                {
                    Flush();
                    continue;
                }
                if (line.Length == 0)
                {
                    Flush();
                    continue;
                }
                if (line.StartsWith(".", StringComparison.Ordinal) && line.Contains("...", StringComparison.Ordinal))
                {
                    Flush();
                    var end = line.IndexOf("...", 1, StringComparison.Ordinal);
                    paragraph.Append(line.Substring(1, end - 1).Trim());
                    Flush();
                    var rest = line[(end + 3)..].Trim();
                    if (rest.Length > 0)
                        paragraph.Append(rest);
                    continue;
                }
                if (line.StartsWith("-", StringComparison.Ordinal) && paragraph.Length > 0)
                    paragraph.Append('\n');
                else if (paragraph.Length > 0)
                    paragraph.Append(' ');
                paragraph.Append(line);
            }
            Flush();
            return sb.ToString();
        }

        // --- Text for the cards, tips and tray lines ---

        private static CultureInfo C => CultureInfo.CurrentCulture;
        private static string L(string key) => key.GetLocalized();
        private static string F(string key, params object[] args) => string.Format(C, key.GetLocalized(), args);

        private static bool Imperial(WetUnits units) => units == WetUnits.e;

        private static string Deg(WetUnits units) => "°" + MeasureUnitUtils.GetTemperatureUnits(units);

        private static string Temp(double celsius, WetUnits units) => WaterService.TemperatureFor(celsius, units) + Deg(units);

        private static string Altitude(double meters, WetUnits units) =>
            MeasureUnitUtils.CeilingInUnits((int) Math.Round(meters / 0.3048), units).ToString("N0", C) + " " + MeasureUnitUtils.GetAltitudeUnits(units);

        private static string Depth(double meters, WetUnits units) =>
            Imperial(units)
                ? (meters / 0.0254).ToString("0.#", C) + " in"
                : (meters * 100).ToString("0", C) + " cm";

        private static string Hours(double seconds) => (seconds / 3600).ToString("0.0", C) + " h";

        private static string Level(double value, double[] limits, string[] keys)
        {
            for (var i = 0; i < limits.Length; i++)
                if (value < limits[i])
                    return L(keys[i]);
            return L(keys[^1]);
        }

        /// <summary>The card or tray text of an item. Null when the location has no data for it.</summary>
        public static async Task<MoreText?> DescribeAsync(MoreItem item, MoreContext ctx)
        {
            try
            {
                return item switch
                {
                    MoreItem.Airport => await DescribeAirportAsync(ctx),
                    MoreItem.Snow => DescribeSnow(ctx),
                    MoreItem.Heat => DescribeHeat(ctx),
                    MoreItem.Storm => await DescribeStormAsync(ctx),
                    MoreItem.Ground => await DescribeGroundAsync(ctx),
                    MoreItem.Freezing => await DescribeFreezingAsync(ctx),
                    MoreItem.Sun => await DescribeSunAsync(ctx),
                    MoreItem.Buoy => await DescribeBuoyAsync(ctx),
                    MoreItem.Gauge => await DescribeGaugeAsync(ctx),
                    MoreItem.Hourly => await DescribeNext12Async(ctx),
                    _ => null
                };
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "MoreDataService " + item);
                return null;
            }
        }

        // A: "Mist now (KFFL), MVFR". The tip adds the ceiling, the airport forecast and the raw report.
        private static async Task<MoreText?> DescribeAirportAsync(MoreContext ctx)
        {
            var metar = await MetarService.NearestAsync(ctx.Latitude, ctx.Longitude);
            if (metar == null)
                return null;
            var weather = MetarService.WxInWords(metar.WxString);
            if (weather.Length == 0)
                weather = L("MoreAirportNoWeather");
            weather = char.ToUpper(weather[0], C) + weather[1..];
            var category = metar.FlightCategory ?? "";

            var note = weather + (category.Length > 0 ? ", " + category : "") + " (" + metar.Id + ")";
            var tip = new StringBuilder();
            tip.Append(F("MoreAirportTipStation", metar.Name, metar.Id, WaterService.DistanceFor(metar.DistanceKm, ctx.Units)));
            tip.Append('\n').Append(L("MoreAirportTipWeather")).Append(' ').Append(weather);
            if (category.Length > 0)
                tip.Append('\n').Append(L("MoreAirportTipCategory")).Append(' ').Append(category)
                    .Append(" (").Append(L("MoreCategory" + category)).Append(')');
            if (metar.CeilingFeet != null)
                tip.Append('\n').Append(L("MoreAirportTipCeiling")).Append(' ')
                    .Append(MeasureUnitUtils.CeilingInUnits(metar.CeilingFeet.Value, ctx.Units).ToString("N0", C))
                    .Append(' ').Append(MeasureUnitUtils.GetAltitudeUnits(ctx.Units));

            var taf = await MetarService.TafAsync(metar.Id, ctx.Latitude, ctx.Longitude);
            if (taf != null && taf.Periods.Count > 0)
            {
                tip.Append("\n\n").Append(F("MoreAirportTipTaf", taf.Id));
                foreach (var p in taf.Periods.Where(p => p.To > DateTimeOffset.UtcNow).Take(4))
                    tip.Append('\n').Append(await TafLineAsync(p, ctx.Units));
            }
            if (!string.IsNullOrEmpty(metar.Raw))
                tip.Append("\n\n").Append(L("MoreAirportTipRaw")).Append('\n').Append(metar.Raw);
            if (taf != null && taf.Raw.Length > 0)
                tip.Append('\n').Append(taf.Raw);
            tip.Append("\n\n").Append(L("MoreAirportCredit"));

            return new MoreText
            {
                Value = weather,
                Note = note,
                Tip = tip.ToString(),
                TrayLine = weather + (category.Length > 0 ? ", " + category : "") + " (" + metar.Id + ")"
            };
        }

        // "From 6 PM: 6+ mi, no ceiling" or "Fri 9 AM, 30% chance: 3 mi, light snow, ceiling 2,400 ft".
        private static async Task<string> TafLineAsync(TafPeriod p, WetUnits units)
        {
            var local = p.From.ToLocalTime();
            var when = (local.Date == DateTime.Today ? "" : local.ToString("ddd", C) + " ") + await VariousUtils.GetTimeBasedOnUserSettings(local.DateTime);
            var started = p.From <= DateTimeOffset.UtcNow;
            var head = p.Change switch
            {
                "TEMPO" => F("MoreTafTempo", started ? L("MoreTafNowLower") : when),
                _ when started => L("MoreTafNow"),
                "BECMG" => F("MoreTafBecoming", when),
                _ => F("MoreTafFrom", when)
            };
            if (p.Probability != null)
                head += ", " + F("MoreTafChance", p.Probability.Value);

            var parts = new List<string>();
            if (p.VisibilityMiles != null)
            {
                parts.Add(units is WetUnits.e or WetUnits.h
                    ? (p.VisibilityMiles.Value >= 6 ? "6+ mi" : p.VisibilityMiles.Value.ToString("0.#", C) + " mi")
                    : (p.VisibilityMiles.Value >= 6 ? "10+ km" : (p.VisibilityMiles.Value * 1.609344).ToString("0.#", C) + " km"));
            }
            var wx = MetarService.WxInWords(p.WxString);
            if (wx.Length > 0)
                parts.Add(wx);
            parts.Add(p.CeilingFeet == null
                ? L("MoreTafNoCeiling")
                : F("MoreTafCeiling", MeasureUnitUtils.CeilingInUnits(p.CeilingFeet.Value, units).ToString("N0", C) + " " + MeasureUnitUtils.GetAltitudeUnits(units)));
            return head + ": " + string.Join(", ", parts);
        }

        // B: snow of the last 6 hours, with 1 and 24 hours in the note.
        private static MoreText? DescribeSnow(MoreContext ctx)
        {
            var o = ctx.Observation;
            if (o == null)
                return null;
            var unit = " " + MeasureUnitUtils.GetSnowPrecipitationUnits(ctx.Units);
            string N(double v) => v.ToString("0.#", C) + unit;
            return new MoreText
            {
                Value = N(o.snow6Hour),
                Note = F("MoreSnowNote", N(o.snow1Hour), N(o.snow24Hour)),
                Tip = F("MoreSnowTip", N(o.snow1Hour), N(o.snow6Hour), N(o.snow24Hour)),
                TrayLine = F("MoreSnowTray", N(o.snow1Hour), N(o.snow6Hour))
            };
        }

        // C: wet bulb globe temperature, with the heat stress level.
        private static MoreText? DescribeHeat(MoreContext ctx)
        {
            var wbgt = ctx.Observation?.temperatureWetBulbGlobe;
            if (wbgt == null)
                return null;
            var celsius = Imperial(ctx.Units) ? (wbgt.Value - 32) * 5.0 / 9 : wbgt.Value;
            var level = Level(celsius, [25, 28, 30, 32], ["MoreHeatLow", "MoreHeatModerate", "MoreHeatHigh", "MoreHeatVeryHigh", "MoreHeatExtreme"]);
            var value = wbgt.Value + Deg(ctx.Units);
            return new MoreText
            {
                Value = value,
                Note = level,
                Tip = F("MoreHeatTip", value, level.ToLower(C)),
                TrayLine = value + ", " + level.ToLower(C)
            };
        }

        // D: storm energy now, the highest today, and the weather.com thunder risk of today.
        private static async Task<MoreText?> DescribeStormAsync(MoreContext ctx)
        {
            var om = await GetOpenMeteoAsync(ctx.Latitude, ctx.Longitude);
            if (om?.Cape == null && ctx.ThunderIndex == null)
                return null;
            string Energy(double j) => j.ToString("#,0", C) + " J/kg";
            string Strength(double j) => Level(j, [300, 1000, 2500, 4000], ["MoreCapeVeryLow", "MoreCapeLow", "MoreCapeModerate", "MoreCapeHigh", "MoreCapeExtreme"]);

            var value = om?.Cape != null ? Energy(om.Cape.Value) : "--";
            var thunder = ctx.ThunderIndex != null ? F("MoreThunderRisk", ctx.ThunderIndex.Value) : "";
            var tip = new StringBuilder(L("MoreStormTipIntro"));
            if (om?.Cape != null)
                tip.Append("\n\n").Append(F("MoreStormTipNow", Energy(om.Cape.Value), Strength(om.Cape.Value).ToLower(C)));
            if (om?.CapeMaxToday != null)
                tip.Append('\n').Append(F("MoreStormTipMax", Energy(om.CapeMaxToday.Value), Strength(om.CapeMaxToday.Value).ToLower(C)));
            if (ctx.ThunderIndex != null)
                tip.Append('\n').Append(thunder).Append(string.IsNullOrEmpty(ctx.ThunderCategory) ? "" : " (" + ctx.ThunderCategory + ")");
            tip.Append("\n\n").Append(L("MoreStormCredit"));

            return new MoreText
            {
                Value = value,
                Note = string.Join("\n", new[] { om?.Cape != null ? Strength(om.Cape.Value) : "", thunder }.Where(s => s.Length > 0)),
                Tip = tip.ToString(),
                TrayLine = ctx.ThunderIndex != null
                    ? F("MoreThunderTray", ctx.ThunderIndex.Value) + (om?.Cape != null ? ", " + Energy(om.Cape.Value) : "")
                    : value
            };
        }

        // E: soil temperature at 6 cm, soil moisture and snow depth.
        private static async Task<MoreText?> DescribeGroundAsync(MoreContext ctx)
        {
            var om = await GetOpenMeteoAsync(ctx.Latitude, ctx.Longitude);
            if (om?.SoilTemperatureC == null)
                return null;
            var temp = Temp(om.SoilTemperatureC.Value, ctx.Units);
            var moisture = om.SoilMoisture != null ? (om.SoilMoisture.Value * 100).ToString("0", C) + "%" : "--";
            var snow = om.SnowDepthM is > 0.005 ? Depth(om.SnowDepthM.Value, ctx.Units) : "";
            var note = F("MoreGroundMoisture", moisture) + (snow.Length > 0 ? "\n" + F("MoreGroundSnow", snow) : "");
            return new MoreText
            {
                Value = temp,
                Note = note,
                Tip = F("MoreGroundTip", temp, moisture, snow.Length > 0 ? snow : L("MoreGroundNoSnow")),
                TrayLine = F("MoreGroundTray", temp, moisture)
            };
        }

        // F: the height of the 0 °C level above sea level.
        private static async Task<MoreText?> DescribeFreezingAsync(MoreContext ctx)
        {
            var om = await GetOpenMeteoAsync(ctx.Latitude, ctx.Longitude);
            if (om?.FreezingLevelM == null)
                return null;
            var height = Altitude(om.FreezingLevelM.Value, ctx.Units);
            // At or below the ground the air at the surface is below freezing. The model then gives a
            // height near or below the ground, which says little by itself.
            var atGround = om.Elevation != null && om.FreezingLevelM.Value <= om.Elevation.Value + 50;
            var value = atGround ? L("MoreFreezingAtGround") : height;
            var note = atGround ? L("MoreFreezingAtGroundNote") : L("MoreFreezingAboveSea");
            var tip = (atGround ? L("MoreFreezingTipGround") : F("MoreFreezingTip", height)) +
                      (om.Elevation != null ? "\n" + F("MoreFreezingElevation", Altitude(om.Elevation.Value, ctx.Units)) : "") +
                      "\n\n" + L("MoreFreezingCredit");
            return new MoreText { Value = value, Note = note, Tip = tip, TrayLine = value };
        }

        // G: sunshine of today, daylight, solar radiation now and evaporation.
        private static async Task<MoreText?> DescribeSunAsync(MoreContext ctx)
        {
            var om = await GetOpenMeteoAsync(ctx.Latitude, ctx.Longitude);
            if (om?.SunshineTodayS == null)
                return null;
            var sun = Hours(om.SunshineTodayS.Value);
            var daylight = om.DaylightTodayS != null ? Hours(om.DaylightTodayS.Value) : "--";
            var radiation = om.RadiationWm2 != null ? om.RadiationWm2.Value.ToString("0", C) + " W/m²" : "--";
            var evaporation = om.EvaporationTodayMm == null ? "--"
                : Imperial(ctx.Units) ? (om.EvaporationTodayMm.Value / 25.4).ToString("0.00", C) + " in" : om.EvaporationTodayMm.Value.ToString("0.0", C) + " mm";
            return new MoreText
            {
                Value = sun,
                Note = F("MoreSunOfDaylight", daylight),
                Tip = F("MoreSunTip", sun, daylight, radiation, evaporation),
                TrayLine = F("MoreSunTray", sun, daylight)
            };
        }

        // H: waves, period and direction at the nearest buoy, or the tide at a tide station.
        private static async Task<MoreText?> DescribeBuoyAsync(MoreContext ctx)
        {
            var b = await GetBuoyAsync(ctx.Latitude, ctx.Longitude);
            if (b == null)
                return null;
            string Tide(double ft) => (ft > 0 ? "+" : "") + (Imperial(ctx.Units) ? ft.ToString("0.0", C) + " ft" : (ft * 0.3048).ToString("0.00", C) + " m");

            var direction = b.WaveDirection != null ? WaterService.Cardinal(b.WaveDirection.Value) : "";
            string value, note, tray;
            if (b.WaveHeightM != null)
            {
                value = WaterService.WaveHeightFor(b.WaveHeightM.Value, ctx.Units);
                var details = new List<string>();
                if (b.WavePeriodS != null)
                    details.Add(F("MoreBuoyEvery", b.WavePeriodS.Value.ToString("0", C)));
                if (direction.Length > 0)
                    details.Add(F("MoreBuoyFrom", direction));
                note = string.Join(", ", details);
                tray = string.Join(", ", new[] { value, b.WavePeriodS != null ? b.WavePeriodS.Value.ToString("0", C) + " s" : "", direction }.Where(s => s.Length > 0));
            }
            else
            {
                value = F("MoreBuoyTide", Tide(b.TideFt!.Value));
                note = "";
                tray = value;
            }

            var tip = new StringBuilder(F("MoreBuoyTipStation", b.Name, b.Id, WaterService.DistanceFor(b.DistanceKm, ctx.Units), WaterService.AgeText(b.Observed)));
            if (b.WaveHeightM != null)
                tip.Append('\n').Append(L("MoreBuoyTipWaves")).Append(' ').Append(value).Append(note.Length > 0 ? ", " + note : "");
            if (b.AirC != null)
                tip.Append('\n').Append(L("MoreBuoyTipAir")).Append(' ').Append(Temp(b.AirC.Value, ctx.Units));
            if (b.WaterC != null)
                tip.Append('\n').Append(L("MoreBuoyTipWater")).Append(' ').Append(Temp(b.WaterC.Value, ctx.Units));
            if (b.PressureHpa != null)
                tip.Append('\n').Append(L("MoreBuoyTipPressure")).Append(' ')
                    .Append(Imperial(ctx.Units) ? (b.PressureHpa.Value * 0.02953).ToString("0.00", C) + " inHg" : b.PressureHpa.Value.ToString("0", C) + " mb")
                    .Append(b.PressureChangeHpa != null ? " (" + (b.PressureChangeHpa > 0 ? "+" : "") + b.PressureChangeHpa.Value.ToString("0.0", C) + " mb, 3 h)" : "");
            if (b.TideFt != null)
                tip.Append('\n').Append(L("MoreBuoyTipTide")).Append(' ').Append(Tide(b.TideFt.Value));
            tip.Append("\n\n").Append(L("MoreBuoyCredit"));
            return new MoreText { Value = value, Note = note, Tip = tip.ToString(), TrayLine = tray };
        }

        // I: river height and flow, the trend, and the last 7 days as small bars.
        private static async Task<MoreText?> DescribeGaugeAsync(MoreContext ctx)
        {
            var g = await GetGaugeAsync(ctx.Latitude, ctx.Longitude);
            if (g == null)
                return null;
            string Height(double ft) => Imperial(ctx.Units) ? ft.ToString("0.00", C) + " ft" : (ft * 0.3048).ToString("0.00", C) + " m";
            string Flow(double cfs) => Imperial(ctx.Units) ? cfs.ToString("#,0", C) + " ft³/s" : (cfs * 0.0283168).ToString(cfs * 0.0283168 < 10 ? "0.0" : "#,0", C) + " m³/s";

            var trend = g.ChangeFt3h == null ? "" : g.ChangeFt3h > 0.05 ? L("MoreGaugeRising") : g.ChangeFt3h < -0.05 ? L("MoreGaugeFalling") : L("MoreGaugeSteady");
            var value = Height(g.HeightFt);
            var noteParts = new List<string>();
            if (g.FlowCfs != null)
                noteParts.Add(Flow(g.FlowCfs.Value));
            if (trend.Length > 0)
                noteParts.Add(noteParts.Count > 0 ? trend.ToLower(C) : trend);
            var spark = Spark(g.DailyFt);
            var note = string.Join(", ", noteParts) + (spark.Length > 0 ? "\n" + spark : "");

            var tip = new StringBuilder(F("MoreGaugeTipStation", g.Name, g.Id.Replace("USGS-", ""), WaterService.DistanceFor(g.DistanceKm, ctx.Units), WaterService.AgeText(g.Observed)));
            tip.Append('\n').Append(L("MoreGaugeTipHeight")).Append(' ').Append(value);
            if (g.FlowCfs != null)
                tip.Append('\n').Append(L("MoreGaugeTipFlow")).Append(' ').Append(Flow(g.FlowCfs.Value));
            if (g.ChangeFt3h != null)
                tip.Append('\n').Append(F("MoreGaugeTipChange", (g.ChangeFt3h > 0 ? "+" : "") + Height(g.ChangeFt3h.Value)));
            if (g.DailyFt.Count > 1)
                tip.Append('\n').Append(F("MoreGaugeTipWeek", spark, Height(g.DailyFt.Min()), Height(g.DailyFt.Max())));
            tip.Append("\n\n").Append(L("MoreGaugeCredit"));
            return new MoreText
            {
                Value = value,
                Note = note,
                Tip = tip.ToString(),
                TrayLine = value + (g.FlowCfs != null ? ", " + Flow(g.FlowCfs.Value) : "") + (trend.Length > 0 ? ", " + trend.ToLower(C) : "")
            };
        }

        // K (tray): rain, snow and ice of the next 12 hours, and the highest chance.
        private static async Task<MoreText?> DescribeNext12Async(MoreContext ctx)
        {
            var h = ctx.Hourly ?? await GetHourlyAsync(ctx.Latitude, ctx.Longitude, ctx.Units);
            if (h?.validTimeLocal == null || h.validTimeLocal.Count == 0)
                return null;
            var now = DateTimeOffset.UtcNow;
            var start = h.validTimeLocal.FindIndex(t => t.AddHours(1) > now);
            if (start < 0)
                return null;
            var end = Math.Min(h.validTimeLocal.Count, start + 12);
            double rain = 0, snow = 0, ice = 0;
            var chance = 0;
            for (var i = start; i < end; i++)
            {
                // Without the split amounts, all of qpf counts as rain.
                rain += h.qpfRain != null ? At(h.qpfRain, i) ?? 0 : h.qpf != null && i < h.qpf.Count ? h.qpf[i] : 0;
                snow += h.qpfSnow != null && i < h.qpfSnow.Count ? h.qpfSnow[i] : 0;
                ice += At(h.qpfIce, i) ?? 0;
                if (h.precipChance != null && i < h.precipChance.Count)
                    chance = Math.Max(chance, h.precipChance[i]);
            }
            var line = AmountsText(rain, snow, ice, ctx.Units);
            line = line.Length == 0 ? F("MoreNext12Dry", chance) : line + ", " + F("MoreNext12Chance", chance);
            return new MoreText { Value = line, TrayLine = line };
        }

        private static double? At(List<double?>? list, int i) => list != null && i < list.Count ? list[i] : null;

        /// <summary>"Rain 0.10 in, snow 1.2 in, ice 0.02 in". Amounts of zero are left out.</summary>
        public static string AmountsText(double rain, double snow, double ice, WetUnits units)
        {
            var liquid = " " + MeasureUnitUtils.GetLiquidPrecipitationUnits(units);
            var snowUnit = " " + MeasureUnitUtils.GetSnowPrecipitationUnits(units);
            var format = Imperial(units) ? "0.00" : "0.#";
            var parts = new List<string>();
            if (rain >= 0.005)
                parts.Add(F("MoreAmountRain", rain.ToString(format, C) + liquid));
            if (snow >= 0.05)
                parts.Add(F("MoreAmountSnow", snow.ToString("0.#", C) + snowUnit));
            if (ice >= 0.005)
                parts.Add(F("MoreAmountIce", ice.ToString(format, C) + liquid));
            return string.Join(", ", parts);
        }

        // --- JSON helpers ---

        private static string? Text(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static double? Number(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

        private static List<double?> Array(JsonElement e, string name)
        {
            var list = new List<double?>();
            if (e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array)
                foreach (var v in a.EnumerateArray())
                    list.Add(v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null);
            return list;
        }

        private static bool TryNumber(string? text, out double value) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
