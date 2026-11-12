using System;
using System.Globalization;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FluentWeather.Utils
{
    internal class ApiUtils
    {
        // Local build: the language chosen in the app settings, otherwise the Windows display language.
        private static readonly string SystemLanguage = !string.IsNullOrEmpty(Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride)
            ? Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride
            : Windows.System.UserProfile.GlobalizationPreferences.Languages[0];

        private static readonly HttpClient SharedClient = CreateClient("https://api.weather.com/v2/");

        /// <summary>
        /// An HttpClient that does not keep idle connections for long. While the app is suspended
        /// (window minimized or PC asleep), the network can close an idle connection without the app
        /// noticing. The next request on it then fails with "An existing connection was forcibly
        /// closed by the remote host". Idle connections are closed after 15 seconds, and every
        /// connection is replaced after 2 minutes.
        /// </summary>
        internal static HttpClient CreateClient(string baseAddress)
        {
            return new HttpClient(new ProxyFallbackHandler()) { BaseAddress = new Uri(baseAddress) };
        }

        private static SocketsHttpHandler CreateSocketsHandler(bool useProxy)
        {
            return new SocketsHttpHandler
            {
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15),
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                UseProxy = useProxy,
            };
        }

        /// <summary>
        /// True when Windows refused the connection (socket error 10013, access denied). A firewall,
        /// an antivirus program, or the app's network isolation causes this. The internet itself can
        /// still work.
        /// </summary>
        internal static bool IsConnectionBlocked(Exception? e)
        {
            for (var inner = e; inner != null; inner = inner.InnerException)
            {
                if (inner is SocketException socket &&
                    (socket.SocketErrorCode == SocketError.AccessDenied || socket.ErrorCode == 10013))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Sends requests through the Windows proxy setting, as before. Some VPN programs set that proxy
        /// to an address on this PC or on the local network. Windows does not let this app connect
        /// there, so the request fails with error 10013. The handler then sends the request again
        /// without the proxy, and uses no proxy for the rest of the session. Networks that need their
        /// proxy keep working, because the direct path is only tried after the proxy path was blocked.
        /// </summary>
        private sealed class ProxyFallbackHandler : HttpMessageHandler
        {
            private readonly HttpMessageInvoker _system = new(CreateSocketsHandler(useProxy: true));
            private readonly HttpMessageInvoker _direct = new(CreateSocketsHandler(useProxy: false));
            private volatile bool _useDirect;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                if (!_useDirect)
                {
                    try
                    {
                        return await _system.SendAsync(request, cancellationToken);
                    }
                    catch (HttpRequestException e) when (IsConnectionBlocked(e) && request.Content == null)
                    {
                        _useDirect = true;
                    }
                }

                return await _direct.SendAsync(Copy(request), cancellationToken);
            }

            private static HttpRequestMessage Copy(HttpRequestMessage request)
            {
                var copy = new HttpRequestMessage(request.Method, request.RequestUri)
                {
                    Version = request.Version,
                    VersionPolicy = request.VersionPolicy,
                };
                foreach (var header in request.Headers)
                    copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
                return copy;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _system.Dispose();
                    _direct.Dispose();
                }

                base.Dispose(disposing);
            }
        }

        /// <summary>
        /// Sends a GET request. When the connection fails before the server answers, the request is
        /// sent once more after 0.2 seconds. The failed connection is dropped, so the second try
        /// uses a new one. This covers the first request after the app wakes up.
        /// An answer with an error status (for example 404) is returned and not repeated.
        /// </summary>
        internal static async Task<HttpResponseMessage> GetWithRetryAsync(HttpClient client, string url)
        {
            try
            {
                return await client.GetAsync(url);
            }
            catch (HttpRequestException e) when (e.StatusCode == null)
            {
                await Task.Delay(200);
                return await client.GetAsync(url);
            }
        }

        public static async Task<HttpResponseMessage> GetFullData(string placeId)
        {
            if (string.IsNullOrEmpty(placeId))
                throw new ArgumentNullException(nameof(placeId));

            var units = (await VariousUtils.GetUnitsCode()).ToString();
            return await FromSourceAsync(
                async () => await GetWithRetryAsync(SharedClient,
                    "aggcommon/v3alertsHeadlines;v3-wx-observations-current;v3-wx-forecast-hourly-10day;v3-wx-forecast-daily-10day;v3-wx-forecast-daily-15day;v3-location-point;v2idxDrySkinDaypart10;v2idxWateringDaypart10;v2idxPollenDaypart10;v2idxRunDaypart10;v2idxDriveDaypart10?format=json&"
                    + await Services.BackupWeatherService.WeatherComPlaceQueryAsync(placeId)
                    + "&units=" + units
                    + "&language=" + SystemLanguage + "&apiKey=793db2b6128c4bc2bdb2b6128c0bc230"),
                () => Services.BackupWeatherService.FullDataJsonAsync(placeId, units));
        }

        // --- Local build: the weather data source (see DataSourceService and BackupWeatherService) ---

        /// <summary>
        /// Sends a weather.com request, or builds the reply from the Open-Meteo backup. A weather.com
        /// reply that refuses the key (401 or 403) counts toward the switch to the backup. Other
        /// failures, such as a lost connection, are reported as before and never switch. A good
        /// weather.com reply also remembers the place for the backup.
        /// </summary>
        private static async Task<HttpResponseMessage> FromSourceAsync(Func<Task<HttpResponseMessage>> weatherCom, Func<Task<string?>> backup)
        {
            if (await Services.DataSourceService.ShouldTryWeatherComAsync())
            {
                var response = await weatherCom();
                if (response.IsSuccessStatusCode)
                {
                    Services.DataSourceService.ReportWeatherComSuccess();
                    var json = await response.Content.ReadAsStringAsync();
                    _ = Services.BackupWeatherService.RememberFromWeatherComAsync(json);
                    return Json(json);
                }

                var refused = response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden;
                if (!refused || !await Services.DataSourceService.ReportKeyRefusedAsync())
                {
                    response.EnsureSuccessStatusCode();
                    return response;
                }
                response.Dispose();
            }

            var built = await backup();
            if (built == null)
                throw new HttpRequestException("The Open-Meteo backup has no data for this place.");
            return Json(built);
        }

        private static HttpResponseMessage Json(string json) =>
            new(System.Net.HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

        /// <summary>Location search, from weather.com or from the Open-Meteo backup, in the weather.com searchflat shape.</summary>
        public static async Task<HttpResponseMessage> SearchLocations(string query)
        {
            if (await Services.DataSourceService.ShouldTryWeatherComAsync())
            {
                // The full address: the base address of SharedClient ends in /v2/, and the search is under /v3/.
                var response = await GetWithRetryAsync(SharedClient, "https://api.weather.com/v3/location/searchflat?query=" + Uri.EscapeDataString(query) +
                                                                     "&language=" + SystemLanguage +
                                                                     "&apiKey=793db2b6128c4bc2bdb2b6128c0bc230&format=json");
                if (response.IsSuccessStatusCode)
                {
                    Services.DataSourceService.ReportWeatherComSuccess();
                    return response;
                }
                var refused = response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden;
                if (!refused || !await Services.DataSourceService.ReportKeyRefusedAsync())
                    return response;
                response.Dispose();
            }
            return Json(await Services.BackupWeatherService.SearchJsonAsync(query, SystemLanguage));
        }

        /// <summary>
        /// Local build: the nearest official weather station (usually an airport METAR station) to a point.
        /// Returns name, station id, distance in km and distance in miles, or null.
        /// The current conditions of weather.com are a blend for the exact point. This station is
        /// only the nearest one that reports, shown for reference.
        /// </summary>
        public static async Task<string[]?> GetNearestStation(double latitude, double longitude)
        {
            // The backup uses the nearest airport report (MetarService) instead.
            if (await Services.DataSourceService.IsBackupActiveAsync())
            {
                var report = await Services.MetarService.NearestAsync(latitude, longitude);
                if (report == null)
                    return null;
                var ci = CultureInfo.InvariantCulture;
                return [report.Name, report.Id, report.DistanceKm.ToString("0.0", ci), (report.DistanceKm / 1.609344).ToString("0.0", ci)];
            }

            var url = "https://api.weather.com/v3/location/near?geocode="
                      + latitude.ToString("0.###", CultureInfo.InvariantCulture) + ","
                      + longitude.ToString("0.###", CultureInfo.InvariantCulture)
                      + "&product=observation&format=json&apiKey=793db2b6128c4bc2bdb2b6128c0bc230";

            using var response = await GetWithRetryAsync(SharedClient, url);
            if (!response.IsSuccessStatusCode)
                return null;

            // JsonDocument needs no generated types, so it works with the trimmed (AOT) build.
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("location", out var location))
                return null;

            string? First(string name)
            {
                if (!location.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() == 0)
                    return null;
                var item = array[0];
                return item.ValueKind switch
                {
                    JsonValueKind.String => item.GetString(),
                    JsonValueKind.Number => item.GetDouble().ToString("0.0", CultureInfo.InvariantCulture),
                    _ => null
                };
            }

            var stationName = First("stationName");
            var stationId = First("stationId");
            var km = First("distanceKm");
            var mi = First("distanceMi");
            if (string.IsNullOrEmpty(stationName) || string.IsNullOrEmpty(stationId) || km == null || mi == null)
                return null;

            return [stationName, stationId, km, mi];
        }

        /// <summary>
        /// Current conditions, place name and the 3-day forecast (for moonrise and moonset), about 6 KB.
        /// Much smaller than GetFullData. If the request with the forecast fails, it is sent again
        /// without it, so the current conditions still arrive.
        /// </summary>
        public static async Task<HttpResponseMessage> GetCurrentConditions(string placeId)
        {
            if (string.IsNullOrEmpty(placeId))
                throw new ArgumentNullException(nameof(placeId));

            var units = (await VariousUtils.GetUnitsCode()).ToString();
            return await FromSourceAsync(async () =>
                {
                    var query = "?format=json&" + await Services.BackupWeatherService.WeatherComPlaceQueryAsync(placeId)
                                + "&units=" + units
                                + "&language=" + SystemLanguage + "&apiKey=793db2b6128c4bc2bdb2b6128c0bc230";

                    var response = await GetWithRetryAsync(SharedClient, "aggcommon/v3-wx-observations-current;v3-location-point;v3-wx-forecast-daily-3day;v2idxPollenDaypart10" + query);
                    if (!response.IsSuccessStatusCode && response.StatusCode is not (System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden))
                    {
                        response.Dispose();
                        response = await GetWithRetryAsync(SharedClient, "aggcommon/v3-wx-observations-current;v3-location-point" + query);
                    }
                    return response;
                },
                () => Services.BackupWeatherService.CurrentConditionsJsonAsync(placeId, units));
        }
    }
}
