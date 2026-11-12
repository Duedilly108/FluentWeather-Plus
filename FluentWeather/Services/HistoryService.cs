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
using Windows.Storage;

namespace FluentWeather.Services
{
    /// <summary>
    /// The history of a saved location, for the Full History page: day by day, and hour by hour.
    ///
    /// Daily values merge two sources. The recorder (PastDaysService) keeps what the app itself
    /// observed, up to a year back. The Open-Meteo historical weather API (archive-api.open-meteo.com)
    /// fills the days the recorder missed and the fields it does not record. The archive is a
    /// reanalysis model (ERA5 from the Copernicus Climate Change Service), free for non-commercial use
    /// under CC BY 4.0, back to 1940, with a delay of about five days. A recorded value always wins.
    /// Daily air quality comes from the Open-Meteo air quality API, from about August 2022.
    ///
    /// Hourly values come from the archive for days older than a week, and from the Open-Meteo
    /// forecast API (which keeps the past 3 months) for recent days.
    ///
    /// One request covers one calendar month of one location. Replies are kept in LocalFolder.
    /// A complete month is never requested again. An incomplete one is requested again when its
    /// copy is more than a day old. The future is never requested.
    /// </summary>
    public static class HistoryService
    {
        private static readonly HttpClient Archive = ApiUtils.CreateClient("https://archive-api.open-meteo.com/");
        private static readonly HttpClient Forecast = ApiUtils.CreateClient("https://api.open-meteo.com/");
        private static readonly HttpClient AirQuality = ApiUtils.CreateClient("https://air-quality-api.open-meteo.com/");
        private static readonly TimeSpan RefreshAfter = TimeSpan.FromHours(24);
        private static readonly TimeSpan GapBetweenRequests = TimeSpan.FromMilliseconds(300);
        private static readonly SemaphoreSlim Lock = new(1, 1);

        // After a 429 (too many requests) reply, no request is sent for an hour.
        private static DateTime _pauseUntilUtc = DateTime.MinValue;

        // The archive publishes a day about five days after it ends. A month or day counts as
        // complete only when it is at least this many days old, so a null never gets frozen in.
        private const int ArchiveDelayDays = 7;

        // The first day of the Open-Meteo air quality history.
        public static readonly DateTime FirstAirQualityDay = new(2022, 8, 1);

        private const string DailyFields =
            "weather_code,temperature_2m_max,temperature_2m_min,precipitation_sum,snowfall_sum," +
            "wind_speed_10m_max,wind_gusts_10m_max,wind_direction_10m_dominant,relative_humidity_2m_mean," +
            "dew_point_2m_mean,pressure_msl_mean,cloud_cover_mean,sunshine_duration";

        private const string HourlyFields =
            "temperature_2m,relative_humidity_2m,dew_point_2m,precipitation,snowfall,weather_code," +
            "pressure_msl,cloud_cover,wind_speed_10m,wind_gusts_10m,wind_direction_10m";

        // --- Days ---

