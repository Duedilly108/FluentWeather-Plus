using FluentWeather.Helpers;
using FluentWeather.Models;
using FluentWeather.Utils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Windows.Storage;

namespace FluentWeather.Services
{
    /// <summary>
    /// Local build: the nearest water station with a current water temperature, for the "Nearby
    /// water" card and the tray hover lines. Two free sources, no key:
    ///
    ///   NOAA NDBC (www.ndbc.noaa.gov): buoys and shore stations on the coasts and the Great Lakes,
    ///   including the NOAA tide stations. Water temperature, and often wind, gusts, air temperature
    ///   and wave height. One file holds the latest reading of every station. It is downloaded at
    ///   most every 30 minutes. The station names are downloaded once a month.
    ///
    ///   USGS Water Data (api.waterdata.usgs.gov): rivers, some lakes and reservoirs. Water
    ///   temperature only (parameter 00010). Station names are looked up once and kept.
    ///
    ///   Open-Meteo Marine (marine-api.open-meteo.com): the modeled sea surface temperature at the
    ///   location, when no station is in range. Ocean points only.
    ///
    /// The nearest NDBC station within MaxDistanceKm is tried first. When it has no reading newer
    /// than MaxAge, the nearest USGS station is tried. Null means no station, and the card hides.
    /// Each location is looked up at most once every 30 minutes.
    /// </summary>
    public static class WaterService
    {
        public const double MaxDistanceKm = 50;
        private static readonly TimeSpan MaxAge = TimeSpan.FromHours(6);
        private static readonly TimeSpan CacheTime = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan EmptyCacheTime = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan StationListAge = TimeSpan.FromDays(30);

        private const string NdbcStationsFile = "waterStationsNdbc";
        private const string UsgsNamesFile = "waterStationNamesUsgs";

        private static readonly HttpClient Ndbc = ApiUtils.CreateClient("https://www.ndbc.noaa.gov/");
        private static readonly HttpClient Usgs = ApiUtils.CreateClient("https://api.waterdata.usgs.gov/");

        private static readonly Dictionary<string, (DateTime FetchedUtc, WaterReading? Reading)> Cache = new();
        private static readonly SemaphoreSlim Lock = new(1, 1);
        private static WaterStationStore? _ndbcStations;
        private static string[]? _ndbcLatest;
        private static DateTime _ndbcLatestFetchedUtc;
        private static WaterStationNameStore? _usgsNames;

        // --- Settings ---

        public const string CardSettingKey = "waterCardEnabled";
        public const string TraySettingKey = "waterTrayEnabled";

        public static async Task<bool> IsCardEnabledAsync() =>
            await ApplicationData.Current.LocalSettings.ReadAsync<bool>(CardSettingKey);

        public static async Task<bool> IsTrayEnabledAsync() =>
            await ApplicationData.Current.LocalSettings.ReadAsync<bool>(TraySettingKey);

        // --- Lookup ---

        public static async Task<WaterReading?> GetNearestAsync(double latitude, double longitude)
        {
            var ci = CultureInfo.InvariantCulture;
            var key = latitude.ToString("0.00", ci) + "," + longitude.ToString("0.00", ci);

            await Lock.WaitAsync();
            try
            {
                // No reading (no station, or a failed request, for example right after the PC wakes up)
                // is tried again after EmptyCacheTime instead of CacheTime.
                if (Cache.TryGetValue(key, out var cached) &&
                    DateTime.UtcNow - cached.FetchedUtc < (cached.Reading == null ? EmptyCacheTime : CacheTime))
                    return cached.Reading;

                WaterReading? reading = null;
                try
                {
                    reading = await FromNdbcAsync(latitude, longitude) ?? await FromUsgsAsync(latitude, longitude) ??
                              await FromMarineModelAsync(latitude, longitude);
                }
                catch (Exception)
                {
                    // No reading. The card hides until the next try.
                }

                Cache[key] = (DateTime.UtcNow, reading);
                return reading;
            }
            finally
            {
                Lock.Release();
            }
        }

        // --- NOAA NDBC ---

        // One file with the latest observation of every NDBC station: buoys, coastal and Great
        // Lakes stations, and the NOAA tide stations. About 100 KB. Columns, separated by spaces:
        // STN LAT LON YYYY MM DD hh mm WDIR WSPD GST WVHT DPD APD MWD PRES PTDY ATMP WTMP DEWP VIS TIDE
        // Times are UTC. Speeds are m/s. Temperatures are Celsius. "MM" means missing.
        private const int ColWdir = 8, ColWspd = 9, ColGst = 10, ColWvht = 11, ColAtmp = 17, ColWtmp = 18;

