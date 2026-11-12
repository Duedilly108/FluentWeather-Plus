using FluentWeather.Helpers;
using FluentWeather.Models;
using FluentWeather.Utils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace FluentWeather.Services
{
    /// <summary>
    /// Records the high, the low and the conditions of each saved location, for the "Past days"
    /// cards left of today.
    ///
    /// Two captures per saved location per day, each one small request (current conditions and
    /// the 3-day forecast, the same request the tray icons use):
    ///   the low capture, 60 minutes after the low that the hourly forecast predicts, and
    ///   the high capture, 60 minutes after the predicted high.
    /// The current conditions hold measured values: the lowest temperature of the last 24 hours
    /// and the highest since 7 AM. So a capture that comes late still records the right values.
    /// A background task checks every 15 minutes which captures are due. Data that the app or
    /// the tray icons download anyway also updates the record, without an extra request.
    ///
    /// The records are kept in LocalFolder\pastDays.json for each saved location. The cards show
    /// the two days before today. The history window (HistoryService) shows up to a year, so the
    /// records are kept for 366 days.
    /// </summary>
    public static class PastDaysService
    {
        private const string FileName = "pastDays";
        private const int CaptureDelayMinutes = 60;
        private const int KeepDays = 2;
        private const int RetainDays = 366;

        private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(1);
        private static readonly TimeSpan GapBetweenRequests = TimeSpan.FromSeconds(3);

        private static readonly SemaphoreSlim Lock = new(1, 1);
        private static readonly SemaphoreSlim RunLock = new(1, 1);

        /// <summary>The recorded days before today, oldest first. Temperatures are in the given units.</summary>
        public static async Task<List<PastDayRecord>> GetPastDaysAsync(string placeId, DateTimeOffset locationNow, string units)
        {
            var result = new List<PastDayRecord>();
            if (string.IsNullOrEmpty(placeId))
                return result;

            PastDaysStore store;
            await Lock.WaitAsync();
            try
            {
                store = await LoadUnlockedAsync();
            }
            finally
            {
                Lock.Release();
            }

            for (var back = KeepDays; back >= 1; back--)
            {
                var date = DateKey(locationNow.AddDays(-back));
                var record = store.Records.FirstOrDefault(r => r.PlaceId == placeId && r.Date == date);
                if (record == null || (record.High == null && record.Low == null))
                    continue;

                result.Add(new PastDayRecord
                {
                    PlaceId = record.PlaceId,
                    Date = record.Date,
                    High = ConvertTemperature(record.High, record.Units, units),
                    Low = ConvertTemperature(record.Low, record.Units, units),
                    IconCode = DayIcon(record.IconCode),
                    Phrase = record.Phrase,
                    PrecipChance = record.PrecipChance,
                    Units = units,
                    LowCaptured = record.LowCaptured,
                    HighCaptured = record.HighCaptured
                });
            }

            return result;
        }

        /// <summary>
        /// The recorded days of a location between two dates (yyyy-MM-dd, both included), by date,
        /// converted to the given units. For the history window.
        /// </summary>
        public static async Task<Dictionary<string, PastDayRecord>> GetRecordedDaysAsync(string placeId, string fromDate, string toDate, string units)
        {
            var result = new Dictionary<string, PastDayRecord>();
            if (string.IsNullOrEmpty(placeId))
                return result;

            PastDaysStore store;
            await Lock.WaitAsync();
            try
            {
                store = await LoadUnlockedAsync();
            }
            finally
            {
                Lock.Release();
            }

            foreach (var record in store.Records)
            {
                if (record.PlaceId != placeId ||
                    string.CompareOrdinal(record.Date, fromDate) < 0 ||
                    string.CompareOrdinal(record.Date, toDate) > 0)
                    continue;

                result[record.Date] = new PastDayRecord
                {
                    PlaceId = record.PlaceId,
                    Date = record.Date,
                    High = ConvertTemperature(record.High, record.Units, units),
                    Low = ConvertTemperature(record.Low, record.Units, units),
                    IconCode = DayIcon(record.IconCode),
                    Phrase = record.Phrase,
                    PrecipChance = record.PrecipChance,
                    Precip = HistoryUnits.ConvertPrecip(record.Precip, record.Units, units),
                    Snow = HistoryUnits.ConvertSnow(record.Snow, record.Units, units),
                    WindMax = HistoryUnits.ConvertWind(record.WindMax, record.Units, units),
                    GustMax = HistoryUnits.ConvertWind(record.GustMax, record.Units, units),
                    Units = units,
                    LowCaptured = record.LowCaptured,
                    HighCaptured = record.HighCaptured
                };
            }

            return result;
        }

        /// <summary>Remembers the coordinates of a saved location, for the history archive.</summary>
        public static async Task SetCoordinatesAsync(string placeId, double latitude, double longitude)
        {
            if (string.IsNullOrEmpty(placeId))
                return;

            await Lock.WaitAsync();
            try
            {
                var store = await LoadUnlockedAsync();
                var schedule = GetOrAddSchedule(store, placeId);
                if (schedule.HasCoordinates && Math.Abs(schedule.Latitude - latitude) < 0.0001 && Math.Abs(schedule.Longitude - longitude) < 0.0001)
                    return;

                schedule.Latitude = latitude;
                schedule.Longitude = longitude;
                schedule.HasCoordinates = true;
                await SaveUnlockedAsync(store);
            }
            catch (Exception)
            {
                // The history window then shows recorded days only.
            }
            finally
            {
                Lock.Release();
            }
        }

        /// <summary>The current time at a location, from its last known UTC offset, or the PC time.</summary>
        public static async Task<DateTimeOffset> GetLocationNowAsync(string placeId)
        {
            await Lock.WaitAsync();
            try
            {
                var store = await LoadUnlockedAsync();
                var schedule = store.Schedules.FirstOrDefault(s => s.PlaceId == placeId);
                if (schedule is { OffsetKnown: true })
                    return DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromMinutes(schedule.UtcOffsetMinutes));
                return DateTimeOffset.Now;
            }
            catch (Exception)
            {
                return DateTimeOffset.Now;
            }
            finally
            {
                Lock.Release();
            }
        }

        public static async Task<(double Latitude, double Longitude)?> GetCoordinatesAsync(string placeId)
        {
            await Lock.WaitAsync();
            try
            {
                var store = await LoadUnlockedAsync();
                var schedule = store.Schedules.FirstOrDefault(s => s.PlaceId == placeId);
                if (schedule is { HasCoordinates: true })
                    return (schedule.Latitude, schedule.Longitude);
                return null;
            }
            finally
            {
                Lock.Release();
            }
        }

        /// <summary>
        /// Sets the capture times of a location from its hourly forecast: the hours of the lowest and
        /// the highest temperature in the next 24 hours.
        /// </summary>
        public static async Task UpdateScheduleAsync(string placeId, V3WxForecastHourly? hourly, TimeSpan utcOffset)
        {
            if (hourly == null || string.IsNullOrEmpty(placeId))
                return;

            var count = Math.Min(24, Math.Min(hourly.temperature.Count, hourly.validTimeLocal.Count));
            if (count < 6)
                return;

            int lowIndex = 0, highIndex = 0;
            for (var i = 1; i < count; i++)
            {
                if (hourly.temperature[i] < hourly.temperature[lowIndex]) lowIndex = i;
                if (hourly.temperature[i] > hourly.temperature[highIndex]) highIndex = i;
            }

            var low = hourly.validTimeLocal[lowIndex];
            var high = hourly.validTimeLocal[highIndex];

            // The low capture needs the night behind it, and the high capture needs the
            // "since 7 AM" value. Both captures stay before midnight.
            var lowMinutes = Math.Clamp(low.Hour * 60 + low.Minute, 4 * 60, 22 * 60);
            var highMinutes = Math.Clamp(high.Hour * 60 + high.Minute, 9 * 60, 22 * 60);

            await Lock.WaitAsync();
            try
            {
                var store = await LoadUnlockedAsync();
                var schedule = GetOrAddSchedule(store, placeId);
                if (schedule.LowMinutes == lowMinutes && schedule.HighMinutes == highMinutes &&
                    schedule.OffsetKnown && schedule.UtcOffsetMinutes == (int) utcOffset.TotalMinutes)
                    return;

                schedule.LowMinutes = lowMinutes;
                schedule.HighMinutes = highMinutes;
                schedule.UtcOffsetMinutes = (int) utcOffset.TotalMinutes;
                schedule.OffsetKnown = true;
                await SaveUnlockedAsync(store);
            }
            catch (Exception)
            {
                // The default capture times stay.
            }
            finally
            {
                Lock.Release();
            }
        }

        /// <summary>
        /// Updates today's record of a saved location from data that was downloaded anyway, or from a
        /// capture. Only a capture marks the low or the high as final.
        /// </summary>
        public static async Task ObserveAsync(string placeId, V3WxObservationsCurrent? observation,
            IReadOnlyList<int?>? iconCodes, IReadOnlyList<string?>? phrases, IReadOnlyList<int?>? precipChances,
            string units, bool lowCapture = false, bool highCapture = false)
        {
            if (observation == null || string.IsNullOrEmpty(placeId))
                return;

            await Lock.WaitAsync();
            try
            {
                var store = await LoadUnlockedAsync();
                var local = observation.validTimeLocal;
                var date = DateKey(local);

                var record = store.Records.FirstOrDefault(r => r.PlaceId == placeId && r.Date == date);
                if (record == null)
                {
                    record = new PastDayRecord { PlaceId = placeId, Date = date, Units = units };
                    store.Records.Add(record);
                }

                var before = Fingerprint(record);

                if (record.Units != units)
                {
                    record.High = ConvertTemperature(record.High, record.Units, units);
                    record.Low = ConvertTemperature(record.Low, record.Units, units);
                    record.Precip = HistoryUnits.ConvertPrecip(record.Precip, record.Units, units);
                    record.Snow = HistoryUnits.ConvertSnow(record.Snow, record.Units, units);
                    record.WindMax = HistoryUnits.ConvertWind(record.WindMax, record.Units, units);
                    record.GustMax = HistoryUnits.ConvertWind(record.GustMax, record.Units, units);
                    record.Units = units;
                }

                // The totals of the day, for the history window. The 24-hour totals grow through the
                // day, so the largest value seen is kept. The wind is the highest speed seen.
                if (local.Hour >= 4)
                {
                    record.Precip = record.Precip == null ? observation.precip24Hour : Math.Max(record.Precip.Value, observation.precip24Hour);
                    record.Snow = record.Snow == null ? observation.snow24Hour : Math.Max(record.Snow.Value, observation.snow24Hour);
                }
                record.WindMax = record.WindMax == null ? observation.windSpeed : Math.Max(record.WindMax.Value, observation.windSpeed);
                if (observation.windGust != null)
                    record.GustMax = record.GustMax == null ? observation.windGust : Math.Max(record.GustMax.Value, observation.windGust.Value);

                // The highest temperature since 7 AM belongs to today only after 7 AM.
                if (local.Hour >= 8)
                {
                    record.High = record.High == null
                        ? observation.temperatureMaxSince7Am
                        : Math.Max(record.High.Value, observation.temperatureMaxSince7Am);
                    if (highCapture)
                        record.HighCaptured = true;
                }

                // The lowest temperature of the last 24 hours includes the night. It is updated until
                // the low capture makes it final.
                if (local.Hour >= 4 && !record.LowCaptured)
                {
                    record.Low = observation.temperatureMin24Hour;
                    if (lowCapture)
                        record.LowCaptured = true;
                }

                // The conditions of the day: the day part of the forecast, or the night part when the
                // day part has passed and nothing was recorded yet.
                var dayIcon = iconCodes is { Count: > 0 } ? iconCodes[0] : null;
                if (dayIcon != null)
                {
                    record.IconCode = dayIcon;
                    record.Phrase = phrases is { Count: > 0 } ? phrases[0] : record.Phrase;
                    record.PrecipChance = precipChances is { Count: > 0 } ? precipChances[0] : record.PrecipChance;
                }
                else if (record.IconCode == null && iconCodes is { Count: > 1 })
                {
                    record.IconCode = DayIcon(iconCodes[1]);
                    record.Phrase = phrases is { Count: > 1 } ? phrases[1] : null;
                    record.PrecipChance = precipChances is { Count: > 1 } ? precipChances[1] : null;
                }

                var schedule = GetOrAddSchedule(store, placeId);
                var offsetChanged = !schedule.OffsetKnown || schedule.UtcOffsetMinutes != (int) local.Offset.TotalMinutes;
                schedule.UtcOffsetMinutes = (int) local.Offset.TotalMinutes;
                schedule.OffsetKnown = true;
                if (lowCapture || highCapture)
                    schedule.LastFailedUtcTicks = 0;

                // Keep a year of days for the history window.
                var oldest = DateKey(local.AddDays(-RetainDays));
                var removed = store.Records.RemoveAll(r => r.PlaceId == placeId && string.CompareOrdinal(r.Date, oldest) < 0);

                if (before != Fingerprint(record) || offsetChanged || removed > 0 || lowCapture || highCapture)
                    await SaveUnlockedAsync(store);
            }
            catch (Exception)
            {
                // A failed record only means a missing past day later.
            }
            finally
            {
                Lock.Release();
            }
        }

        /// <summary>
        /// Downloads the captures that are due for the saved locations, one location at a time with a
        /// short gap between requests. Called by the background task every 15 minutes and when the
        /// app shows new data.
        /// </summary>
        public static async Task RunDueCapturesAsync()
        {
            if (!await RunLock.WaitAsync(0))
                return; // a run is already going

            try
            {
                var saved = await SavedLocationsService.GetAllAsync();
                var savedIds = new HashSet<string>(saved.Select(s => s.PlaceId));
                var units = (await VariousUtils.GetUnitsCode()).ToString();

                PastDaysStore store;
                await Lock.WaitAsync();
                try
                {
                    store = await LoadUnlockedAsync();
                    var changed = false;
                    if (store.SpreadMinutes < 0)
                    {
                        store.SpreadMinutes = Random.Shared.Next(0, 21);
                        changed = true;
                    }

                    // Locations that are no longer saved are not kept.
                    changed |= store.Records.RemoveAll(r => !savedIds.Contains(r.PlaceId)) > 0;
                    changed |= store.Schedules.RemoveAll(s => !savedIds.Contains(s.PlaceId)) > 0;
                    if (changed)
                        await SaveUnlockedAsync(store);
                }
                finally
                {
                    Lock.Release();
                }

                var requests = 0;
                foreach (var location in saved)
                {
                    var schedule = store.Schedules.FirstOrDefault(s => s.PlaceId == location.PlaceId)
                                   ?? new PastDaySchedule { PlaceId = location.PlaceId };
                    var nowUtc = DateTimeOffset.UtcNow;

                    if (schedule.LastFailedUtcTicks > 0 && nowUtc.UtcTicks - schedule.LastFailedUtcTicks < RetryAfterFailure.Ticks)
                        continue;

                    var local = schedule.OffsetKnown
                        ? nowUtc.ToOffset(TimeSpan.FromMinutes(schedule.UtcOffsetMinutes))
                        : DateTimeOffset.Now;
                    var date = DateKey(local);
                    var record = store.Records.FirstOrDefault(r => r.PlaceId == location.PlaceId && r.Date == date);
                    var minutes = local.Hour * 60 + local.Minute;
                    var wait = CaptureDelayMinutes + store.SpreadMinutes;

                    var lowDue = record is not { LowCaptured: true } && minutes >= schedule.LowMinutes + wait;
                    var highDue = record is not { HighCaptured: true } && minutes >= schedule.HighMinutes + wait;
                    if (!lowDue && !highDue)
                        continue;

                    if (requests > 0)
                        await Task.Delay(GapBetweenRequests);
                    requests++;

                    try
                    {
                        using var response = await ApiUtils.GetCurrentConditions(location.PlaceId);
                        var json = await response.Content.ReadAsStringAsync();
                        var data = JsonSerializer.Deserialize(json, FluentWeatherJsonContext.Default.CurrentConditionsResponse);
                        var observation = data?.v3wxobservationscurrent;
                        if (observation == null)
                            throw new InvalidOperationException("No current conditions.");

                        var part = data?.v3wxforecastdaily3day?.daypart?.FirstOrDefault();
                        await ObserveAsync(location.PlaceId, observation, part?.iconCode, part?.wxPhraseLong, part?.precipChance,
                            units, lowDue, highDue);
                    }
                    catch (Exception)
                    {
                        await MarkFailedAsync(location.PlaceId);
                    }
                }
            }
            catch (Exception)
            {
                // The next run tries again.
            }
            finally
            {
                RunLock.Release();
            }
        }

        private static async Task MarkFailedAsync(string placeId)
        {
            await Lock.WaitAsync();
            try
            {
                var store = await LoadUnlockedAsync();
                GetOrAddSchedule(store, placeId).LastFailedUtcTicks = DateTimeOffset.UtcNow.UtcTicks;
                await SaveUnlockedAsync(store);
            }
            catch (Exception)
            {
                // The next run tries again.
            }
            finally
            {
                Lock.Release();
            }
        }

        private static PastDaySchedule GetOrAddSchedule(PastDaysStore store, string placeId)
        {
            var schedule = store.Schedules.FirstOrDefault(s => s.PlaceId == placeId);
            if (schedule == null)
            {
                schedule = new PastDaySchedule { PlaceId = placeId };
                store.Schedules.Add(schedule);
            }

            return schedule;
        }

        private static string DateKey(DateTimeOffset local) => local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        private static string Fingerprint(PastDayRecord r) =>
            r.High + "|" + r.Low + "|" + r.IconCode + "|" + r.Phrase + "|" + r.PrecipChance + "|" + r.Units + "|" + r.LowCaptured + "|" + r.HighCaptured +
            "|" + r.Precip + "|" + r.Snow + "|" + r.WindMax + "|" + r.GustMax;

        /// <summary>
        /// The day version of a weather.com night icon code. A day that was first recorded in the
        /// evening only has the night forecast, and a past day card shows the day.
        /// </summary>
        public static int? DayIcon(int? code) => code switch
        {
            27 => 28, // mostly cloudy
            29 => 30, // partly cloudy
            31 => 32, // clear
            33 => 34, // fair
            45 => 39, // scattered showers
            46 => 41, // scattered snow showers
            47 => 38, // scattered thunderstorms
            _ => code
        };

        private static bool IsFahrenheit(string units) => units == "e";

        public static int? ConvertTemperature(int? value, string from, string to)
        {
            if (value == null || IsFahrenheit(from) == IsFahrenheit(to))
                return value;

            return IsFahrenheit(from)
                ? (int) Math.Round((value.Value - 32) * 5 / 9.0)
                : (int) Math.Round(value.Value * 9 / 5.0 + 32);
        }

        private static async Task<PastDaysStore> LoadUnlockedAsync()
        {
            try
            {
                return await ApplicationData.Current.LocalFolder.ReadAsync<PastDaysStore>(FileName) ?? new PastDaysStore();
            }
            catch (Exception)
            {
                return new PastDaysStore();
            }
        }

        private static async Task SaveUnlockedAsync(PastDaysStore store)
        {
            await ApplicationData.Current.LocalFolder.SaveAsync(FileName, store);
        }
    }
}