        /// <summary>
        /// The days of one month, oldest first, one entry per calendar day up to yesterday. Days
        /// that no source has are present with null values, so the chart can show the gap.
        /// </summary>
        public static async Task<List<HistoryDay>> GetMonthAsync(string placeId, int year, int month, string units, DateTimeOffset locationNow)
        {
            var today = locationNow.Date;
            var first = new DateTime(year, month, 1);
            var last = first.AddMonths(1).AddDays(-1);
            if (last >= today)
                last = today.AddDays(-1);

            var result = new List<HistoryDay>();
            if (last < first)
                return result;

            var recorded = await PastDaysService.GetRecordedDaysAsync(placeId, DateKey(first), DateKey(last), units);
            var archive = await GetArchiveMonthAsync(placeId, year, month, today);
            var air = await GetAirMonthAsync(placeId, year, month, today);

            for (var day = first; day <= last; day = day.AddDays(1))
            {
                var key = DateKey(day);
                var entry = new HistoryDay { Date = key };

                recorded.TryGetValue(key, out var record);
                var archived = archive?.Days.FirstOrDefault(d => d.Date == key);
                var airDay = air?.Days.FirstOrDefault(d => d.Date == key);

                if (record != null && (record.High != null || record.Low != null))
                {
                    entry.Source = HistorySource.Recorded;
                    entry.High = record.High;
                    entry.Low = record.Low;
                    entry.IconCode = record.IconCode;
                    entry.Phrase = record.Phrase;
                    entry.Precip = record.Precip;
                    entry.Snow = record.Snow;
                    entry.WindMax = record.WindMax;
                    entry.GustMax = record.GustMax;
                }
                else if (archived != null && (archived.High != null || archived.Low != null))
                {
                    entry.Source = HistorySource.Archive;
                    entry.High = HistoryUnits.TemperatureFromMetric(archived.High, units);
                    entry.Low = HistoryUnits.TemperatureFromMetric(archived.Low, units);
                    entry.IconCode = WmoWeather.IconCode(archived.WeatherCode);
                    entry.Phrase = WmoWeather.Phrase(archived.WeatherCode);
                }

                // The archive fills every field that the recorder does not have.
                if (archived != null)
                {
                    entry.Precip ??= HistoryUnits.PrecipFromMetric(archived.Precip, units);
                    entry.Snow ??= HistoryUnits.SnowFromMetric(archived.Snow, units);
                    entry.WindMax ??= HistoryUnits.WindFromMetric(archived.WindMax, units);
                    entry.GustMax ??= HistoryUnits.WindFromMetric(archived.GustMax, units);
                    entry.WindDirection = archived.WindDirection;
                    entry.Humidity = Round(archived.Humidity);
                    entry.DewPoint = HistoryUnits.TemperatureFromMetric(archived.DewPoint, units);
                    entry.Pressure = HistoryUnits.PressureFromMetric(archived.Pressure, units);
                    entry.CloudCover = Round(archived.CloudCover);
                    entry.SunHours = archived.SunshineSeconds == null ? null : Math.Round(archived.SunshineSeconds.Value / 3600, 1);
                }

                if (airDay != null)
                {
                    entry.UsAqi = Round(airDay.UsAqiMax);
                    entry.EuropeanAqi = Round(airDay.EuropeanAqiMax);
                    entry.Pm25 = airDay.Pm25Mean == null ? null : Math.Round(airDay.Pm25Mean.Value, 1);
                }

                result.Add(entry);
            }

            return result;
        }

        /// <summary>The days of one year, oldest first. Twelve month requests at most, once.</summary>
        public static async Task<List<HistoryDay>> GetYearAsync(string placeId, int year, string units, DateTimeOffset locationNow)
        {
            var result = new List<HistoryDay>();
            for (var month = 1; month <= 12; month++)
            {
                var days = await GetMonthAsync(placeId, year, month, units, locationNow);
                if (days.Count == 0)
                    break;
                result.AddRange(days);
            }

            return result;
        }

        // --- Hours ---