        private static async Task<WaterReading?> FromNdbcAsync(double latitude, double longitude)
        {
            var lines = await GetNdbcLatestAsync();
            if (lines == null)
                return null;

            WaterReading? best = null;
            foreach (var line in lines)
            {
                if (line.Length == 0 || line[0] == '#')
                    continue;
                var c = line.Split((char[]?) null, StringSplitOptions.RemoveEmptyEntries);
                if (c.Length <= ColWtmp)
                    continue;
                if (!TryParse(c[1], out var lat) || !TryParse(c[2], out var lon) || !TryParse(c[ColWtmp], out var waterC))
                    continue;

                var distance = DistanceKm(latitude, longitude, lat, lon);
                if (distance > MaxDistanceKm || (best != null && distance >= best.DistanceKm))
                    continue;

                if (!int.TryParse(c[3], out var y) || !int.TryParse(c[4], out var mo) || !int.TryParse(c[5], out var d) ||
                    !int.TryParse(c[6], out var h) || !int.TryParse(c[7], out var mi))
                    continue;
                DateTimeOffset observed;
                try
                {
                    observed = new DateTimeOffset(y, mo, d, h, mi, 0, TimeSpan.Zero);
                }
                catch (ArgumentOutOfRangeException)
                {
                    continue;
                }
                if (DateTimeOffset.UtcNow - observed > MaxAge)
                    continue;

                var reading = new WaterReading
                {
                    Source = WaterSource.NoaaNdbc,
                    StationId = c[0].ToUpperInvariant(),
                    DistanceKm = distance,
                    WaterTemperatureC = waterC,
                    ObservedUtc = observed
                };
                if (TryParse(c[ColWspd], out var speed))
                {
                    reading.WindSpeedKmh = speed * 3.6;
                    if (TryParse(c[ColWdir], out var degrees))
                        reading.WindDirection = Cardinal(degrees);
                    if (TryParse(c[ColGst], out var gust))
                        reading.WindGustKmh = gust * 3.6;
                }
                if (TryParse(c[ColAtmp], out var airC))
                    reading.AirTemperatureC = airC;
                if (TryParse(c[ColWvht], out var waves))
                    reading.WaveHeightM = waves;
                best = reading;
            }

            if (best != null)
                best.StationName = await GetNdbcNameAsync(best.StationId);
            return best;
        }

        private static async Task<string[]?> GetNdbcLatestAsync()
        {
            if (_ndbcLatest != null && DateTime.UtcNow - _ndbcLatestFetchedUtc < CacheTime)
                return _ndbcLatest;

            try
            {
                using var response = await ApiUtils.GetWithRetryAsync(Ndbc, "data/latest_obs/latest_obs.txt");
                if (!response.IsSuccessStatusCode)
                    return _ndbcLatest;
                var text = await response.Content.ReadAsStringAsync();
                _ndbcLatest = text.Split('\n');
                _ndbcLatestFetchedUtc = DateTime.UtcNow;
                return _ndbcLatest;
            }
            catch (Exception)
            {
                return _ndbcLatest;
            }
        }

        // Station names come from the NDBC station list, about 270 KB, downloaded once a month.
        private static async Task<string> GetNdbcNameAsync(string stationId)
        {
            var list = await GetNdbcStationsAsync();
            var station = list?.FirstOrDefault(s => string.Equals(s.Id, stationId, StringComparison.OrdinalIgnoreCase));
            return station != null && station.Name.Length > 0 ? station.Name : "NDBC " + stationId;
        }

        private static async Task<List<WaterStation>?> GetNdbcStationsAsync()
        {
            if (_ndbcStations != null && DateTime.UtcNow.Ticks - _ndbcStations.FetchedUtcTicks < StationListAge.Ticks)
                return _ndbcStations.Stations;

            try
            {
                _ndbcStations = await ApplicationData.Current.LocalFolder.ReadAsync<WaterStationStore>(NdbcStationsFile);
            }
            catch (Exception)
            {
                _ndbcStations = null;
            }

            if (_ndbcStations != null && DateTime.UtcNow.Ticks - _ndbcStations.FetchedUtcTicks < StationListAge.Ticks)
                return _ndbcStations.Stations;

            try
            {
                using var response = await ApiUtils.GetWithRetryAsync(Ndbc, "activestations.xml");
                if (!response.IsSuccessStatusCode)
                    return _ndbcStations?.Stations;

                var store = new WaterStationStore { FetchedUtcTicks = DateTime.UtcNow.Ticks };
                using (var stream = await response.Content.ReadAsStreamAsync())
                using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore }))
                {
                    while (reader.Read())
                    {
                        if (reader.NodeType != XmlNodeType.Element || reader.Name != "station")
                            continue;
                        var id = reader.GetAttribute("id");
                        if (string.IsNullOrEmpty(id))
                            continue;
                        store.Stations.Add(new WaterStation { Id = id.ToUpperInvariant(), Name = CleanName(reader.GetAttribute("name"), id) });
                    }
                }

