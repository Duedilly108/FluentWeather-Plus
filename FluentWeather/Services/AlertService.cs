using FluentWeather.Helpers;
using FluentWeather.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FluentWeather.Services
{
    /// <summary>
    /// Local build: severe weather alerts for a location.
    ///
    /// The alerts come from weather.com (v3/alerts/headlines and v3/alerts/detail). weather.com passes
    /// on the alerts of the national weather services, for example the US National Weather Service
    /// and Environment Canada. When the app uses the Open-Meteo backup, or weather.com does not
    /// answer, US locations get the alerts directly from the National Weather Service
    /// (api.weather.gov). Each location is asked at most once every 5 minutes.
    /// </summary>
    public static class AlertService
    {
        private static readonly TimeSpan CacheTime = TimeSpan.FromMinutes(5);
        private static readonly ConcurrentDictionary<string, (DateTime FetchedAt, List<WeatherAlert> Alerts)> Cache = new();
        private static readonly ConcurrentDictionary<string, string> DetailCache = new();
        private static readonly HttpClient Nws = CreateNwsClient();

        /// <summary>Set by a "fluentweather-local:switch?...&amp;alerts=open" link. The main page opens the alert details.</summary>
        public static bool OpenDetailsRequested;

        /// <summary>Raised when a link asks for the alert details while the app is open.</summary>
        public static event Action? OpenDetailsRequestedChanged;

        public static void RequestOpenDetails()
        {
            OpenDetailsRequested = true;
            OpenDetailsRequestedChanged?.Invoke();
        }

        private static HttpClient CreateNwsClient()
        {
            var client = ApiUtils.CreateClient("https://api.weather.gov/");
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "FluentWeather-Plus (https://github.com/Duedilly108/FluentWeather-Plus)");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/geo+json");
            return client;
        }

        private static string Key(double latitude, double longitude) =>
            latitude.ToString("0.00", CultureInfo.InvariantCulture) + "," + longitude.ToString("0.00", CultureInfo.InvariantCulture);

        /// <summary>
        /// The active alerts of a location, most important first. An empty list when there are none
        /// or when no source answers.
        /// </summary>
        public static async Task<List<WeatherAlert>> GetAsync(double latitude, double longitude, string? countryCode)
        {
            var key = Key(latitude, longitude);
            if (Cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.FetchedAt < CacheTime)
                return hit.Alerts;

            List<WeatherAlert>? alerts = null;
            if (!await DataSourceService.IsBackupActiveAsync())
                alerts = await FromWeatherComAsync(latitude, longitude);
            if (alerts == null && (string.IsNullOrEmpty(countryCode) || string.Equals(countryCode, "US", StringComparison.OrdinalIgnoreCase)))
                alerts = await FromNwsAsync(latitude, longitude);

            alerts ??= hit.Alerts ?? new List<WeatherAlert>();
            alerts = alerts
                .Where(a => a.End == null || a.End > DateTimeOffset.UtcNow)
                .GroupBy(a => a.Key).Select(g => g.First())
                .OrderBy(a => a.Rank).ThenBy(a => a.End ?? DateTimeOffset.MaxValue)
                .ToList();
            Cache[key] = (DateTime.UtcNow, alerts);
            return alerts;
        }

        // --- weather.com ---

        private static async Task<List<WeatherAlert>?> FromWeatherComAsync(double latitude, double longitude)
        {
            try
            {
                var url = "v3/alerts/headlines?geocode=" + Key(latitude, longitude) + "&format=json&language=" +
                          ApiUtils.SystemLanguage + "&apiKey=" + ApiUtils.WeatherComKey;
                using var response = await ApiUtils.GetWithRetryAsync(ApiUtils.WeatherComClient, url);
                // 204 means no alerts.
                if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
                    return new List<WeatherAlert>();
                if (!response.IsSuccessStatusCode)
                    return null;
                var json = await response.Content.ReadAsStringAsync();
                return ParseWeatherCom(json);
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "AlertService weather.com");
                return null;
            }
        }

        /// <summary>The alerts of a weather.com headlines reply. Separate from the download, so it can be tested.</summary>
        public static List<WeatherAlert> ParseWeatherCom(string json)
        {
            var list = new List<WeatherAlert>();
            if (string.IsNullOrWhiteSpace(json))
                return list;
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("alerts", out var alerts) || alerts.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var a in alerts.EnumerateArray())
            {
                var detailKey = Text(a, "detailKey");
                var name = Text(a, "eventDescription");
                if (string.IsNullOrEmpty(detailKey) || string.IsNullOrEmpty(name))
                    continue;

                // One key per event, so an update of the same alert does not count as a new alert.
                var office = Text(a, "officeCode");
                var phenomena = Text(a, "phenomena");
                var significance = Text(a, "significance") ?? "";
                var number = Text(a, "eventTrackingNumber");
                var key = !string.IsNullOrEmpty(office) && !string.IsNullOrEmpty(phenomena) && !string.IsNullOrEmpty(number)
                    ? office + "." + phenomena + "." + significance + "." + number
                    : Text(a, "identifier") ?? detailKey;

                var officeName = Text(a, "officeName");
                var source = Text(a, "source") ?? "";
                if (!string.IsNullOrEmpty(officeName) && !source.Contains(officeName!, StringComparison.OrdinalIgnoreCase))
                    source = (source + " " + officeName).Trim();

                list.Add(new WeatherAlert
                {
                    Key = Clean(key),
                    DetailKey = detailKey!,
                    Name = name!,
                    Significance = significance,
                    SeverityCode = Number(a, "severityCode") is { } code ? (int) code : 5,
                    Issued = Time(a, "issueTimeLocal"),
                    End = Time(a, "endTimeLocal") ?? Time(a, "expireTimeLocal"),
                    Area = Text(a, "areaName") ?? "",
                    Source = source,
                    Summary = Text(a, "summaryHeadline") ?? Text(a, "headlineText") ?? "",
                    FromNws = false
                });
            }
            return list;
        }

        // --- National Weather Service (US only) ---

        private static async Task<List<WeatherAlert>?> FromNwsAsync(double latitude, double longitude)
        {
            try
            {
                var ci = CultureInfo.InvariantCulture;
                var url = "alerts/active?point=" + latitude.ToString("0.####", ci) + "," + longitude.ToString("0.####", ci);
                using var response = await ApiUtils.GetWithRetryAsync(Nws, url);
                if (!response.IsSuccessStatusCode)
                    return null;
                var json = await response.Content.ReadAsStringAsync();
                return ParseNws(json, DetailCache);
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "AlertService NWS");
                return null;
            }
        }

        /// <summary>The alerts of a National Weather Service reply. The texts go into "details".</summary>
        public static List<WeatherAlert> ParseNws(string json, IDictionary<string, string>? details)
        {
            var list = new List<WeatherAlert>();
            if (string.IsNullOrWhiteSpace(json))
                return list;
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var f in features.EnumerateArray())
            {
                if (!f.TryGetProperty("properties", out var p))
                    continue;
                var id = Text(p, "id");
                var name = Text(p, "event");
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
                    continue;

                // The event code (VTEC) has the form /O.CON.PAFC.WS.A.0014.261115T0000Z-261116T1200Z/.
                // Office, type, significance and number together name the event, as weather.com does.
                var key = id!;
                var significance = "";
                if (p.TryGetProperty("parameters", out var parameters) && parameters.TryGetProperty("VTEC", out var vtec) &&
                    vtec.ValueKind == JsonValueKind.Array && vtec.GetArrayLength() > 0 && vtec[0].ValueKind == JsonValueKind.String)
                {
                    var parts = (vtec[0].GetString() ?? "").Trim('/').Split('.');
                    if (parts.Length >= 6)
                    {
                        key = parts[2].TrimStart('K') + "." + parts[3] + "." + parts[4] + "." + parts[5];
                        significance = parts[4];
                    }
                }
                if (significance.Length == 0)
                    significance = name!.EndsWith("Warning") ? "W" : name.EndsWith("Watch") ? "A" : name.EndsWith("Advisory") ? "Y" : "S";

                var severity = Text(p, "severity") switch
                {
                    "Extreme" => 1,
                    "Severe" => 2,
                    "Moderate" => 3,
                    "Minor" => 4,
                    _ => 5
                };

                var text = (Text(p, "description") ?? "") +
                           (string.IsNullOrEmpty(Text(p, "instruction")) ? "" : "\n\n" + Text(p, "instruction"));
                if (details != null)
                    details[id!] = CleanText(text);

                list.Add(new WeatherAlert
                {
                    Key = Clean(key),
                    DetailKey = id!,
                    Name = name!,
                    Significance = significance,
                    SeverityCode = severity,
                    Issued = Time(p, "sent"),
                    End = Time(p, "ends") ?? Time(p, "expires"),
                    Area = Text(p, "areaDesc") ?? "",
                    Source = Text(p, "senderName") ?? "National Weather Service",
                    Summary = Text(p, "headline") ?? "",
                    FromNws = true
                });
            }
            return list;
        }

        /// <summary>The full text of an alert, in short paragraphs. Null when it cannot be loaded.</summary>
        public static async Task<string?> GetDetailTextAsync(WeatherAlert alert)
        {
            if (DetailCache.TryGetValue(alert.DetailKey, out var cached))
                return cached;
            if (alert.FromNws)
                return null;

            try
            {
                var url = "v3/alerts/detail?alertId=" + Uri.EscapeDataString(alert.DetailKey) + "&format=json&language=" +
                          ApiUtils.SystemLanguage + "&apiKey=" + ApiUtils.WeatherComKey;
                using var response = await ApiUtils.GetWithRetryAsync(ApiUtils.WeatherComClient, url);
                if (!response.IsSuccessStatusCode)
                    return null;
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (!doc.RootElement.TryGetProperty("alertDetail", out var detail) ||
                    !detail.TryGetProperty("texts", out var texts) || texts.ValueKind != JsonValueKind.Array)
                    return null;

                var sb = new StringBuilder();
                foreach (var t in texts.EnumerateArray())
                {
                    foreach (var part in new[] { "overview", "description", "instruction" })
                    {
                        var value = Text(t, part);
                        if (string.IsNullOrWhiteSpace(value))
                            continue;
                        if (sb.Length > 0)
                            sb.Append("\n\n");
                        sb.Append(value);
                    }
                }
                var result = CleanText(sb.ToString());
                DetailCache[alert.DetailKey] = result;
                return result;
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "AlertService detail");
                return null;
            }
        }

        /// <summary>
        /// Makes the text of an alert easier to read. The services send it as fixed-width lines of
        /// about 70 characters. Lines of one paragraph are joined. "* WHAT...Heavy snow." becomes
        /// "WHAT: Heavy snow." The "&amp;&amp;" end mark and the "..." around headlines are removed.
        /// </summary>
        public static string CleanText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";
            var paragraphs = Regex.Split(text.Replace("\r", "").Trim(), @"\n\s*\n");
            var result = new List<string>();
            foreach (var paragraph in paragraphs)
            {
                var p = Regex.Replace(paragraph.Trim(), @"\s*\n\s*", " ");
                if (p.Length == 0 || p == "&&")
                    continue;
                p = Regex.Replace(p, @"^\*\s*([A-Z][A-Z /]+?)\.\.\.", "$1: ");
                p = Regex.Replace(p, @"^([A-Z][A-Z /]+?)\.\.\.", "$1: ");
                p = p.Trim('.', ' ').Length > 0 && p.StartsWith("...") ? p.Trim('.', ' ') + "." : p;
                result.Add(p.Trim());
            }
            return string.Join("\n\n", result);
        }

        /// <summary>Removes the characters that the tray file uses to separate values.</summary>
        public static string Clean(string? value) =>
            (value ?? "").Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ').Trim();

        private static string? Text(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static double? Number(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

        private static DateTimeOffset? Time(JsonElement e, string name) =>
            DateTimeOffset.TryParse(Text(e, name), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

        // --- Settings ---

        /// <summary>Which alerts the main window band shows: 0 warnings, 1 warnings and watches, 2 all.</summary>
        public static bool Passes(WeatherAlert alert, int level) => level switch
        {
            0 => alert.Significance == "W",
            1 => alert.Significance is "W" or "A",
            _ => true
        };

        // --- Text ---

        /// <summary>"until Fri 6:00 PM" in the time of the location, or an empty string.</summary>
        public static async Task<string> UntilTextAsync(WeatherAlert alert)
        {
            if (alert.End is not { } end)
                return "";
            var time = await VariousUtils.GetTimeBasedOnUserSettings(end.DateTime);
            return string.Format(CultureInfo.CurrentCulture, "AlertUntil".GetLocalized(),
                end.ToString("ddd", CultureInfo.CurrentCulture) + " " + time);
        }

        /// <summary>"alert=" lines for the tray file, one per alert.</summary>
        public static async Task<string> TrayLinesAsync(List<WeatherAlert> alerts)
        {
            var sb = new StringBuilder();
            foreach (var a in alerts.Take(6))
            {
                // alert=key|significance|severity|end (ISO)|name|until text|area|source|summary
                sb.Append("alert=")
                    .Append(Clean(a.Key)).Append('|')
                    .Append(Clean(a.Significance)).Append('|')
                    .Append(a.SeverityCode.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(a.End?.ToString("o", CultureInfo.InvariantCulture) ?? "").Append('|')
                    .Append(Clean(a.Name)).Append('|')
                    .Append(Clean(await UntilTextAsync(a))).Append('|')
                    .Append(Clean(a.Area)).Append('|')
                    .Append(Clean(a.Source)).Append('|')
                    .Append(Clean(a.Summary))
                    .Append('\n');
            }
            return sb.ToString();
        }
    }

    /// <summary>Local build: one active weather alert.</summary>
    public sealed class WeatherAlert
    {
        /// <summary>The same for every update of one event, for example "DMX.WS.W.0012".</summary>
        public string Key { get; init; } = "";

        /// <summary>The key that loads the full text.</summary>
        public string DetailKey { get; init; } = "";
        public string Name { get; init; } = "";

        /// <summary>W warning, A watch, Y advisory, S statement.</summary>
        public string Significance { get; init; } = "";

        /// <summary>1 extreme, 2 severe, 3 moderate, 4 minor, 5 unknown.</summary>
        public int SeverityCode { get; init; } = 5;
        public DateTimeOffset? Issued { get; init; }
        public DateTimeOffset? End { get; init; }
        public string Area { get; init; } = "";
        public string Source { get; init; } = "";
        public string Summary { get; init; } = "";
        public bool FromNws { get; init; }

        /// <summary>Warnings first, then watches, advisories and statements. Within each, the most severe first.</summary>
        public int Rank => (Significance switch { "W" => 0, "A" => 1, "Y" => 2, _ => 3 }) * 10 + SeverityCode;
    }
}