        /// <summary>
        /// The hours of one month up to the end of yesterday, in the local time of the location,
        /// oldest first. Empty when there is no data. One download per month, kept in LocalFolder.
        /// </summary>
        public static async Task<List<HistoryHour>> GetMonthHoursAsync(string placeId, int year, int month, string units, DateTimeOffset locationNow)
        {
            var result = new List<HistoryHour>();
            var today = locationNow.Date;
            await RemoveOldDayFilesAsync();
            var store = await GetHoursMonthStoreAsync(placeId, year, month, today);
            if (store == null)
                return result;

            var sun = new Dictionary<string, (DateTime? Rise, DateTime? Set)>();
            foreach (var day in store.Days)
                sun[day.Date] = (ParseLocal(day.Sunrise), ParseLocal(day.Sunset));

            foreach (var h in store.Hours)
            {
                var time = ParseLocal(h.Time);
                if (time == null || time.Value.Date >= today)
                    continue;
                sun.TryGetValue(DateKey(time.Value.Date), out var s);
                var middle = time.Value;
                result.Add(new HistoryHour
                {
                    Time = time.Value,
                    Temperature = HistoryUnits.TemperatureFromMetric(h.Temperature, units),
                    DewPoint = HistoryUnits.TemperatureFromMetric(h.DewPoint, units),
                    Humidity = Round(h.Humidity),
                    Precip = HistoryUnits.PrecipFromMetric(h.Precip, units),
                    Snow = HistoryUnits.SnowFromMetric(h.Snow, units),
                    Pressure = HistoryUnits.PressureFromMetric(h.Pressure, units),
                    CloudCover = Round(h.CloudCover),
                    Wind = HistoryUnits.WindFromMetric(h.Wind, units),
                    Gust = HistoryUnits.WindFromMetric(h.Gust, units),
                    WindDirection = h.WindDirection,
                    UsAqi = Round(h.UsAqi),
                    EuropeanAqi = Round(h.EuropeanAqi),
                    Pm25 = h.Pm25 == null ? null : Math.Round(h.Pm25.Value, 1),
                    Phrase = WmoWeather.Phrase(h.WeatherCode),
                    // The chart draws each hour centered on its time, so the time decides day or night.
                    IsNight = s.Rise != null && s.Set != null && (middle < s.Rise.Value || middle > s.Set.Value)
                });
            }

            return result;
        }

        private static async Task<HistoryHoursMonthStore?> GetHoursMonthStoreAsync(string placeId, int year, int month, DateTime today)
        {
            var first = new DateTime(year, month, 1);
            if (first >= today)
                return null;

            var coordinates = await PastDaysService.GetCoordinatesAsync(placeId);
            if (coordinates == null)
                return null;

            var fileName = "historyhours-" + placeId + "-" + first.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            await Lock.WaitAsync();
            try
            {
                var store = await ReadAsync<HistoryHoursMonthStore>(fileName);
                if (store != null && store.Version == HistoryHoursMonthStore.CurrentVersion &&
                    (store.Complete || DateTime.UtcNow.Ticks - store.FetchedUtcTicks < RefreshAfter.Ticks))
                    return store;

                var downloaded = await DownloadMonthHoursAsync(coordinates.Value.Latitude, coordinates.Value.Longitude, first, today);
                if (downloaded == null)
                    return store;
                await SaveAsync(fileName, downloaded);
                return downloaded;
            }
            finally
            {
                Lock.Release();
            }
        }

        private static async Task<HistoryHoursMonthStore?> DownloadMonthHoursAsync(double latitude, double longitude, DateTime first, DateTime today)
        {
            var last = first.AddMonths(1).AddDays(-1);
            var end = last < today ? last : today.AddDays(-1);
            if (end < first || DateTime.UtcNow < _pauseUntilUtc)
                return null;

            var store = new HistoryHoursMonthStore { Version = HistoryHoursMonthStore.CurrentVersion, FetchedUtcTicks = DateTime.UtcNow.Ticks };
            var byTime = new Dictionary<string, HistoryArchiveHour>();

            // The archive has the days older than about a week. The forecast API keeps the past
            // 3 months and has the recent days.
            var archiveEnd = today.AddDays(-ArchiveDelayDays);
            if (first <= archiveEnd &&
                !await AddHoursAsync(store, byTime, Archive, "v1/archive?", latitude, longitude, first, end < archiveEnd ? end : archiveEnd))
                return null;
            var recentStart = first > archiveEnd ? first : archiveEnd.AddDays(1);
            if (recentStart <= end &&
                !await AddHoursAsync(store, byTime, Forecast, "v1/forecast?", latitude, longitude, recentStart, end))
                return null;

            // Air quality for the same hours, when the month is in its range.
            if (end >= FirstAirQualityDay)
            {
                var air = await DownloadAirHoursAsync(latitude, longitude, first < FirstAirQualityDay ? FirstAirQualityDay : first, end);
                if (air?.Time != null)
                {
                    for (var i = 0; i < air.Time.Count; i++)
                    {
                        if (!byTime.TryGetValue(air.Time[i], out var hour))
                            continue;
                        hour.UsAqi = At(air.UsAqi, i);
                        hour.EuropeanAqi = At(air.EuropeanAqi, i);
                        hour.Pm25 = At(air.Pm25, i);
                    }
                }
            }

            var daysInMonth = DateTime.DaysInMonth(first.Year, first.Month);
            store.Complete = (today - last).TotalDays >= ArchiveDelayDays && store.Hours.Count >= daysInMonth * 24 - 1 &&
                             store.Hours.All(x => x.Temperature != null);
            return store;
        }