                if (store.Stations.Count == 0)
                    return _ndbcStations?.Stations;

                _ndbcStations = store;
                try
                {
                    await ApplicationData.Current.LocalFolder.SaveAsync(NdbcStationsFile, store);
                }
                catch (Exception)
                {
                    // Kept in memory for this run.
                }

                return store.Stations;
            }
            catch (Exception)
            {
                return _ndbcStations?.Stations;
            }
        }

        // NDBC names look like "Capistrano Beach Nearshore, CA (284)" or
        // "9410665 - Los Angeles Pier J, CA". The number parts are dropped.
        private static string CleanName(string? name, string id)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "NDBC " + id.ToUpperInvariant();
            var n = name!.Trim();
            var dash = n.IndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0 && n.Substring(0, dash).All(char.IsDigit))
                n = n.Substring(dash + 3);
            var paren = n.LastIndexOf(" (", StringComparison.Ordinal);
            if (paren > 0 && n.EndsWith(")", StringComparison.Ordinal))
                n = n.Substring(0, paren);
            return n;
        }

        public static string Cardinal(double degrees)
        {
            string[] names = ["N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW"];
            var index = (int) Math.Round(((degrees % 360) + 360) % 360 / 22.5) % 16;
            return names[index];
        }

        // --- Open-Meteo Marine ---
        // When no station is in range, the sea surface temperature of the Open-Meteo marine model at
        // the location is used. It exists only for points on or near the ocean. It is a model value,
        // so the card names the model, and the distance is 0.

        private static async Task<WaterReading?> FromMarineModelAsync(double latitude, double longitude)
        {
            var marine = await OpenMeteoExtrasService.GetMarineAsync(latitude, longitude);
            if (marine?.SeaTemperatureC == null)
                return null;
            return new WaterReading
            {
                Source = WaterSource.OpenMeteoMarine,
                StationId = "",
                StationName = "WaterMarineModel".GetLocalized(),
                DistanceKm = 0,
                WaterTemperatureC = marine.SeaTemperatureC.Value,
                ObservedUtc = DateTimeOffset.UtcNow,
                WaveHeightM = marine.WaveHeightM
            };
        }

        // --- USGS ---

        private static async Task<WaterReading?> FromUsgsAsync(double latitude, double longitude)
        {
            var ci = CultureInfo.InvariantCulture;
            var dLat = MaxDistanceKm / 111.0;
            var dLon = MaxDistanceKm / (111.0 * Math.Max(0.2, Math.Cos(latitude * Math.PI / 180)));
            var bbox = (longitude - dLon).ToString("0.0000", ci) + "," + (latitude - dLat).ToString("0.0000", ci) + "," +
                       (longitude + dLon).ToString("0.0000", ci) + "," + (latitude + dLat).ToString("0.0000", ci);

            UsgsFeatureCollection? collection;
            try
            {
                var url = "ogcapi/v0/collections/latest-continuous/items?f=json&parameter_code=00010&limit=100&bbox=" + bbox;
                using var response = await ApiUtils.GetWithRetryAsync(Usgs, url);
                if (!response.IsSuccessStatusCode)
                    return null;
                var json = await response.Content.ReadAsStringAsync();
                collection = JsonSerializer.Deserialize(json, FluentWeatherJsonContext.Default.UsgsFeatureCollection);
            }
            catch (Exception)
            {
                return null;
            }

            if (collection?.Features == null)
                return null;

            var best = collection.Features
                .Select(f =>
                {
                    var p = f.Properties;
                    var g = f.Geometry?.Coordinates;
                    if (p == null || g == null || g.Count < 2 || string.IsNullOrEmpty(p.MonitoringLocationId))
                        return null;
                    if (!TryParse(p.Value, out var value) || !DateTimeOffset.TryParse(p.Time, ci, DateTimeStyles.AdjustToUniversal, out var time))
                        return null;
                    if (DateTimeOffset.UtcNow - time > MaxAge)
                        return null;
                    if (p.UnitOfMeasure != null && p.UnitOfMeasure.StartsWith("degF", StringComparison.OrdinalIgnoreCase))
                        value = (value - 32) * 5 / 9;
                    return new WaterReading
                    {
                        Source = WaterSource.Usgs,
                        StationId = p.MonitoringLocationId,
                        DistanceKm = DistanceKm(latitude, longitude, g[1], g[0]),
                        WaterTemperatureC = value,
                        ObservedUtc = time
                    };
                })
                .Where(r => r != null && r.DistanceKm <= MaxDistanceKm)
                .OrderBy(r => r!.DistanceKm)
                .FirstOrDefault();

            if (best == null)
                return null;

            best.StationName = await GetUsgsNameAsync(best.StationId);
            return best;
        }

        private static async Task<string> GetUsgsNameAsync(string monitoringLocationId)
        {
            if (_usgsNames == null)
            {
                try
                {
                    _usgsNames = await ApplicationData.Current.LocalFolder.ReadAsync<WaterStationNameStore>(UsgsNamesFile);
                }
                catch (Exception)
                {
                    // A new store below.
                }
                _usgsNames ??= new WaterStationNameStore();
            }

            var known = _usgsNames.Stations.FirstOrDefault(s => s.Id == monitoringLocationId);
            if (known != null)
                return known.Name;

            var fallback = monitoringLocationId.Replace("USGS-", "USGS ");
            try
            {
                var url = "ogcapi/v0/collections/monitoring-locations/items/" + monitoringLocationId + "?f=json";
                using var response = await ApiUtils.GetWithRetryAsync(Usgs, url);
                if (!response.IsSuccessStatusCode)
                    return fallback;
                var json = await response.Content.ReadAsStringAsync();
                var name = JsonSerializer.Deserialize(json, FluentWeatherJsonContext.Default.UsgsFeature)?.Properties?.MonitoringLocationName;
                if (string.IsNullOrWhiteSpace(name))
                    return fallback;

                _usgsNames.Stations.Add(new WaterStation { Id = monitoringLocationId, Name = name });
                try
                {
                    await ApplicationData.Current.LocalFolder.SaveAsync(UsgsNamesFile, _usgsNames);
                }
                catch (Exception)
                {
                    // Kept in memory for this run.
                }

                return name;
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        // --- Helpers ---

        private static DateTimeOffset? ParseNoaaTime(string? text)
        {
            if (DateTimeOffset.TryParseExact(text, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time))
                return time;
            return null;
        }

        private static bool TryParse(string? text, out double value) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        public static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
        {
            const double r = 6371.0;
            var dLat = (lat2 - lat1) * Math.PI / 180;
            var dLon = (lon2 - lon1) * Math.PI / 180;
            var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                    Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            return 2 * r * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        }

        /// <summary>"3 min ago", "2 h ago", for the card and the tooltip.</summary>
        public static string AgeText(DateTimeOffset observedUtc)
        {
            var age = DateTimeOffset.UtcNow - observedUtc;
            if (age < TimeSpan.FromMinutes(1))
                return "WaterJustNow".GetLocalized();
            if (age < TimeSpan.FromHours(1))
                return string.Format(CultureInfo.CurrentCulture, "WaterMinutesAgo".GetLocalized(), (int) age.TotalMinutes);
            return string.Format(CultureInfo.CurrentCulture, "WaterHoursAgo".GetLocalized(), Math.Round(age.TotalHours, 1));
        }

        public static string SourceName(WaterSource source) =>
            source switch
            {
                WaterSource.NoaaNdbc => "NOAA NDBC",
                WaterSource.Usgs => "USGS",
                _ => "Open-Meteo Marine"
            };

        // --- Display units ---

        public static int TemperatureFor(double celsius, WetUnits units) =>
            (int) Math.Round(units == WetUnits.e ? celsius * 9 / 5 + 32 : celsius);

        public static int WindFor(double kmh, WetUnits units) => units switch
        {
            WetUnits.e or WetUnits.h => (int) Math.Round(kmh / 1.609344),
            WetUnits.s => (int) Math.Round(kmh / 3.6),
            _ => (int) Math.Round(kmh)
        };

        public static string WaveHeightFor(double meters, WetUnits units) =>
            units == WetUnits.e
                ? (meters * 3.28084).ToString("0.#", CultureInfo.CurrentCulture) + " ft"
                : meters.ToString("0.#", CultureInfo.CurrentCulture) + " m";

        public static string DistanceFor(double km, WetUnits units) =>
            units is WetUnits.e or WetUnits.h
                ? (km / 1.609344).ToString("0.#", CultureInfo.CurrentCulture) + " mi"
                : km.ToString("0.#", CultureInfo.CurrentCulture) + " km";
    }
}
