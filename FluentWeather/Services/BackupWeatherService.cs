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
    /// Local build: the Open-Meteo backup for weather.com. It builds the same data bundles that
    /// weather.com sends (RootV3Response for the full data, CurrentConditionsResponse for the tray),
    /// in the same units and formats, so every page works unchanged. See DataSourceService for when
    /// it is used.
    ///
    /// Open-Meteo (open-meteo.com, free, no key, CC BY 4.0) gives the current conditions and the
    /// hourly and daily forecast. The app calculates the rest: the 24-hour totals and extremes, the
    /// pressure trend, heat index, wind chill, moon phase and moon times, the day and night parts
    /// with their text, and the running, driving, dry skin and watering indices.
    ///
    /// The nearest airport report (MetarService) gives the cloud ceiling, the station, and the
    /// measured visibility and pressure. When the station is close, it also gives the measured
    /// temperature, dew point, wind and gusts. Without a station the ceiling is CeilingUnknown.
    /// The US pollen levels stay empty, because no free source has them.
    ///
    /// Place names: weather.com place IDs are remembered with their names and coordinates whenever
    /// weather.com answers (RememberFromWeatherCom). Places found by the backup search get IDs of the
    /// form "om-{id}" and are remembered the same way.
    /// </summary>
    public static class BackupWeatherService
    {
        /// <summary>
        /// The cloud ceiling value of the backup when no airport report tells it. The pages show
        /// "not available". weather.com itself sends null when there is no ceiling.
        /// </summary>
        public const int CeilingUnknown = -1;

        private static readonly HttpClient Forecast = ApiUtils.CreateClient("https://api.open-meteo.com/");
        private static readonly HttpClient Geocoding = ApiUtils.CreateClient("https://geocoding-api.open-meteo.com/");
        private static readonly SemaphoreSlim StoreLock = new(1, 1);
        private const string KnownLocationsFile = "knownLocations";
        private const int MaxKnownLocations = 300;
        private static KnownLocationsStore? _known;

        public const string BackupPrefix = "om-";

        // --- Places ---

        /// <summary>Remembers the place in a weather.com reply, by place ID, with its name and coordinates.</summary>
        public static async Task RememberFromWeatherComAsync(string json)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                if (!document.RootElement.TryGetProperty("v3-location-point", out var point) || point.ValueKind != JsonValueKind.Object)
                    return;
                var location = JsonSerializer.Deserialize(point.GetRawText(), FluentWeatherJsonContext.Default.V3LocationPoint)?.LocationV3;
                if (location != null)
                    await RememberAsync(location);
            }
            catch (Exception)
            {
                // Nothing to remember.
            }
        }

        private static async Task RememberAsync(LocationV3 location)
        {
            if (string.IsNullOrEmpty(location.placeId))
                return;
            await StoreLock.WaitAsync();
            try
            {
                var store = await LoadKnownAsync();
                var index = store.Locations.FindIndex(l => l.placeId == location.placeId);
                if (index >= 0)
                {
                    var old = store.Locations[index];
                    if (Math.Abs(old.latitude - location.latitude) < 1e-4 && Math.Abs(old.longitude - location.longitude) < 1e-4 &&
                        old.displayName == location.displayName)
                        return;
                    store.Locations.RemoveAt(index);
                }
                store.Locations.Add(location);
                while (store.Locations.Count > MaxKnownLocations)
                    store.Locations.RemoveAt(0);
                await ApplicationData.Current.LocalFolder.SaveAsync(KnownLocationsFile, store);
            }
            catch (Exception)
            {
                // The backup then has no name for this place until the next time.
            }
            finally
            {
                StoreLock.Release();
            }
        }

        private static async Task<KnownLocationsStore> LoadKnownAsync()
        {
            if (_known != null)
                return _known;
            try
            {
                _known = await ApplicationData.Current.LocalFolder.ReadAsync<KnownLocationsStore>(KnownLocationsFile);
            }
            catch (Exception)
            {
                _known = null;
            }
            _known ??= new KnownLocationsStore();
            return _known;
        }

        public static async Task<LocationV3?> FindLocationAsync(string placeId)
        {
            await StoreLock.WaitAsync();
            try
            {
                var store = await LoadKnownAsync();
                return store.Locations.LastOrDefault(l => l.placeId == placeId);
            }
            finally
            {
                StoreLock.Release();
            }
        }

        /// <summary>The weather.com request parameter for a place: placeid, or geocode for a backup place.</summary>
        public static async Task<string> WeatherComPlaceQueryAsync(string placeId)
        {
            if (placeId.StartsWith(BackupPrefix, StringComparison.Ordinal))
            {
                var location = await FindLocationAsync(placeId);
                if (location != null)
                    return "geocode=" + location.latitude.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                           location.longitude.ToString("0.###", CultureInfo.InvariantCulture);
            }
            return "placeid=" + Uri.EscapeDataString(placeId);
        }

        /// <summary>Location search, in the shape of the weather.com searchflat reply.</summary>
        public static async Task<string> SearchJsonAsync(string query, string language)
        {
            var url = "v1/search?name=" + Uri.EscapeDataString(query) + "&count=10&format=json&language=" +
                      Uri.EscapeDataString(language.Split('-')[0]);
            var json = await GetJsonAsync(Geocoding, url);
            var places = json == null ? null : JsonSerializer.Deserialize(json, FluentWeatherJsonContext.Default.OmGeocodingResponse)?.Results;
            var result = new SearchLocationResponse { location = new List<SearchedLocation>() };
            foreach (var p in places ?? new List<OmPlace>())
            {
                var location = ToLocation(p);
                await RememberAsync(location);
                result.location.Add(new SearchedLocation
                {
                    address = string.Join(", ", new[] { p.Name, p.Admin1, p.Country }.Where(s => !string.IsNullOrEmpty(s))),
                    locale = location.locale,
                    displayName = location.displayName,
                    ianaTimeZone = location.ianaTimeZone,
                    adminDistrict = location.adminDistrict,
                    adminDistrictCode = null!,
                    city = location.city,
                    longitude = location.longitude,
                    postalCode = location.postalCode,
                    latitude = location.latitude,
                    placeId = location.placeId,
                    neighborhood = null!,
                    country = location.country,
                    postalKey = location.postalKey,
                    countryCode = location.countryCode,
                    disputedArea = false,
                    disputedCountries = null!,
                    disputedCountryCodes = null!,
                    disputedCustomers = null!,
                    disputedShowCountry = new List<bool> { false },
                    iataCode = "",
                    icaoCode = "",
                    locId = "",
                    locationCategory = null!,
                    pwsId = "",
                    type = "city"
                });
            }
            return JsonSerializer.Serialize(result, FluentWeatherJsonContext.Default.SearchLocationResponse);
        }

        private static LocationV3 ToLocation(OmPlace p) => new()
        {
            latitude = p.Latitude,
            longitude = p.Longitude,
            city = p.Name ?? "",
            locale = new Locale { locale1 = p.Admin2 ?? "", locale2 = p.Name ?? "", locale3 = null!, locale4 = null! },
            neighborhood = null!,
            adminDistrict = p.Admin1 ?? "",
            adminDistrictCode = "",
            postalCode = p.Postcodes?.FirstOrDefault() ?? "",
            postalKey = "",
            country = p.Country ?? "",
            countryCode = p.CountryCode ?? "",
            ianaTimeZone = p.Timezone ?? "",
            displayName = p.Name ?? "",
            dstEnd = null,
            dstStart = null,
            dmaCd = "",
            placeId = BackupPrefix + p.Id.ToString(CultureInfo.InvariantCulture),
            disputedArea = false,
            disputedCountries = null!,
            disputedCountryCodes = null!,
            disputedCustomers = null!,
            disputedShowCountry = new List<bool> { false },
            canonicalCityId = "",
            countyId = "",
            locId = "",
            locationCategory = null!,
            pollenId = "",
            pwsId = "",
            regionalSatellite = "",
            tideId = null!,
            type = "city",
            zoneId = ""
        };

        // --- Bundles ---

        /// <summary>The full data of a place, in the shape of the weather.com aggcommon reply.</summary>
        public static async Task<string?> FullDataJsonAsync(string placeId, string units)
        {
            var location = await FindLocationAsync(placeId);
            if (location == null)
                return null;
            var station = MetarService.NearestAsync(location.latitude, location.longitude);
            var om = await GetForecastAsync(location.latitude, location.longitude, units, 16);
            if (om == null)
                return null;

            return JsonSerializer.Serialize(BuildFull(om, units, location, placeId, await station), FluentWeatherJsonContext.Default.RootV3Response);
        }

        /// <summary>The full data built from an Open-Meteo reply. Separate from the download, so it can be tested.</summary>
        public static RootV3Response BuildFull(OmForecastResponse om, string units, LocationV3 location, string placeId,
            MetarReport? station = null)
        {
            var built = new Builder(om, units, location, station);
            return new RootV3Response
            {
                id = placeId,
                v3wxobservationscurrent = built.Observation(),
                v3wxforecastdaily15day = built.Daily(15),
                v3wxforecastdaily10day = built.Daily(10),
                v3wxforecasthourly10day = built.Hourly(240),
                v3alertsHeadlines = null!,
                v3locationpoint = new V3LocationPoint { LocationV3 = location },
                v2idxDriveDaypart10days = built.Drive(),
                v2idxRunDaypart10days = built.Run(),
                v2idxPollenDaypart10days = built.Pollen(),
                V2IdxWateringDaypart10days = built.Watering(),
                V2IdxDrySkinDaypart10days = built.DrySkin()
            };
        }

        /// <summary>The small tray data of a place, in the shape of CurrentConditionsResponse.</summary>
        public static async Task<string?> CurrentConditionsJsonAsync(string placeId, string units)
        {
            var location = await FindLocationAsync(placeId);
            if (location == null)
                return null;
            var station = MetarService.NearestAsync(location.latitude, location.longitude);
            var om = await GetForecastAsync(location.latitude, location.longitude, units, 3);
            if (om == null)
                return null;

            var built = new Builder(om, units, location, await station);
            var daily = built.Daily(3);
            var response = new CurrentConditionsResponse
            {
                v3wxobservationscurrent = built.Observation(),
                v3locationpoint = new V3LocationPoint { LocationV3 = location },
                v3wxforecastdaily3day = new MoonTimes
                {
                    moonriseTimeLocal = daily.moonriseTimeLocal,
                    moonsetTimeLocal = daily.moonsetTimeLocal,
                    daypart = new List<DaypartConditions>
                    {
                        new()
                        {
                            iconCode = daily.daypart[0].iconCode,
                            wxPhraseLong = daily.daypart[0].wxPhraseLong,
                            precipChance = daily.daypart[0].precipChance
                        }
                    }
                },
                pollen = null
            };
            return JsonSerializer.Serialize(response, FluentWeatherJsonContext.Default.CurrentConditionsResponse);
        }

        /// <summary>The Open-Meteo request for a place. Public for the test of BuildFull.</summary>
        public static string ForecastUrl(double latitude, double longitude, string units, int days)
        {
            var ci = CultureInfo.InvariantCulture;
            var (temperature, wind, precipitation) = units switch
            {
                "e" => ("fahrenheit", "mph", "inch"),
                "h" => ("celsius", "mph", "mm"),
                "s" => ("celsius", "ms", "mm"),
                _ => ("celsius", "kmh", "mm")
            };
            return "v1/forecast?latitude=" + latitude.ToString("0.0000", ci) + "&longitude=" + longitude.ToString("0.0000", ci) +
                   "&current=temperature_2m,relative_humidity_2m,dew_point_2m,apparent_temperature,is_day,weather_code,cloud_cover," +
                   "pressure_msl,wind_speed_10m,wind_direction_10m,wind_gusts_10m" +
                   "&hourly=temperature_2m,relative_humidity_2m,dew_point_2m,apparent_temperature,precipitation_probability," +
                   "precipitation,snowfall,weather_code,pressure_msl,cloud_cover,visibility,wind_speed_10m,wind_direction_10m," +
                   "wind_gusts_10m,uv_index,is_day" +
                   "&daily=sunrise,sunset&past_days=1&forecast_days=" + days.ToString(ci) + "&timezone=auto" +
                   "&temperature_unit=" + temperature + "&wind_speed_unit=" + wind + "&precipitation_unit=" + precipitation;
        }

        private static async Task<OmForecastResponse?> GetForecastAsync(double latitude, double longitude, string units, int days)
        {
            var json = await GetJsonAsync(Forecast, ForecastUrl(latitude, longitude, units, days));
            return json == null ? null : JsonSerializer.Deserialize(json, FluentWeatherJsonContext.Default.OmForecastResponse);
        }

        private static async Task<string?> GetJsonAsync(HttpClient client, string url)
        {
            try
            {
                using var response = await ApiUtils.GetWithRetryAsync(client, url);
                if (!response.IsSuccessStatusCode)
                    return null;
                return await response.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "BackupWeatherService");
                return null;
            }
        }

        // --- The builder: Open-Meteo data in weather.com shapes ---

        private sealed class Builder
        {
            private readonly OmForecastResponse _om;
            private readonly OmHourly _h;
            private readonly string _units;
            private readonly LocationV3 _location;
            private readonly TimeSpan _offset;
            private readonly DateTime _nowLocal;
            private readonly int _nowIndex;
            private readonly bool _visibilityInFeet;
            private readonly MetarReport? _station;
            private List<Part>? _parts;

            public Builder(OmForecastResponse om, string units, LocationV3 location, MetarReport? station = null)
            {
                _om = om;
                _station = station;
                _h = om.Hourly ?? new OmHourly();
                _units = units;
                _location = location;
                _offset = TimeSpan.FromSeconds(om.UtcOffsetSeconds);
                _nowLocal = ParseLocal(om.Current?.Time) ?? DateTime.UtcNow.Add(_offset);
                var times = _h.Time ?? new List<string>();
                _nowIndex = Math.Max(0, times.FindLastIndex(t => ParseLocal(t) is { } x && x <= _nowLocal));
                _visibilityInFeet = om.HourlyUnits?.Visibility?.Contains("ft") == true;
            }

            private bool Imperial => _units == "e";
            private int Count => _h.Time?.Count ?? 0;
            private DateTime TimeAt(int i) => ParseLocal(_h.Time![i]) ?? _nowLocal;
            private DateTimeOffset Offset(DateTime local) => new(local, _offset);
            private static int Utc(DateTimeOffset t) => (int) t.ToUnixTimeSeconds();
            private int Expire => Utc(Offset(_nowLocal)) + 600;

            // Unit helpers. Open-Meteo already returns temperature, wind and precipitation in the
            // requested units. Pressure comes in hPa and visibility in meters (or feet).
            private double TempF(double t) => Imperial ? t : t * 9 / 5 + 32;
            private double FromF(double f) => Imperial ? f : (f - 32) * 5 / 9;
            private double WindMph(double w) => _units switch { "e" or "h" => w, "s" => w * 2.23694, _ => w / 1.609344 };
            private double PrecipIn(double p) => Imperial ? p : p / 25.4;
            private double FromC(double c) => Imperial ? c * 9 / 5 + 32 : c;
            private double FromKnots(double kt) => _units switch { "e" or "h" => kt * 1.150779, "s" => kt * 0.514444, _ => kt * 1.852 };
            private double HourlyPressure(double hpa) => Imperial ? Math.Round(hpa * 0.0295300, 2) : Math.Round(hpa, 2);
            private double Visibility(double? raw)
            {
                if (raw == null) return Imperial ? 10 : 16.09;
                var meters = _visibilityInFeet ? raw.Value * 0.3048 : raw.Value;
                return Imperial ? Math.Round(Math.Min(meters / 1609.344, 10), 1) : Math.Round(Math.Min(meters / 1000, 16.09), 2);
            }

            private double V(List<double?>? list, int i, double fallback = 0) =>
                list != null && i >= 0 && i < list.Count && list[i] != null ? list[i]!.Value : fallback;

            private int Code(int i) => _h.WeatherCode != null && i < _h.WeatherCode.Count ? _h.WeatherCode[i] ?? 0 : 0;
            private bool IsDay(int i) => _h.IsDay != null && i < _h.IsDay.Count ? _h.IsDay[i] == 1 : TimeAt(i).Hour is >= 7 and < 19;

            // --- Current conditions ---

            public V3WxObservationsCurrent Observation()
            {
                var c = _om.Current ?? new OmCurrent();
                var n = _nowIndex;
                var temperature = (int) Math.Round(c.Temperature ?? V(_h.Temperature, n));
                var dew = (int) Math.Round(c.DewPoint ?? V(_h.DewPoint, n));
                var humidity = (int) Math.Round(c.Humidity ?? V(_h.Humidity, n));
                var windSpeed = c.WindSpeed ?? V(_h.WindSpeed, n);
                var gust = c.WindGusts ?? V(_h.WindGusts, n);
                var direction = c.WindDirection ?? V(_h.WindDirection, n);

                // A close airport station replaces the model values with its measured ones.
                var s = _station;
                var measuredTemperature = s is { IsNear: true, TemperatureC: not null };
                if (measuredTemperature)
                    temperature = (int) Math.Round(FromC(s!.TemperatureC!.Value));
                if (s is { IsNear: true, DewPointC: not null })
                {
                    dew = (int) Math.Round(FromC(s.DewPointC.Value));
                    if (s.TemperatureC != null)
                        humidity = Humidity(s.TemperatureC.Value, s.DewPointC.Value);
                }
                var measuredWind = s is { IsNear: true, WindKnots: not null };
                var variableWind = false;
                if (measuredWind)
                {
                    windSpeed = FromKnots(s!.WindKnots!.Value);
                    gust = s.GustKnots != null ? FromKnots(s.GustKnots.Value) : windSpeed;
                    variableWind = s.WindDegrees == null && s.WindKnots > 0;
                    if (s.WindDegrees != null)
                        direction = s.WindDegrees.Value;
                }
                var isDay = c.IsDay != null ? c.IsDay == 1 : IsDay(n);
                var code = Wmo.ByClouds(c.WeatherCode ?? Code(n), c.CloudCover ?? V(_h.CloudCover, n));
                var icon = Wmo.Icon(code, isDay);
                var mslHpa = c.PressureMsl ?? V(_h.PressureMsl, n, 1013.25);
                var msl3hAgo = V(_h.PressureMsl, n - 3, mslHpa);
                var changeHpa = mslHpa - msl3hAgo;
                var altimeterHpa = mslHpa;
                if (s?.AltimeterHpa != null)
                {
                    altimeterHpa = s.AltimeterHpa.Value;
                    mslHpa = s.SeaLevelHpa ?? altimeterHpa;
                    if (s.TendencyHpa != null)
                        changeHpa = s.TendencyHpa.Value;
                }
                var tendency = Math.Abs(changeHpa) < 0.6 ? 0 : changeHpa > 0 ? 1 : 2;

                double Sum(List<double?>? list, int hours) =>
                    Enumerable.Range(n - hours + 1, hours).Where(i => i >= 0).Sum(i => V(list, i));
                var last24 = Enumerable.Range(Math.Max(0, n - 23), Math.Min(24, n + 1)).Select(i => V(_h.Temperature, i, temperature)).ToList();
                var since7 = Enumerable.Range(0, n + 1).Where(i => TimeAt(i).Date == _nowLocal.Date && TimeAt(i).Hour >= 7)
                    .Select(i => V(_h.Temperature, i, temperature)).DefaultIfEmpty(temperature).Max();
                var (sunrise, sunset) = SunTimes(_nowLocal.Date);
                var uv = (int) Math.Round(V(_h.UvIndex, n));
                var gustMph = WindMph(gust);
                var speedMph = WindMph(windSpeed);
                var maxSince7 = since7;
                var max24 = last24.Max();
                var min24 = last24.Min();
                var feelsLike = (int) Math.Round(c.FeelsLike ?? V(_h.FeelsLike, n, temperature));
                if (measuredTemperature)
                {
                    max24 = Math.Max(max24, temperature);
                    min24 = Math.Min(min24, temperature);
                    if (_nowLocal.Hour >= 7)
                        maxSince7 = Math.Max(maxSince7, temperature);
                    // weather.com shows the heat index in the heat, the wind chill in the cold, and the temperature between.
                    var heat = HeatIndex(temperature, humidity);
                    var chill = WindChill(temperature, speedMph);
                    feelsLike = heat != temperature ? heat : chill;
                }

                // weather.com sends the ceiling in feet for every unit setting, and null for no ceiling.
                int? ceiling = CeilingUnknown;
                if (s is { SkyKnown: true })
                    ceiling = s.CeilingFeet;

                var visibility = s?.VisibilityMiles != null
                    ? Imperial
                        ? Math.Round(Math.Min(s.VisibilityMiles.Value, 10), 1)
                        : Math.Round(Math.Min(s.VisibilityMiles.Value * 1.609344, 16.09), 2)
                    : Visibility(_h.Visibility != null && n < _h.Visibility.Count ? _h.Visibility[n] : null);

                // A station sends a gust only when there is one. The model always has a gust value.
                int? windGust = measuredWind
                    ? s!.GustKnots != null ? (int) Math.Round(gust) : null
                    : gustMph >= speedMph + 9 && gustMph >= 15 ? (int) Math.Round(gust) : null;

                return new V3WxObservationsCurrent
                {
                    cloudCeiling = ceiling,
                    cloudCoverPhrase = CloudPhrase(c.CloudCover ?? V(_h.CloudCover, n)),
                    dayOfWeek = _nowLocal.ToString("dddd", CultureInfo.InvariantCulture),
                    dayOrNight = isDay ? "D" : "N",
                    expirationTimeUtc = Expire,
                    iconCode = icon,
                    iconCodeExtend = icon * 100,
                    obsQualifierCode = null!,
                    obsQualifierSeverity = null!,
                    precip1Hour = Math.Round(Sum(_h.Precipitation, 1), 2),
                    precip6Hour = Math.Round(Sum(_h.Precipitation, 6), 2),
                    precip24Hour = Math.Round(Sum(_h.Precipitation, 24), 2),
                    // weather.com sends the mean sea level pressure in mb in every unit set, and the
                    // altimeter setting and the 3-hour change in inHg for "e".
                    pressureAltimeter = HourlyPressure(altimeterHpa),
                    pressureChange = Imperial ? Math.Round(changeHpa * 0.02953, 2) : Math.Round(changeHpa, 2),
                    pressureMeanSeaLevel = Math.Round(mslHpa, 1),
                    pressureTendencyCode = tendency,
                    pressureTendencyTrend = tendency == 0 ? "Steady" : tendency == 1 ? "Rising" : "Falling",
                    relativeHumidity = humidity,
                    snow1Hour = Math.Round(Sum(_h.Snowfall, 1), 1),
                    snow6Hour = Math.Round(Sum(_h.Snowfall, 6), 1),
                    snow24Hour = Math.Round(Sum(_h.Snowfall, 24), 1),
                    sunriseTimeLocal = sunrise ?? Offset(_nowLocal.Date.AddHours(7)),
                    sunriseTimeUtc = Utc(sunrise ?? Offset(_nowLocal.Date.AddHours(7))),
                    sunsetTimeLocal = sunset ?? Offset(_nowLocal.Date.AddHours(19)),
                    sunsetTimeUtc = Utc(sunset ?? Offset(_nowLocal.Date.AddHours(19))),
                    temperature = temperature,
                    temperatureChange24Hour = n >= 24 ? temperature - (int) Math.Round(V(_h.Temperature, n - 24, temperature)) : 0,
                    temperatureDewPoint = dew,
                    temperatureFeelsLike = feelsLike,
                    temperatureHeatIndex = HeatIndex(temperature, humidity),
                    temperatureMax24Hour = (int) Math.Round(max24),
                    temperatureMaxSince7Am = (int) Math.Round(maxSince7),
                    temperatureMin24Hour = (int) Math.Round(min24),
                    temperatureWindChill = WindChill(temperature, speedMph),
                    uvDescription = UvText(uv),
                    uvIndex = uv,
                    validTimeLocal = Offset(_nowLocal),
                    validTimeUtc = Utc(Offset(_nowLocal)),
                    visibility = visibility,
                    windDirection = (int) Math.Round(direction),
                    windDirectionCardinal = speedMph < 1 ? "CALM" : variableWind ? "VAR" : WaterService.Cardinal(direction),
                    windGust = windGust,
                    windSpeed = (int) Math.Round(windSpeed),
                    wxPhraseLong = Wmo.Phrase(code, isDay),
                    wxPhraseMedium = null!,
                    wxPhraseShort = null!
                };
            }

            // --- Hourly ---

            public V3WxForecastHourly Hourly(int hours)
            {
                var first = _nowIndex + 1;
                var indices = Enumerable.Range(first, Math.Max(0, Math.Min(hours, Count - first))).ToList();
                var h = new V3WxForecastHourly
                {
                    cloudCover = new(), dayOfWeek = new(), dayOrNight = new(), expirationTimeUtc = new(), iconCode = new(),
                    iconCodeExtend = new(), precipChance = new(), precipType = new(), pressureMeanSeaLevel = new(), qpf = new(),
                    qpfSnow = new(), relativeHumidity = new(), temperature = new(), temperatureDewPoint = new(),
                    temperatureFeelsLike = new(), temperatureHeatIndex = new(), temperatureWindChill = new(), uvDescription = new(),
                    uvIndex = new(), validTimeLocal = new(), validTimeUtc = new(), visibility = new(), windDirection = new(),
                    windDirectionCardinal = new(), windGust = new(), windSpeed = new(), wxPhraseLong = new(), wxPhraseShort = new(),
                    wxSeverity = new()
                };
                foreach (var i in indices)
                {
                    var t = TimeAt(i);
                    var day = IsDay(i);
                    var code = Wmo.ByClouds(Code(i), V(_h.CloudCover, i));
                    var icon = Wmo.Icon(code, day);
                    var temp = (int) Math.Round(V(_h.Temperature, i));
                    var hum = (int) Math.Round(V(_h.Humidity, i));
                    var speed = V(_h.WindSpeed, i);
                    var gust = V(_h.WindGusts, i);
                    var uv = (int) Math.Round(V(_h.UvIndex, i));
                    h.cloudCover.Add((int) Math.Round(V(_h.CloudCover, i)));
                    h.dayOfWeek.Add(t.ToString("dddd", CultureInfo.InvariantCulture));
                    h.dayOrNight.Add(day ? "D" : "N");
                    h.expirationTimeUtc.Add(Expire);
                    h.iconCode.Add(icon);
                    h.iconCodeExtend.Add(icon * 100);
                    h.precipChance.Add((int) Math.Round(V(_h.PrecipChance, i)));
                    h.precipType.Add(V(_h.Snowfall, i) > 0 || Wmo.IsSnow(code) ? "snow" : "rain");
                    h.pressureMeanSeaLevel.Add(HourlyPressure(V(_h.PressureMsl, i, 1013.25)));
                    h.qpf.Add(Math.Round(V(_h.Precipitation, i), 2));
                    h.qpfSnow.Add(Math.Round(V(_h.Snowfall, i), 1));
                    h.relativeHumidity.Add(hum);
                    h.temperature.Add(temp);
                    h.temperatureDewPoint.Add((int) Math.Round(V(_h.DewPoint, i)));
                    h.temperatureFeelsLike.Add((int) Math.Round(V(_h.FeelsLike, i, temp)));
                    h.temperatureHeatIndex.Add(HeatIndex(temp, hum));
                    h.temperatureWindChill.Add(WindChill(temp, WindMph(speed)));
                    h.uvDescription.Add(UvText(uv));
                    h.uvIndex.Add(uv);
                    h.validTimeLocal.Add(Offset(t));
                    h.validTimeUtc.Add(Utc(Offset(t)));
                    h.visibility.Add(Visibility(_h.Visibility != null && i < _h.Visibility.Count ? _h.Visibility[i] : null));
                    h.windDirection.Add((int) Math.Round(V(_h.WindDirection, i)));
                    h.windDirectionCardinal.Add(WaterService.Cardinal(V(_h.WindDirection, i)));
                    h.windGust.Add(WindMph(gust) >= WindMph(speed) + 9 && WindMph(gust) >= 15 ? (int) Math.Round(gust) : null);
                    h.windSpeed.Add((int) Math.Round(speed));
                    h.wxPhraseLong.Add(Wmo.Phrase(code, day));
                    h.wxPhraseShort.Add(Wmo.Phrase(code, day));
                    h.wxSeverity.Add(1);
                }
                return h;
            }

            // --- Days and day parts ---

            // A 12-hour part: the day from 7:00 to 19:00, the night from 19:00 to 7:00.
            private sealed class Part
            {
                public DateTime Start;
                public bool Day;
                public List<int> Hours = new();
                public string Name = "";
            }

            private List<Part> Parts()
            {
                if (_parts != null)
                    return _parts;
                _parts = new List<Part>();
                for (var d = 0; d < 16; d++)
                {
                    foreach (var day in new[] { true, false })
                    {
                        var start = _nowLocal.Date.AddDays(d).AddHours(day ? 7 : 19);
                        var part = new Part { Start = start, Day = day };
                        for (var i = 0; i < Count; i++)
                        {
                            var t = TimeAt(i);
                            if (t >= start && t < start.AddHours(12))
                                part.Hours.Add(i);
                        }
                        part.Name = d switch
                        {
                            0 => day ? "Today" : "Tonight",
                            1 => day ? "Tomorrow" : "Tomorrow night",
                            _ => start.ToString("dddd", CultureInfo.InvariantCulture) + (day ? "" : " night")
                        };
                        _parts.Add(part);
                    }
                }
                return _parts;
            }

            public V3WxForecastDaily Daily(int days)
            {
                var parts = Parts();
                var dp = new Daypart
                {
                    cloudCover = new(), dayOrNight = new(), daypartName = new(), iconCode = new(), iconCodeExtend = new(),
                    narrative = new(), precipChance = new(), precipType = new(), qpf = new(), qpfSnow = new(),
                    qualifierCode = new(), qualifierPhrase = new(), relativeHumidity = new(), snowRange = new(),
                    temperature = new(), temperatureHeatIndex = new(), temperatureWindChill = new(), thunderCategory = new(),
                    thunderIndex = new(), uvDescription = new(), uvIndex = new(), windDirection = new(),
                    windDirectionCardinal = new(), windPhrase = new(), windSpeed = new(), wxPhraseLong = new(), wxPhraseShort = new()
                };
                var daily = new V3WxForecastDaily
                {
                    calendarDayTemperatureMax = new(), calendarDayTemperatureMin = new(), dayOfWeek = new(), expirationTimeUtc = new(),
                    moonPhase = new(), moonPhaseCode = new(), moonPhaseDay = new(), moonriseTimeLocal = new(), moonriseTimeUtc = new(),
                    moonsetTimeLocal = new(), moonsetTimeUtc = new(), narrative = new(), qpf = new(), qpfSnow = new(),
                    sunriseTimeLocal = new(), sunriseTimeUtc = new(), sunsetTimeLocal = new(), sunsetTimeUtc = new(),
                    temperatureMax = new(), temperatureMin = new(), validTimeLocal = new(), validTimeUtc = new(),
                    daypart = new List<Daypart> { dp }
                };

                for (var d = 0; d < days; d++)
                {
                    var date = _nowLocal.Date.AddDays(d);
                    var calendar = Enumerable.Range(0, Count).Where(i => TimeAt(i).Date == date).ToList();
                    var dayPart = parts[d * 2];
                    var nightPart = parts[d * 2 + 1];
                    int? max = dayPart.Hours.Count > 0 ? (int) Math.Round(dayPart.Hours.Max(i => V(_h.Temperature, i))) : null;
                    int? min = nightPart.Hours.Count > 0 ? (int) Math.Round(nightPart.Hours.Min(i => V(_h.Temperature, i))) : null;
                    var (sunrise, sunset) = SunTimes(date);
                    var (moonrise, moonset) = Moon.RiseSet(date, _location.latitude, _location.longitude, _offset);
                    var age = Moon.Age(Offset(date.AddHours(12)).UtcDateTime);

                    daily.calendarDayTemperatureMax.Add(calendar.Count > 0 ? (int) Math.Round(calendar.Max(i => V(_h.Temperature, i))) : max);
                    daily.calendarDayTemperatureMin.Add(calendar.Count > 0 ? (int) Math.Round(calendar.Min(i => V(_h.Temperature, i))) : min ?? 0);
                    daily.dayOfWeek.Add(date.ToString("dddd", CultureInfo.InvariantCulture));
                    daily.expirationTimeUtc.Add(Expire);
                    daily.moonPhase.Add(Moon.PhaseName(age));
                    daily.moonPhaseCode.Add(Moon.PhaseCode(age));
                    daily.moonPhaseDay.Add((int) Math.Round(age) % 30);
                    daily.moonriseTimeLocal.Add(moonrise == null ? null : Offset(moonrise.Value));
                    daily.moonriseTimeUtc.Add(moonrise == null ? null : Utc(Offset(moonrise.Value)));
                    daily.moonsetTimeLocal.Add(moonset == null ? null : Offset(moonset.Value));
                    daily.moonsetTimeUtc.Add(moonset == null ? null : Utc(Offset(moonset.Value)));
                    daily.qpf.Add(Math.Round(calendar.Sum(i => V(_h.Precipitation, i)), 2));
                    daily.qpfSnow.Add(Math.Round(calendar.Sum(i => V(_h.Snowfall, i)), 1));
                    daily.sunriseTimeLocal.Add(sunrise);
                    daily.sunriseTimeUtc.Add(sunrise == null ? null : Utc(sunrise.Value));
                    daily.sunsetTimeLocal.Add(sunset);
                    daily.sunsetTimeUtc.Add(sunset == null ? null : Utc(sunset.Value));
                    daily.temperatureMax.Add(max);
                    daily.temperatureMin.Add(min ?? 0);
                    daily.validTimeLocal.Add(Offset(date.AddHours(7)));
                    daily.validTimeUtc.Add(Utc(Offset(date.AddHours(7))));
                    daily.narrative.Add(DayNarrative(dayPart, max, min));

                    foreach (var part in new[] { dayPart, nightPart })
                        AddPart(dp, part);
                }
                return daily;
            }

            private void AddPart(Daypart dp, Part part)
            {
                if (part.Hours.Count == 0)
                {
                    dp.cloudCover.Add(null); dp.dayOrNight.Add(null!); dp.daypartName.Add(null!); dp.iconCode.Add(null);
                    dp.iconCodeExtend.Add(null); dp.narrative.Add(null!); dp.precipChance.Add(null); dp.precipType.Add(null!);
                    dp.qpf.Add(null); dp.qpfSnow.Add(null); dp.qualifierCode.Add(null!); dp.qualifierPhrase.Add(null!);
                    dp.relativeHumidity.Add(null); dp.snowRange.Add(null!); dp.temperature.Add(null);
                    dp.temperatureHeatIndex.Add(null); dp.temperatureWindChill.Add(null); dp.thunderCategory.Add(null!);
                    dp.thunderIndex.Add(null); dp.uvDescription.Add(null!); dp.uvIndex.Add(null); dp.windDirection.Add(null);
                    dp.windDirectionCardinal.Add(null!); dp.windPhrase.Add(null!); dp.windSpeed.Add(null);
                    dp.wxPhraseLong.Add(null!); dp.wxPhraseShort.Add(null!);
                    return;
                }

                var a = Aggregate(part);
                dp.cloudCover.Add(a.Cloud);
                dp.dayOrNight.Add(part.Day ? "D" : "N");
                dp.daypartName.Add(part.Name);
                dp.iconCode.Add(a.Icon);
                dp.iconCodeExtend.Add(a.Icon * 100);
                dp.narrative.Add(PartNarrative(part, a));
                dp.precipChance.Add(a.Chance);
                dp.precipType.Add(a.Snow > 0 ? "snow" : "rain");
                dp.qpf.Add(Math.Round(a.Precip, 2));
                dp.qpfSnow.Add(Math.Round(a.Snow, 1));
                dp.qualifierCode.Add(null!);
                dp.qualifierPhrase.Add(null!);
                dp.relativeHumidity.Add(a.Humidity);
                dp.snowRange.Add("");
                dp.temperature.Add(a.Temperature);
                dp.temperatureHeatIndex.Add(HeatIndex(a.Temperature, a.Humidity));
                dp.temperatureWindChill.Add(WindChill(a.Temperature, WindMph(a.Wind)));
                dp.thunderCategory.Add(a.Thunder ? "Thunder expected" : "No thunder");
                dp.thunderIndex.Add(a.Thunder ? 2 : 0);
                dp.uvDescription.Add(UvText(a.Uv));
                dp.uvIndex.Add(a.Uv);
                dp.windDirection.Add(a.Direction);
                dp.windDirectionCardinal.Add(WaterService.Cardinal(a.Direction));
                dp.windPhrase.Add(WindPhrase(a));
                dp.windSpeed.Add((int) Math.Round(a.Wind));
                dp.wxPhraseLong.Add(Wmo.Phrase(a.Code, part.Day));
                dp.wxPhraseShort.Add(Wmo.Phrase(a.Code, part.Day));
            }

            private sealed class PartValues
            {
                public int Code, Icon, Cloud, Chance, Humidity, Temperature, Uv, Direction;
                public double Precip, Snow, Wind, WindMax, Gust, DewF, TempF, VisibilityMi;
                public bool Thunder, Freezing;
            }

            private PartValues Aggregate(Part part)
            {
                var hours = part.Hours;
                // The most significant weather code of the part: thunder, then snow and rain, then clouds.
                var code = hours.Select(Code).OrderByDescending(Wmo.Weight).First();
                code = Wmo.ByClouds(code, hours.Average(i => V(_h.CloudCover, i)));
                double x = 0, y = 0;
                foreach (var i in hours)
                {
                    var s = V(_h.WindSpeed, i);
                    var r = V(_h.WindDirection, i) * Math.PI / 180;
                    x += s * Math.Sin(r);
                    y += s * Math.Cos(r);
                }
                var direction = (int) Math.Round((Math.Atan2(x, y) * 180 / Math.PI + 360) % 360);
                var temps = hours.Select(i => V(_h.Temperature, i)).ToList();
                return new PartValues
                {
                    Code = code,
                    Icon = Wmo.Icon(code, part.Day),
                    Cloud = (int) Math.Round(hours.Average(i => V(_h.CloudCover, i))),
                    Chance = (int) Math.Round(hours.Max(i => V(_h.PrecipChance, i))),
                    Humidity = (int) Math.Round(hours.Average(i => V(_h.Humidity, i))),
                    Temperature = (int) Math.Round(part.Day ? temps.Max() : temps.Min()),
                    Uv = (int) Math.Round(hours.Max(i => V(_h.UvIndex, i))),
                    Direction = direction,
                    Precip = hours.Sum(i => V(_h.Precipitation, i)),
                    Snow = hours.Sum(i => V(_h.Snowfall, i)),
                    Wind = hours.Average(i => V(_h.WindSpeed, i)),
                    WindMax = hours.Max(i => V(_h.WindSpeed, i)),
                    Gust = hours.Max(i => V(_h.WindGusts, i)),
                    DewF = TempF(hours.Average(i => V(_h.DewPoint, i))),
                    TempF = TempF(temps.Average()),
                    VisibilityMi = hours.Min(i => Imperial ? Visibility(_h.Visibility?[i]) : Visibility(_h.Visibility?[i]) / 1.609344),
                    Thunder = hours.Any(i => Code(i) >= 95),
                    Freezing = hours.Any(i => Code(i) is 56 or 57 or 66 or 67)
                };
            }

            private string Unit => Imperial ? "F" : "C";

            private static string Sentence(string phrase) =>
                phrase.Length == 0 ? phrase : phrase.Substring(0, 1) + phrase.Substring(1).ToLowerInvariant();
            private string WindUnit => _units switch { "e" or "h" => "mph", "s" => "m/s", _ => "km/h" };

            private string WindPhrase(PartValues a)
            {
                if (WindMph(a.WindMax) < 6)
                    return "Winds light and variable.";
                var low = (int) Math.Round(a.Wind / 5) * 5;
                var high = Math.Max(low + 5, (int) Math.Round(a.WindMax / 5) * 5);
                return "Winds " + WaterService.Cardinal(a.Direction) + " at " + low + " to " + high + " " + WindUnit + ".";
            }

            private string PartNarrative(Part part, PartValues a)
            {
                var text = Sentence(Wmo.Phrase(a.Code, part.Day)) + ". ";
                if (a.Chance >= 30)
                    text += "Chance of " + (a.Snow > 0 ? "snow" : "rain") + " " + a.Chance + "%. ";
                text += (part.Day ? "High " : "Low ") + a.Temperature + Unit + ". " + WindPhrase(a);
                return text;
            }

            private string DayNarrative(Part dayPart, int? max, int? min)
            {
                var phrase = dayPart.Hours.Count > 0 ? Sentence(Wmo.Phrase(Aggregate(dayPart).Code, true)) + ". " : "";
                var high = max != null ? "High " + max + Unit + ". " : "";
                var low = min != null ? "Low " + min + Unit + "." : "";
                return (phrase + high + low).Trim();
            }

            // --- Indices, one value per 12-hour part from the part that holds the current time ---

            private List<Part> IndexParts() =>
                Parts().Where(p => p.Hours.Count > 0 && p.Start.AddHours(12) > _nowLocal).Take(30).ToList();

            private (List<int> Valid, List<DateTimeOffset> Local, List<string> DayInd, List<int> Num, List<string> Names) IndexHeader()
            {
                var parts = IndexParts();
                return (parts.Select(p => Utc(Offset(p.Start))).ToList(),
                        parts.Select(p => Offset(p.Start)).ToList(),
                        parts.Select(p => p.Day ? "D" : "N").ToList(),
                        Enumerable.Range(1, parts.Count).ToList(),
                        parts.Select(p => p.Name).ToList());
            }

            private Metadata Meta() => new()
            {
                language = "en-US", transactionId = "open-meteo", version = "1",
                latitude = _location.latitude, longitude = _location.longitude,
                expireTimeGmt = Expire, statusCode = 200
            };

            public V2idxRunDaypartResult Run()
            {
                var (valid, local, ind, num, names) = IndexHeader();
                var values = IndexParts().Select(p =>
                {
                    var a = Aggregate(p);
                    var score = 10.0;
                    score -= Math.Max(0, Math.Abs(a.TempF - 55) - 5) / 4;
                    score -= Math.Max(0, a.DewF - 60) / 4;
                    score -= Math.Max(0, WindMph(a.Wind) - 12) / 6;
                    score -= a.Chance >= 60 ? 3 : a.Chance >= 30 ? 1 : 0;
                    score -= PrecipIn(a.Precip) > 0.2 ? 2 : 0;
                    score -= a.Thunder ? 2 : 0;
                    return (int) Math.Max(1, Math.Min(10, Math.Round(score)));
                }).ToList();
                var categories = values.Select(v => v >= 9 ? "Excellent" : v >= 7 ? "Good" : v >= 5 ? "Fair" : v >= 3 ? "Poor" : "Very Poor").ToList();
                return new V2idxRunDaypartResult
                {
                    metadata = Meta(),
                    RunWeatherIndexDaypart = new RunWeatherIndexDaypart
                    {
                        fcstValid = valid, fcstValidLocal = local, dayInd = ind, num = num, daypartName = names,
                        longRunWeatherIndex = values, longRunWeatherCategory = categories,
                        shortRunWeatherIndex = values, shortRunWeatherCategory = categories
                    }
                };
            }

            public V2idxDriveDaypartResult Drive()
            {
                var (valid, local, ind, num, names) = IndexHeader();
                var values = IndexParts().Select(p =>
                {
                    var a = Aggregate(p);
                    var snowIn = Imperial ? a.Snow : a.Snow / 2.54;
                    var score = 0;
                    if (a.Freezing) score = Math.Max(score, 5);
                    if (snowIn >= 3) score = Math.Max(score, 4);
                    else if (snowIn >= 1) score = Math.Max(score, 3);
                    else if (snowIn >= 0.1) score = Math.Max(score, 2);
                    if (PrecipIn(a.Precip) >= 0.5) score = Math.Max(score, 2);
                    else if (PrecipIn(a.Precip) >= 0.1) score = Math.Max(score, 1);
                    if (a.VisibilityMi < 0.5) score += 2;
                    else if (a.VisibilityMi < 2) score += 1;
                    if (WindMph(a.Gust) >= 45) score += 1;
                    return Math.Min(6, score);
                }).ToList();
                return new V2idxDriveDaypartResult
                {
                    metadata = Meta(),
                    drivingDifficultyIndex12hour = new DrivingDifficultyIndexDaypart
                    {
                        fcstValid = valid, fcstValidLocal = local, dayInd = ind, num = num, daypartName = names,
                        drivingDifficultyIndex = values,
                        drivingDifficultyCategory = values.Select(v => v == 0 ? "None" : v <= 2 ? "Low" : v <= 4 ? "Moderate" : v == 5 ? "High" : "Very High").ToList()
                    }
                };
            }

            public V2idxDrySkinDaypartResult DrySkin()
            {
                var (valid, local, ind, num, names) = IndexHeader();
                var values = IndexParts().Select(p =>
                {
                    var a = Aggregate(p);
                    var score = a.DewF >= 55 ? 1 : a.DewF >= 48 ? 3 : a.DewF >= 40 ? 5 : a.DewF >= 32 ? 6 : a.DewF >= 24 ? 7 : a.DewF >= 15 ? 8 : 9;
                    if (WindMph(a.Wind) > 15) score++;
                    if (a.TempF < 40) score++;
                    if (a.Humidity > 70) score--;
                    return Math.Max(0, Math.Min(10, score));
                }).ToList();
                return new V2idxDrySkinDaypartResult
                {
                    metadata = Meta(),
                    DrySkinIndexDaypart = new DrySkinIndexDaypart
                    {
                        fcstValid = valid, fcstValidLocal = local, dayInd = ind, num = num, daypartName = names,
                        drySkinIndex = values,
                        drySkinCategory = values.Select(v => v <= 3 ? "Low Concern" : v <= 6 ? "Moderate Concern" : v <= 8 ? "High Concern" : "Extreme Concern").ToList()
                    }
                };
            }

            public V2idxWateringDaypartResult Watering()
            {
                var (valid, local, ind, num, names) = IndexHeader();
                var values = IndexParts().Select(p =>
                {
                    var a = Aggregate(p);
                    var need = Math.Max(0, (a.TempF - 30) / 7);
                    need *= a.Humidity > 80 ? 0.6 : a.Humidity < 40 ? 1.3 : 1;
                    need -= PrecipIn(a.Precip) * 20;
                    return (int) Math.Max(0, Math.Min(10, Math.Round(need)));
                }).ToList();
                return new V2idxWateringDaypartResult
                {
                    metadata = Meta(),
                    WateringNeedsIndexDaypart = new WateringNeedsIndexDaypart
                    {
                        fcstValid = valid, fcstValidLocal = local, dayInd = ind, num = num, daypartName = names,
                        wateringNeedsIndex = values,
                        wateringNeedsCategory = values.Select(v => v <= 1 ? "Very Low" : v <= 3 ? "Low" : v <= 5 ? "Moderate" : v <= 7 ? "High" : "Very High").ToList()
                    }
                };
            }

            // Pollen cannot be calculated from the weather. The lists stay empty, so the Pollen card
            // uses Open-Meteo in Europe and hides elsewhere.
            public V2idxPollenDaypartResult Pollen() => new()
            {
                metadata = Meta(),
                PollenForecastDaypart = new PollenForecastDaypart
                {
                    fcstValid = new(), fcstValidLocal = new(), dayInd = new(), num = new(), daypartName = new(),
                    grassPollenIndex = new(), grassPollenCategory = new(), treePollenIndex = new(), treePollenCategory = new(),
                    ragweedPollenIndex = new(), ragweedPollenCategory = new()
                }
            };

            // --- Small helpers ---

            private (DateTimeOffset? Rise, DateTimeOffset? Set) SunTimes(DateTime date)
            {
                var d = _om.Daily;
                if (d?.Time == null)
                    return (null, null);
                var i = d.Time.IndexOf(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                if (i < 0)
                    return (null, null);
                var rise = ParseLocal(d.Sunrise != null && i < d.Sunrise.Count ? d.Sunrise[i] : null);
                var set = ParseLocal(d.Sunset != null && i < d.Sunset.Count ? d.Sunset[i] : null);
                return (rise == null ? null : Offset(rise.Value), set == null ? null : Offset(set.Value));
            }

            // Relative humidity from temperature and dew point in degrees C (Magnus formula).
            private static int Humidity(double temperatureC, double dewPointC)
            {
                double E(double t) => Math.Exp(17.625 * t / (243.04 + t));
                return (int) Math.Round(Math.Clamp(100 * E(dewPointC) / E(temperatureC), 1, 100));
            }

            private int HeatIndex(int temperature, int humidity)
            {
                var t = TempF(temperature);
                if (t < 80)
                    return temperature;
                var r = humidity;
                var hi = -42.379 + 2.04901523 * t + 10.14333127 * r - 0.22475541 * t * r - 0.00683783 * t * t
                         - 0.05481717 * r * r + 0.00122874 * t * t * r + 0.00085282 * t * r * r - 0.00000199 * t * t * r * r;
                return (int) Math.Round(FromF(Math.Max(t, hi)));
            }

            private int WindChill(int temperature, double windMph)
            {
                var t = TempF(temperature);
                if (t > 50 || windMph < 3)
                    return temperature;
                var wc = 35.74 + 0.6215 * t - 35.75 * Math.Pow(windMph, 0.16) + 0.4275 * t * Math.Pow(windMph, 0.16);
                return (int) Math.Round(FromF(Math.Min(t, wc)));
            }

            private static string UvText(int uv) =>
                uv <= 2 ? "Low" : uv <= 5 ? "Moderate" : uv <= 7 ? "High" : uv <= 10 ? "Very High" : "Extreme";

            private static string CloudPhrase(double cover) =>
                cover < 13 ? "Clear" : cover < 38 ? "Mostly Clear" : cover < 63 ? "Partly Cloudy" : cover < 88 ? "Mostly Cloudy" : "Cloudy";
        }

        private static DateTime? ParseLocal(string? text) =>
            DateTime.TryParseExact(text, "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

        // --- WMO weather codes in weather.com terms ---

        private static class Wmo
        {
            public static bool IsSnow(int code) => code is 71 or 73 or 75 or 77 or 85 or 86;

            // Codes 0 to 3 describe clouds only. They are replaced by the cloud cover, which gives
            // the same wording as weather.com more often. Code 4 stands for "mostly cloudy".
            public static int ByClouds(int code, double cover) =>
                code > 3 ? code : cover < 13 ? 0 : cover < 38 ? 1 : cover < 63 ? 2 : cover < 88 ? 4 : 3;

            // Which code of a period describes it best: thunder, freezing, snow, rain, drizzle, fog, clouds.
            public static int Weight(int code) => code switch
            {
                >= 95 => 100 + code,
                56 or 57 or 66 or 67 => 90 + code,
                >= 71 and <= 77 or 85 or 86 => 80 + code,
                >= 61 and <= 65 or >= 80 and <= 82 => 70 + code,
                >= 51 and <= 55 => 60 + code,
                45 or 48 => 50,
                _ => code
            };

            public static int Icon(int code, bool day) => code switch
            {
                0 => day ? 32 : 31,
                1 => day ? 34 : 33,
                2 => day ? 30 : 29,
                3 => 26,
                4 => day ? 28 : 27,
                45 or 48 => 20,
                51 or 53 or 55 => 9,
                56 or 57 => 8,
                61 => 11,
                63 or 65 => 12,
                66 or 67 => 10,
                71 => 14,
                73 or 75 => 16,
                77 => 13,
                80 or 81 => day ? 39 : 45,
                82 => 12,
                85 => day ? 41 : 46,
                86 => 16,
                95 => day ? 37 : 47,
                96 or 99 => 4,
                _ => day ? 34 : 33
            };

            public static string Phrase(int code, bool day) => code switch
            {
                0 => day ? "Sunny" : "Clear",
                1 => day ? "Mostly Sunny" : "Mostly Clear",
                2 => "Partly Cloudy",
                3 => "Cloudy",
                4 => "Mostly Cloudy",
                45 => "Fog",
                48 => "Freezing Fog",
                51 => "Light Drizzle",
                53 => "Drizzle",
                55 => "Heavy Drizzle",
                56 or 57 => "Freezing Drizzle",
                61 => "Light Rain",
                63 => "Rain",
                65 => "Heavy Rain",
                66 or 67 => "Freezing Rain",
                71 => "Light Snow",
                73 => "Snow",
                75 => "Heavy Snow",
                77 => "Snow Grains",
                80 => "Light Rain Shower",
                81 => "Rain Shower",
                82 => "Heavy Rain Shower",
                85 => "Snow Shower",
                86 => "Heavy Snow Shower",
                95 => "Thunderstorm",
                96 or 99 => "Thunderstorm with Hail",
                _ => day ? "Fair" : "Fair"
            };
        }

        // --- Moon: phase, and rise and set times from a short lunar theory (accurate to a few minutes) ---

        private static class Moon
        {
            private const double Synodic = 29.530588853;
            private static readonly DateTime KnownNewMoon = new(2000, 1, 6, 18, 14, 0, DateTimeKind.Utc);

            public static double Age(DateTime utc)
            {
                var days = (utc - KnownNewMoon).TotalDays % Synodic;
                return days < 0 ? days + Synodic : days;
            }

            public static string PhaseCode(double age) => age switch
            {
                < 0.5 => "N",
                < 6.4 => "WXC",
                < 8.4 => "FQ",
                < 13.8 => "WXG",
                < 15.8 => "F",
                < 21.1 => "WNG",
                < 23.1 => "LQ",
                < 28.5 => "WNC",
                _ => "N"
            };

            public static string PhaseName(double age) => PhaseCode(age) switch
            {
                "N" => "New Moon",
                "WXC" => "Waxing Crescent",
                "FQ" => "First Quarter",
                "WXG" => "Waxing Gibbous",
                "F" => "Full Moon",
                "WNG" => "Waning Gibbous",
                "LQ" => "Last Quarter",
                _ => "Waning Crescent"
            };

            /// <summary>Moonrise and moonset on a local date, found in 10-minute steps.</summary>
            public static (DateTime? Rise, DateTime? Set) RiseSet(DateTime localDate, double latitude, double longitude, TimeSpan offset)
            {
                DateTime? rise = null, set = null;
                const double horizon = -0.83;
                var previous = Altitude(localDate - offset, latitude, longitude) - horizon;
                for (var minutes = 10; minutes <= 24 * 60; minutes += 10)
                {
                    var local = localDate.AddMinutes(minutes);
                    var current = Altitude(local - offset, latitude, longitude) - horizon;
                    if (previous < 0 && current >= 0 && rise == null)
                        rise = local.AddMinutes(-10 * current / (current - previous));
                    if (previous >= 0 && current < 0 && set == null)
                        set = local.AddMinutes(-10 * current / (current - previous));
                    previous = current;
                }
                return (rise, set);
            }

            private static double Rad(double d) => d * Math.PI / 180;
            private static double Deg(double r) => r * 180 / Math.PI;
            private static double Norm(double d) => ((d % 360) + 360) % 360;

            // Topocentric altitude of the moon in degrees, after Paul Schlyter's method.
            private static double Altitude(DateTime utc, double latitude, double longitude)
            {
                var d = (utc - new DateTime(1999, 12, 31, 0, 0, 0, DateTimeKind.Utc)).TotalDays;
                var n = Norm(125.1228 - 0.0529538083 * d);
                const double i = 5.1454, a = 60.2666, e = 0.054900;
                var w = Norm(318.0634 + 0.1643573223 * d);
                var m = Norm(115.3654 + 13.0649929509 * d);
                var ms = Norm(356.0470 + 0.9856002585 * d);
                var ws = Norm(282.9404 + 4.70935e-5 * d);

                var ea = m + Deg(e * Math.Sin(Rad(m)) * (1 + e * Math.Cos(Rad(m))));
                for (var k = 0; k < 3; k++)
                    ea -= (ea - Deg(e * Math.Sin(Rad(ea))) - m) / (1 - e * Math.Cos(Rad(ea)));
                var xv = a * (Math.Cos(Rad(ea)) - e);
                var yv = a * Math.Sqrt(1 - e * e) * Math.Sin(Rad(ea));
                var v = Deg(Math.Atan2(yv, xv));
                var r = Math.Sqrt(xv * xv + yv * yv);

                var xh = r * (Math.Cos(Rad(n)) * Math.Cos(Rad(v + w)) - Math.Sin(Rad(n)) * Math.Sin(Rad(v + w)) * Math.Cos(Rad(i)));
                var yh = r * (Math.Sin(Rad(n)) * Math.Cos(Rad(v + w)) + Math.Cos(Rad(n)) * Math.Sin(Rad(v + w)) * Math.Cos(Rad(i)));
                var zh = r * Math.Sin(Rad(v + w)) * Math.Sin(Rad(i));
                var lon = Deg(Math.Atan2(yh, xh));
                var lat = Deg(Math.Atan2(zh, Math.Sqrt(xh * xh + yh * yh)));

                var ls = Norm(ws + ms);
                var lm = Norm(n + w + m);
                var dd = lm - ls;
                var f = lm - n;
                lon += -1.274 * Math.Sin(Rad(m - 2 * dd)) + 0.658 * Math.Sin(Rad(2 * dd)) - 0.186 * Math.Sin(Rad(ms))
                       - 0.059 * Math.Sin(Rad(2 * m - 2 * dd)) - 0.057 * Math.Sin(Rad(m - 2 * dd + ms)) + 0.053 * Math.Sin(Rad(m + 2 * dd))
                       + 0.046 * Math.Sin(Rad(2 * dd - ms)) + 0.041 * Math.Sin(Rad(m - ms)) - 0.035 * Math.Sin(Rad(dd))
                       - 0.031 * Math.Sin(Rad(m + ms)) - 0.015 * Math.Sin(Rad(2 * f - 2 * dd)) + 0.011 * Math.Sin(Rad(m - 4 * dd));
                lat += -0.173 * Math.Sin(Rad(f - 2 * dd)) - 0.055 * Math.Sin(Rad(m - f - 2 * dd)) - 0.046 * Math.Sin(Rad(m + f - 2 * dd))
                       + 0.033 * Math.Sin(Rad(f + 2 * dd)) + 0.017 * Math.Sin(Rad(2 * m + f));
                r += -0.58 * Math.Cos(Rad(m - 2 * dd)) - 0.46 * Math.Cos(Rad(2 * dd));

                var ecl = 23.4393 - 3.563e-7 * d;
                var xg = Math.Cos(Rad(lon)) * Math.Cos(Rad(lat));
                var yg = Math.Sin(Rad(lon)) * Math.Cos(Rad(lat));
                var zg = Math.Sin(Rad(lat));
                var xe = xg;
                var ye = yg * Math.Cos(Rad(ecl)) - zg * Math.Sin(Rad(ecl));
                var ze = yg * Math.Sin(Rad(ecl)) + zg * Math.Cos(Rad(ecl));
                var ra = Deg(Math.Atan2(ye, xe));
                var dec = Deg(Math.Atan2(ze, Math.Sqrt(xe * xe + ye * ye)));

                var ut = utc.Hour + utc.Minute / 60.0 + utc.Second / 3600.0;
                var lst = Norm(ls + 180 + ut * 15 + longitude);
                var ha = Rad(lst - ra);
                var alt = Deg(Math.Asin(Math.Sin(Rad(latitude)) * Math.Sin(Rad(dec)) + Math.Cos(Rad(latitude)) * Math.Cos(Rad(dec)) * Math.Cos(ha)));
                var parallax = Deg(Math.Asin(1 / r));
                return alt - parallax * Math.Cos(Rad(alt));
            }
        }
    }
}