        // Adds the hours of one date range from the archive or the forecast API. False when the request failed.
        private static async Task<bool> AddHoursAsync(HistoryHoursMonthStore store, Dictionary<string, HistoryArchiveHour> byTime,
            HttpClient client, string path, double latitude, double longitude, DateTime first, DateTime last)
        {
            var json = await GetJsonAsync(client, path + Point(latitude, longitude) +
                "&start_date=" + DateKey(first) + "&end_date=" + DateKey(last) +
                "&hourly=" + HourlyFields + "&daily=sunrise,sunset&timezone=auto");
            if (json == null)
                return false;

            var reply = JsonSerializer.Deserialize(json, FluentWeatherJsonContext.Default.HourlyHistoryResponse);
            var h = reply?.Hourly;
            if (h?.Time == null)
                return false;

            var daily = reply!.Daily;
            if (daily?.Time != null)
            {
                for (var i = 0; i < daily.Time.Count; i++)
                {
                    store.Days.Add(new HistorySunDay
                    {
                        Date = daily.Time[i],
                        Sunrise = daily.Sunrise != null && i < daily.Sunrise.Count ? daily.Sunrise[i] : null,
                        Sunset = daily.Sunset != null && i < daily.Sunset.Count ? daily.Sunset[i] : null
                    });
                }
            }

            for (var i = 0; i < h.Time.Count; i++)
            {
                if (byTime.ContainsKey(h.Time[i]))
                    continue;
                var hour = new HistoryArchiveHour
                {
                    Time = h.Time[i],
                    Temperature = At(h.Temperature, i),
                    Humidity = At(h.Humidity, i),
                    DewPoint = At(h.DewPoint, i),
                    Precip = At(h.Precipitation, i),
                    Snow = At(h.Snowfall, i),
                    Pressure = At(h.Pressure, i),
                    CloudCover = At(h.CloudCover, i),
                    Wind = At(h.WindSpeed, i),
                    Gust = At(h.WindGusts, i),
                    WindDirection = At(h.WindDirection, i),
                    WeatherCode = At(h.WeatherCode, i)
                };
                byTime[hour.Time] = hour;
                store.Hours.Add(hour);
            }

            return true;
        }

        // Earlier builds kept the hours of each day in its own file (historyday-...). The month
        // files replace them, so the old files are removed once per run.
        private static bool _oldDayFilesRemoved;

        private static async Task RemoveOldDayFilesAsync()
        {
            if (_oldDayFilesRemoved)
                return;
            _oldDayFilesRemoved = true;
            try
            {
                foreach (var file in await ApplicationData.Current.LocalFolder.GetFilesAsync())
                {
                    if (file.Name.StartsWith("historyday-", StringComparison.Ordinal))
                        await file.DeleteAsync();
                }
            }
            catch (Exception)
            {
                // Old files only take a little space.
            }
        }

        // --- Archive months ---

