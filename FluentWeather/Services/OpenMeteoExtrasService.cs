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
    /// <summary>The extra Open-Meteo data feeds. Each one has a main page chip and tray lines.</summary>
    public enum ExtraFeed
    {
        Marine,
        Wind,
        Models,
        River,
        Pollen
    }

    /// <summary>
    /// Local build: extra data from the free Open-Meteo APIs (open-meteo.com, CC BY 4.0, no key):
    ///   Marine: waves, swell and sea surface temperature (marine-api.open-meteo.com), oceans worldwide.
    ///   Wind: the highest gust of the next 6 hours, the model gust now, and the wind at 80 m.
    ///   Models: today's high, low and precipitation from the ECMWF, GFS and ICON weather models.
    ///   River: today's river discharge and the usual discharge for this day (flood-api.open-meteo.com).
    ///   Pollen: the weather.com tree, grass and ragweed levels where weather.com has them (the US and
    ///   some other countries), otherwise grains per cubic meter of six pollen types from Open-Meteo,
    ///   Europe only (air-quality-api.open-meteo.com).
    /// Each feed is requested only when the main page, the tray or the water card needs it. Each
    /// location and feed is requested at most once an hour, because the values change hourly.
    /// </summary>
    public static class OpenMeteoExtrasService
    {
        private static readonly TimeSpan CacheTime = TimeSpan.FromMinutes(60);
        private static readonly TimeSpan EmptyCacheTime = TimeSpan.FromMinutes(5);
        private static readonly HttpClient Forecast = ApiUtils.CreateClient("https://api.open-meteo.com/");
        private static readonly HttpClient Marine = ApiUtils.CreateClient("https://marine-api.open-meteo.com/");
        private static readonly HttpClient Flood = ApiUtils.CreateClient("https://flood-api.open-meteo.com/");
        private static readonly HttpClient AirQuality = ApiUtils.CreateClient("https://air-quality-api.open-meteo.com/");

        private static readonly Dictionary<string, (DateTime FetchedUtc, object? Data)> Cache = new();
        private static readonly SemaphoreSlim Lock = new(1, 1);

        public static readonly ExtraFeed[] AllFeeds = [ExtraFeed.Marine, ExtraFeed.Wind, ExtraFeed.Models, ExtraFeed.River, ExtraFeed.Pollen];

        // --- Settings: "extra.Marine.main", "extra.Marine.tray" and so on. All off by default. ---

        public static string SettingKey(ExtraFeed feed, bool tray) => "extra." + feed + (tray ? ".tray" : ".main");

        public static async Task<bool> IsOnAsync(ExtraFeed feed, bool tray) =>
            await ApplicationData.Current.LocalSettings.ReadAsync<bool>(SettingKey(feed, tray));

        public static async Task SetAsync(ExtraFeed feed, bool tray, bool on) =>
            await ApplicationData.Current.LocalSettings.SaveAsync(SettingKey(feed, tray), on);

        // --- weather.com pollen ---
        // The weather requests of the main page and the tray already carry the weather.com pollen
        // forecast. RememberWeatherComPollen keeps it by location for 3 hours, for the Pollen card.
        private static readonly Dictionary<string, (DateTime StoredUtc, WeatherComPollen Pollen)> WeatherComPollenByPlace = new();
        private static readonly TimeSpan WeatherComPollenAge = TimeSpan.FromHours(3);

        private static string PlaceKey(double lat, double lon)
        {
            var ci = CultureInfo.InvariantCulture;
            return lat.ToString("0.00", ci) + "," + lon.ToString("0.00", ci);
        }

        public static void RememberWeatherComPollen(double lat, double lon, WeatherComPollen? pollen)
        {
            if (pollen == null || !HasWeatherComPollen(pollen))
                return;
            lock (WeatherComPollenByPlace)
                WeatherComPollenByPlace[PlaceKey(lat, lon)] = (DateTime.UtcNow, pollen);
        }

        /// <summary>The same data from the main request, which has its own model with fixed fields.</summary>
        public static void RememberWeatherComPollen(double lat, double lon, PollenForecastDaypart? part)
        {
            if (part == null)
                return;
            RememberWeatherComPollen(lat, lon, new WeatherComPollen
            {
                daypartName = part.daypartName?.Select(x => (string?) x).ToList(),
                treePollenIndex = part.treePollenIndex?.Select(x => (int?) x).ToList(),
                treePollenCategory = part.treePollenCategory?.Select(x => (string?) x).ToList(),
                grassPollenIndex = part.grassPollenIndex?.Select(x => (int?) x).ToList(),
                grassPollenCategory = part.grassPollenCategory?.Select(x => (string?) x).ToList(),
                ragweedPollenIndex = part.ragweedPollenIndex?.Select(x => (int?) x).ToList(),
                ragweedPollenCategory = part.ragweedPollenCategory?.Select(x => (string?) x).ToList()
            });
        }

        private static bool HasWeatherComPollen(WeatherComPollen p) =>
            At(p.treePollenIndex, 0) != null || At(p.grassPollenIndex, 0) != null || At(p.ragweedPollenIndex, 0) != null;

        private static WeatherComPollen? GetWeatherComPollen(double lat, double lon)
        {
            lock (WeatherComPollenByPlace)
            {
                if (WeatherComPollenByPlace.TryGetValue(PlaceKey(lat, lon), out var stored) &&
                    DateTime.UtcNow - stored.StoredUtc < WeatherComPollenAge)
                    return stored.Pollen;
            }
            return null;
        }

        private static T? At<T>(List<T?>? list, int index) where T : struct =>
            list != null && index < list.Count ? list[index] : null;

        private static string? AtText(List<string?>? list, int index) =>
            list != null && index < list.Count ? list[index] : null;

        // "Tree high, grass low, ragweed none" for one 12-hour part.
        private static string WeatherComPart(WeatherComPollen p, int i)
        {
            var parts = new List<string>();
            void Add(string nameKey, List<int?>? index, List<string?>? category)
            {
                var value = At(index, i);
                if (value == null)
                    return;
                var text = AtText(category, i);
                parts.Add(nameKey.GetLocalized() + " " + (string.IsNullOrEmpty(text) ? WeatherComLevel(value.Value) : text!.ToLower(CultureInfo.CurrentCulture)));
            }
            Add("PollenTree", p.treePollenIndex, p.treePollenCategory);
            Add("PollenGrass", p.grassPollenIndex, p.grassPollenCategory);
            Add("PollenRagweed", p.ragweedPollenIndex, p.ragweedPollenCategory);
            return string.Join(", ", parts);
        }

        private static string WeatherComLevel(int index) =>
            (index <= 0 ? "PollenNone" : index == 1 ? "PollenLow" : index == 2 ? "PollenModerate" : index == 3 ? "PollenHigh" : "PollenVeryHigh").GetLocalized();

        private static (string Value, string Tip, string TrayLine)? DescribeWeatherComPollen(WeatherComPollen p)
        {
            // The card shows the type with the highest level now, or "None".
            var now = new List<(string Name, int Index, string? Category)>
            {
                ("PollenTree".GetLocalized(), At(p.treePollenIndex, 0) ?? -1, AtText(p.treePollenCategory, 0)),
                ("PollenGrass".GetLocalized(), At(p.grassPollenIndex, 0) ?? -1, AtText(p.grassPollenCategory, 0)),
                ("PollenRagweed".GetLocalized(), At(p.ragweedPollenIndex, 0) ?? -1, AtText(p.ragweedPollenCategory, 0))
            }.Where(x => x.Index >= 0).OrderByDescending(x => x.Index).ToList();
            if (now.Count == 0)
                return null;

            var top = now[0];
            var level = string.IsNullOrEmpty(top.Category) ? WeatherComLevel(top.Index) : top.Category!.ToLower(CultureInfo.CurrentCulture);
            var value = top.Index <= 0 ? "PollenNone".GetLocalized() : top.Name + " " + level;

            var tip = "ExtraPollenUsTip".GetLocalized();
            for (var i = 0; i < 3; i++)
            {
                var line = WeatherComPart(p, i);
                if (line.Length == 0)
                    break;
                var name = AtText(p.daypartName, i);
                tip += "\n" + (string.IsNullOrEmpty(name) ? "" : name + ": ") + line;
            }
            tip += "\n\n" + "ExtraPollenUsCredit".GetLocalized();
            return (value, tip, WeatherComPart(p, 0));
        }

        // --- Data ---

        public static Task<MarineData?> GetMarineAsync(double lat, double lon) =>
            GetCachedAsync(ExtraFeed.Marine, lat, lon, () => FetchMarineAsync(lat, lon));

        public static Task<WindDetailData?> GetWindAsync(double lat, double lon) =>
            GetCachedAsync(ExtraFeed.Wind, lat, lon, () => FetchWindAsync(lat, lon));

        public static Task<List<ModelDay>?> GetModelsAsync(double lat, double lon) =>
            GetCachedAsync(ExtraFeed.Models, lat, lon, () => FetchModelsAsync(lat, lon));

        public static Task<RiverData?> GetRiverAsync(double lat, double lon) =>
            GetCachedAsync(ExtraFeed.River, lat, lon, () => FetchRiverAsync(lat, lon));

        public static Task<PollenData?> GetPollenAsync(double lat, double lon) =>
            GetCachedAsync(ExtraFeed.Pollen, lat, lon, () => FetchPollenAsync(lat, lon));

        private static async Task<T?> GetCachedAsync<T>(ExtraFeed feed, double lat, double lon, Func<Task<T?>> fetch) where T : class
        {
            var ci = CultureInfo.InvariantCulture;
            var key = feed + "|" + lat.ToString("0.00", ci) + "," + lon.ToString("0.00", ci);

            await Lock.WaitAsync();
            try
            {
                // An empty result (no data, or a failed request, for example right after the PC wakes
                // up) is tried again after EmptyCacheTime instead of CacheTime.
                if (Cache.TryGetValue(key, out var cached) &&
                    DateTime.UtcNow - cached.FetchedUtc < (cached.Data == null ? EmptyCacheTime : CacheTime))
                    return cached.Data as T;
            }
            finally
            {
                Lock.Release();
            }

            T? data = null;
            try
            {
                data = await fetch();
            }
            catch (Exception)
            {
                // No data. The chip hides until the next try.
            }

            await Lock.WaitAsync();
            try
            {
                Cache[key] = (DateTime.UtcNow, data);
            }
            finally
            {
                Lock.Release();
            }

            return data;
        }

        private static string Point(double lat, double lon)
        {
            var ci = CultureInfo.InvariantCulture;
            return "latitude=" + lat.ToString("0.0000", ci) + "&longitude=" + lon.ToString("0.0000", ci);
        }

        private static async Task<string?> GetJsonAsync(HttpClient client, string url)
        {
            using var response = await ApiUtils.GetWithRetryAsync(client, url);
            if (!response.IsSuccessStatusCode)
                return null;
            return await response.Content.ReadAsStringAsync();
        }

        private static async Task<MarineData?> FetchMarineAsync(double lat, double lon)
        {
            var json = await GetJsonAsync(Marine, "v1/marine?" + Point(lat, lon) +
                "&current=wave_height,wave_direction,wave_period,swell_wave_height,swell_wave_period,swell_wave_direction,sea_surface_temperature&timezone=auto");
            if (json == null)
                return null;
            var c = JsonSerializer.Deserialize(json, FluentWeatherJsonContext.Default.MarineResponse)?.Current;
            if (c == null || (c.WaveHeight == null && c.SeaSurfaceTemperature == null))
                return null;
            return new MarineData
            {
                WaveHeightM = c.WaveHeight,
                WavePeriodS = c.WavePeriod,
                WaveDirection = c.WaveDirection,
                SwellHeightM = c.SwellHeight,
                SwellPeriodS = c.SwellPeriod,
                SwellDirection = c.SwellDirection,
                SeaTemperatureC = c.SeaSurfaceTemperature
            };
        }

        private static async Task<WindDetailData?> FetchWindAsync(double lat, double lon)
        {
            var json = await GetJsonAsync(Forecast, "v1/forecast?" + Point(lat, lon) +
                "&current=wind_speed_10m,wind_gusts_10m,wind_direction_10m&hourly=wind_speed_80m,wind_gusts_10m&forecast_hours=6&timezone=auto");
            if (json == null)
                return null;
            var r = JsonSerializer.Deserialize(json, FluentWeatherJsonContext.Default.WindDetailResponse);
            if (r?.Current == null)
                return null;
            return new WindDetailData
            {
                SpeedKmh = r.Current.WindSpeed,
                GustKmh = r.Current.WindGusts,
                Direction = r.Current.WindDirection,
                Speed80mKmh = r.Hourly?.WindSpeed80m?.FirstOrDefault(v => v != null),
                MaxGustNext6hKmh = r.Hourly?.WindGusts?.Where(v => v != null).DefaultIfEmpty(null).Max()
            };
        }

        private static async Task<List<ModelDay>?> FetchModelsAsync(double lat, double lon)
        {
            var json = await GetJsonAsync(Forecast, "v1/forecast?" + Point(lat, lon) +
                "&daily=temperature_2m_max,temperature_2m_min,precipitation_sum&models=ecmwf_ifs025,gfs_seamless,icon_seamless&forecast_days=1&timezone=auto");
            if (json == null)
                return null;
            var d = JsonSerializer.Deserialize(json, FluentWeatherJsonContext.Default.ModelsResponse)?.Daily;
            if (d == null)
                return null;
            static double? First(List<double?>? list) => list != null && list.Count > 0 ? list[0] : null;
            var result = new List<ModelDay>
            {
                new() { Name = "ECMWF", MaxC = First(d.EcmwfMax), MinC = First(d.EcmwfMin), PrecipMm = First(d.EcmwfPrecip) },
                new() { Name = "GFS", MaxC = First(d.GfsMax), MinC = First(d.GfsMin), PrecipMm = First(d.GfsPrecip) },
                new() { Name = "ICON", MaxC = First(d.IconMax), MinC = First(d.IconMin), PrecipMm = First(d.IconPrecip) }
            }.Where(m => m.MaxC != null && m.MinC != null).ToList();
            return result.Count > 0 ? result : null;
        }

        private static async Task<RiverData?> FetchRiverAsync(double lat, double lon)
        {
            var json = await GetJsonAsync(Flood, "v1/flood?" + Point(lat, lon) +
                "&daily=river_discharge,river_discharge_median&forecast_days=1");
            if (json == null)
                return null;
            var d = JsonSerializer.Deserialize(json, FluentWeatherJsonContext.Default.FloodResponse)?.Daily;
            var discharge = d?.Discharge?.FirstOrDefault();
            if (discharge == null)
                return null;
            return new RiverData { DischargeM3s = discharge.Value, MedianM3s = d!.Median?.FirstOrDefault() };
        }

        private static async Task<PollenData?> FetchPollenAsync(double lat, double lon)
        {
            var json = await GetJsonAsync(AirQuality, "v1/air-quality?" + Point(lat, lon) +
                "&current=alder_pollen,birch_pollen,grass_pollen,mugwort_pollen,olive_pollen,ragweed_pollen");
            if (json == null)
                return null;
            var c = JsonSerializer.Deserialize(json, FluentWeatherJsonContext.Default.PollenResponse)?.Current;
            if (c == null)
                return null;
            var all = new List<(string Name, double? Count)>
            {
                ("PollenAlder".GetLocalized(), c.Alder), ("PollenBirch".GetLocalized(), c.Birch),
                ("PollenGrass".GetLocalized(), c.Grass), ("PollenMugwort".GetLocalized(), c.Mugwort),
                ("PollenOlive".GetLocalized(), c.Olive), ("PollenRagweed".GetLocalized(), c.Ragweed)
            };
            if (all.All(p => p.Count == null))
                return null; // outside Europe
            var present = all.Where(p => p.Count != null).Select(p => (p.Name, p.Count!.Value)).OrderByDescending(p => p.Value).ToList();
            return new PollenData { Highest = present[0].Name, HighestCount = present[0].Value, All = present.Where(p => p.Value > 0).ToList() };
        }

        // --- Text for the chips, tooltips and tray lines ---

        public static string Degrees(WetUnits units) => "°" + MeasureUnitUtils.GetTemperatureUnits(units);

        public static string Wind(double kmh, WetUnits units) =>
            WaterService.WindFor(kmh, units) + " " + MeasureUnitUtils.GetWindSpeedUnits(units);

        public static string Height(double meters, WetUnits units) => WaterService.WaveHeightFor(meters, units);

        public static string Temperature(double celsius, WetUnits units) => WaterService.TemperatureFor(celsius, units) + Degrees(units);

        public static string Direction(double? degrees) => degrees == null ? "" : " " + WaterService.Cardinal(degrees.Value);

        public static string Period(double? seconds) =>
            seconds == null ? "" : " " + string.Format(CultureInfo.CurrentCulture, "ExtraPeriod".GetLocalized(), Math.Round(seconds.Value));

        public static string Discharge(double m3s, WetUnits units) =>
            units == WetUnits.e
                ? (m3s * 35.3147).ToString(m3s * 35.3147 < 10 ? "0.#" : "#,0", CultureInfo.CurrentCulture) + " ft³/s"
                : m3s.ToString(m3s < 10 ? "0.##" : "#,0", CultureInfo.CurrentCulture) + " m³/s";

        public static string PollenLevel(double count) =>
            (count <= 0 ? "PollenNone" : count < 10 ? "PollenLow" : count < 50 ? "PollenModerate" : count < 200 ? "PollenHigh" : "PollenVeryHigh").GetLocalized();

        /// <summary>The chip value, the tooltip and the tray line of a feed. Null when there is nothing to show.</summary>
        public static async Task<(string Value, string Tip, string TrayLine)?> DescribeAsync(ExtraFeed feed, double lat, double lon, WetUnits units)
        {
            var credit = "\n\n" + "ExtraCredit".GetLocalized();
            switch (feed)
            {
                case ExtraFeed.Marine:
                {
                    var m = await GetMarineAsync(lat, lon);
                    if (m == null || (!m.HasWaves && m.SeaTemperatureC == null))
                        return null;
                    var value = m.HasWaves ? Height(m.WaveHeightM!.Value, units) + Period(m.WavePeriodS) : Temperature(m.SeaTemperatureC!.Value, units);
                    var tip = "ExtraMarineTip".GetLocalized();
                    if (m.HasWaves)
                        tip += "\n" + "ExtraWaves".GetLocalized() + ": " + Height(m.WaveHeightM!.Value, units) + Period(m.WavePeriodS) + Direction(m.WaveDirection);
                    if (m.SwellHeightM != null)
                        tip += "\n" + "ExtraSwell".GetLocalized() + ": " + Height(m.SwellHeightM.Value, units) + Period(m.SwellPeriodS) + Direction(m.SwellDirection);
                    if (m.SeaTemperatureC != null)
                        tip += "\n" + "ExtraSeaTemp".GetLocalized() + ": " + Temperature(m.SeaTemperatureC.Value, units);
                    var tray = value + (m.HasWaves && m.SeaTemperatureC != null ? ", " + "ExtraSeaShort".GetLocalized() + " " + Temperature(m.SeaTemperatureC.Value, units) : "");
                    return (value, tip + credit, tray);
                }
                case ExtraFeed.Wind:
                {
                    // A forecast, not the current wind: the Wind card has the current wind from
                    // weather.com, and the Nearby water card has the wind measured at the water.
                    var w = await GetWindAsync(lat, lon);
                    var peak = w?.MaxGustNext6hKmh ?? w?.GustKmh;
                    if (w == null || peak == null)
                        return null;
                    var value = string.Format(CultureInfo.CurrentCulture, "ExtraWindForecastValue".GetLocalized(), Wind(peak.Value, units));
                    var tip = "ExtraWindTip".GetLocalized();
                    if (w.MaxGustNext6hKmh != null)
                        tip += "\n" + "ExtraGusts6h".GetLocalized() + ": " + Wind(w.MaxGustNext6hKmh.Value, units);
                    if (w.GustKmh != null)
                        tip += "\n" + "ExtraGustsModelNow".GetLocalized() + ": " + Wind(w.GustKmh.Value, units);
                    if (w.Speed80mKmh != null)
                        tip += "\n" + "ExtraWind80m".GetLocalized() + ": " + Wind(w.Speed80mKmh.Value, units);
                    tip += "\n" + "ExtraWindWhere".GetLocalized();
                    var tray = value + (w.Speed80mKmh != null ? ", 80 m " + Wind(w.Speed80mKmh.Value, units) : "");
                    return (value, tip + credit, tray);
                }
                case ExtraFeed.Models:
                {
                    var list = await GetModelsAsync(lat, lon);
                    if (list == null)
                        return null;
                    string Day(ModelDay m) => m.Name + " " + WaterService.TemperatureFor(m.MaxC!.Value, units) + "° / " + WaterService.TemperatureFor(m.MinC!.Value, units) + "°";
                    var highs = list.Select(m => WaterService.TemperatureFor(m.MaxC!.Value, units)).ToList();
                    var value = highs.Min() == highs.Max() ? highs.Min() + Degrees(units) : highs.Min() + "-" + highs.Max() + Degrees(units);
                    var tip = "ExtraModelsTip".GetLocalized();
                    foreach (var m in list)
                    {
                        tip += "\n" + Day(m);
                        if (m.PrecipMm != null)
                        {
                            var precip = HistoryUnits.PrecipFromMetric(m.PrecipMm, units == WetUnits.e ? "e" : "m");
                            tip += ", " + precip!.Value.ToString("0.##", CultureInfo.CurrentCulture) + " " + MeasureUnitUtils.GetLiquidPrecipitationUnits(units);
                        }
                    }
                    return (value, tip + credit, string.Join(", ", list.Select(Day)));
                }
                case ExtraFeed.River:
                {
                    var r = await GetRiverAsync(lat, lon);
                    if (r == null)
                        return null;
                    var value = Discharge(r.DischargeM3s, units);
                    var percent = r.MedianM3s is > 0 ? (int?) Math.Round(r.DischargeM3s / r.MedianM3s.Value * 100) : null;
                    var tip = "ExtraRiverTip".GetLocalized() + "\n" + "ExtraRiverToday".GetLocalized() + ": " + value;
                    if (r.MedianM3s != null)
                        tip += "\n" + "ExtraRiverMedian".GetLocalized() + ": " + Discharge(r.MedianM3s.Value, units);
                    if (percent != null)
                        tip += "\n" + string.Format(CultureInfo.CurrentCulture, "ExtraRiverPercent".GetLocalized(), percent);
                    var tray = value + (percent != null ? ", " + string.Format(CultureInfo.CurrentCulture, "ExtraRiverPercent".GetLocalized(), percent) : "");
                    return (value, tip + credit, tray);
                }
                case ExtraFeed.Pollen:
                {
                    // weather.com first, where it has pollen. Open-Meteo in Europe otherwise.
                    var us = GetWeatherComPollen(lat, lon);
                    if (us != null)
                    {
                        var described = DescribeWeatherComPollen(us);
                        if (described != null)
                            return described;
                    }

                    var p = await GetPollenAsync(lat, lon);
                    if (p == null)
                        return null;
                    var value = p.HighestCount <= 0 ? PollenLevel(0) : p.Highest + " " + PollenLevel(p.HighestCount);
                    var tip = "ExtraPollenTip".GetLocalized();
                    foreach (var (name, count) in p.All)
                        tip += "\n" + name + ": " + Math.Round(count) + " (" + PollenLevel(count) + ")";
                    if (p.All.Count == 0)
                        tip += "\n" + PollenLevel(0);
                    return (value, tip + credit, value);
                }
            }

            return null;
        }
    }
}