        private static async Task<HistoryMonthStore?> GetArchiveMonthAsync(string placeId, int year, int month, DateTime today)
        {
            var first = new DateTime(year, month, 1);
            if (first >= today)
                return null;

            var coordinates = await PastDaysService.GetCoordinatesAsync(placeId);
            if (coordinates == null)
                return null;

            var monthKey = first.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            var fileName = "history-" + placeId + "-" + monthKey;

            await Lock.WaitAsync();
            try
            {
                var store = await ReadAsync<HistoryMonthStore>(fileName);
                if (store != null && store.Version == HistoryMonthStore.CurrentVersion &&
                    (store.Complete || DateTime.UtcNow.Ticks - store.FetchedUtcTicks < RefreshAfter.Ticks))
                    return store;

                var downloaded = await DownloadMonthAsync(coordinates.Value.Latitude, coordinates.Value.Longitude, first, today);
                if (downloaded == null)
                    return store;

                downloaded.PlaceId = placeId;
                downloaded.Month = monthKey;
                await SaveAsync(fileName, downloaded);
                return downloaded;
            }
            finally
            {
                Lock.Release();
            }
        }

        private static async Task<HistoryMonthStore?> DownloadMonthAsync(double latitude, double longitude, DateTime first, DateTime today)
        {
            var last = first.AddMonths(1).AddDays(-1);
            var requestLast = last < today ? last : today.AddDays(-1);
            if (requestLast < first || DateTime.UtcNow < _pauseUntilUtc)
                return null;

            var json = await GetJsonAsync(Archive, "v1/archive?" + Point(latitude, longitude) +
                "&start_date=" + DateKey(first) + "&end_date=" + DateKey(requestLast) +
                "&daily=" + DailyFields + "&timezone=auto");
            if (json == null)
                return null;

            var daily = JsonSerializer.Deserialize(json, FluentWeatherJsonContext.Default.ArchiveResponse)?.Daily;
            if (daily?.Time == null)
                return null;

            var store = new HistoryMonthStore { Version = HistoryMonthStore.CurrentVersion, FetchedUtcTicks = DateTime.UtcNow.Ticks };
            for (var i = 0; i < daily.Time.Count; i++)
            {
                store.Days.Add(new HistoryArchiveDay
                {
                    Date = daily.Time[i],
                    High = At(daily.TemperatureMax, i),
                    Low = At(daily.TemperatureMin, i),
                    Precip = At(daily.PrecipitationSum, i),
                    Snow = At(daily.SnowfallSum, i),
                    WindMax = At(daily.WindSpeedMax, i),
                    GustMax = At(daily.WindGustsMax, i),
                    WindDirection = At(daily.WindDirection, i),
                    Humidity = At(daily.HumidityMean, i),
                    DewPoint = At(daily.DewPointMean, i),
                    Pressure = At(daily.PressureMean, i),
                    CloudCover = At(daily.CloudCoverMean, i),
                    SunshineSeconds = At(daily.SunshineSeconds, i),
                    WeatherCode = At(daily.WeatherCode, i)
                });
            }

            var daysInMonth = DateTime.DaysInMonth(first.Year, first.Month);
            store.Complete = (today - last).TotalDays >= ArchiveDelayDays && store.Days.Count == daysInMonth &&
                             store.Days.All(d => d.High != null && d.Low != null);
            return store;
        }

        // --- Air quality months ---

        private static async Task<HistoryAirMonthStore?> GetAirMonthAsync(string placeId, int year, int month, DateTime today)
        {
            var first = new DateTime(year, month, 1);
            var last = first.AddMonths(1).AddDays(-1);
            if (first >= today || last < FirstAirQualityDay)
                return null;

            var coordinates = await PastDaysService.GetCoordinatesAsync(placeId);
            if (coordinates == null)
                return null;

            var fileName = "historyaq-" + placeId + "-" + first.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            await Lock.WaitAsync();
            try
            {
                var store = await ReadAsync<HistoryAirMonthStore>(fileName);
                if (store != null && (store.Complete || DateTime.UtcNow.Ticks - store.FetchedUtcTicks < RefreshAfter.Ticks))
                    return store;

                var start = first < FirstAirQualityDay ? FirstAirQualityDay : first;
                var end = last < today ? last : today.AddDays(-1);
                var hours = await DownloadAirHoursAsync(coordinates.Value.Latitude, coordinates.Value.Longitude, start, end);
                if (hours?.Time == null)
                    return store;

                // Daily values from the hours: the highest index and the mean fine particles.
                var downloaded = new HistoryAirMonthStore { FetchedUtcTicks = DateTime.UtcNow.Ticks };
                foreach (var group in Enumerable.Range(0, hours.Time.Count).GroupBy(i => hours.Time[i].Length >= 10 ? hours.Time[i].Substring(0, 10) : ""))
                {
                    if (group.Key.Length == 0)
                        continue;
                    var us = group.Select(i => At(hours.UsAqi, i)).Where(v => v != null).ToList();
                    var eu = group.Select(i => At(hours.EuropeanAqi, i)).Where(v => v != null).ToList();
                    var pm = group.Select(i => At(hours.Pm25, i)).Where(v => v != null).ToList();
                    downloaded.Days.Add(new HistoryAirDay
                    {
                        Date = group.Key,
                        UsAqiMax = us.Count > 0 ? us.Max() : null,
                        EuropeanAqiMax = eu.Count > 0 ? eu.Max() : null,
                        Pm25Mean = pm.Count > 0 ? pm.Average(v => v!.Value) : null
                    });
                }
                downloaded.Complete = (today - last).TotalDays >= ArchiveDelayDays;
                await SaveAsync(fileName, downloaded);
                return downloaded;
            }
            finally
            {
                Lock.Release();
            }
        }

        private static async Task<AirQualityHistoryHourly?> DownloadAirHoursAsync(double latitude, double longitude, DateTime first, DateTime last)
        {
            if (DateTime.UtcNow < _pauseUntilUtc)
                return null;
            var json = await GetJsonAsync(AirQuality, "v1/air-quality?" + Point(latitude, longitude) +
                "&start_date=" + DateKey(first) + "&end_date=" + DateKey(last) +
                "&hourly=us_aqi,european_aqi,pm2_5&timezone=auto");
            return json == null ? null : JsonSerializer.Deserialize(json, FluentWeatherJsonContext.Default.AirQualityHistoryResponse)?.Hourly;
        }

        // --- Helpers ---

        private static async Task<string?> GetJsonAsync(HttpClient client, string url)
        {
            try
            {
                await Task.Delay(GapBetweenRequests);
                using var response = await ApiUtils.GetWithRetryAsync(client, url);
                if ((int) response.StatusCode == 429)
                    _pauseUntilUtc = DateTime.UtcNow.AddHours(1);
                if (!response.IsSuccessStatusCode)
                    return null;
                return await response.Content.ReadAsStringAsync();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static async Task<T?> ReadAsync<T>(string fileName) where T : class
        {
            try
            {
                return await ApplicationData.Current.LocalFolder.ReadAsync<T>(fileName);
            }
            catch (Exception)
            {
                // A damaged file is replaced by a new download.
                return null;
            }
        }

        private static async Task SaveAsync<T>(string fileName, T value)
        {
            try
            {
                await ApplicationData.Current.LocalFolder.SaveAsync(fileName, value);
            }
            catch (Exception)
            {
                // The data is still shown. The next open downloads it again.
            }
        }

        private static string Point(double latitude, double longitude)
        {
            var ci = CultureInfo.InvariantCulture;
            return "latitude=" + latitude.ToString("0.0000", ci) + "&longitude=" + longitude.ToString("0.0000", ci);
        }

        private static DateTime? ParseLocal(string? text) =>
            DateTime.TryParseExact(text, "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

        private static int? Round(double? value) => value == null ? null : (int) Math.Round(value.Value);

        private static T? At<T>(List<T?>? list, int index) where T : struct =>
            list != null && index < list.Count ? list[index] : null;

        private static string DateKey(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>Unit conversions for history values. The archive is metric, the recorder uses the app's unit code.</summary>
    public static class HistoryUnits
    {
        public static bool IsFahrenheit(string units) => units == "e";
        public static bool IsInches(string units) => units == "e";
        public static bool IsMph(string units) => units == "e" || units == "h";
        public static bool IsMetersPerSecond(string units) => units == "s";

        public static int? TemperatureFromMetric(double? celsius, string units)
        {
            if (celsius == null) return null;
            return (int) Math.Round(IsFahrenheit(units) ? celsius.Value * 9 / 5 + 32 : celsius.Value);
        }

        public static double? PrecipFromMetric(double? mm, string units)
        {
            if (mm == null) return null;
            return IsInches(units) ? Math.Round(mm.Value / 25.4, 2) : Math.Round(mm.Value, 1);
        }

        public static double? SnowFromMetric(double? cm, string units)
        {
            if (cm == null) return null;
            return IsInches(units) ? Math.Round(cm.Value / 2.54, 1) : Math.Round(cm.Value, 1);
        }

        // Pressure: inches of mercury for "e", hPa otherwise.
        public static double? PressureFromMetric(double? hpa, string units)
        {
            if (hpa == null) return null;
            return units == "e" ? Math.Round(hpa.Value * 0.0295300, 2) : Math.Round(hpa.Value, 1);
        }

        public static int? WindFromMetric(double? kmh, string units)
        {
            if (kmh == null) return null;
            if (IsMph(units)) return (int) Math.Round(kmh.Value / 1.609344);
            if (IsMetersPerSecond(units)) return (int) Math.Round(kmh.Value / 3.6);
            return (int) Math.Round(kmh.Value);
        }

        public static double? ConvertPrecip(double? value, string from, string to)
        {
            if (value == null || IsInches(from) == IsInches(to)) return value;
            return IsInches(from) ? Math.Round(value.Value * 25.4, 1) : Math.Round(value.Value / 25.4, 2);
        }

        public static double? ConvertSnow(double? value, string from, string to)
        {
            if (value == null || IsInches(from) == IsInches(to)) return value;
            return IsInches(from) ? Math.Round(value.Value * 2.54, 1) : Math.Round(value.Value / 2.54, 1);
        }

        public static int? ConvertWind(int? value, string from, string to)
        {
            if (value == null || from == to) return value;
            var kmh = IsMph(from) ? value.Value * 1.609344 : IsMetersPerSecond(from) ? value.Value * 3.6 : value.Value;
            return WindFromMetric(kmh, to);
        }
    }

    /// <summary>
    /// WMO weather interpretation codes, as the Open-Meteo archive reports them, mapped to the
    /// weather.com icon codes the app already has icons for, and to a short phrase.
    /// </summary>
    public static class WmoWeather
    {
        public static int? IconCode(int? code) => code switch
        {
            null => null,
            0 => 32,
            1 => 34,
            2 => 30,
            3 => 26,
            45 or 48 => 20,
            51 or 53 or 55 => 9,
            56 or 57 => 8,
            61 => 11,
            63 or 65 => 12,
            66 or 67 => 10,
            71 => 14,
            73 or 75 => 16,
            77 => 13,
            80 => 39,
            81 => 11,
            82 => 12,
            85 => 41,
            86 => 16,
            95 => 4,
            96 or 99 => 17,
            _ => 44
        };

        public static string? Phrase(int? code)
        {
            var key = code switch
            {
                null => null,
                0 => "HistoryWmoClear",
                1 => "HistoryWmoMainlyClear",
                2 => "HistoryWmoPartlyCloudy",
                3 => "HistoryWmoOvercast",
                45 or 48 => "HistoryWmoFog",
                51 or 53 or 55 or 56 or 57 => "HistoryWmoDrizzle",
                61 or 63 or 65 or 66 or 67 => "HistoryWmoRain",
                71 or 73 or 75 or 77 => "HistoryWmoSnow",
                80 or 81 or 82 => "HistoryWmoShowers",
                85 or 86 => "HistoryWmoSnowShowers",
                95 or 96 or 99 => "HistoryWmoThunderstorm",
                _ => "HistoryWmoUnknown"
            };
            return key?.GetLocalized();
        }
    }
}
