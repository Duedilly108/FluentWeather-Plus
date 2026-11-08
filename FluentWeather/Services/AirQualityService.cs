using FluentWeather.Helpers;
using FluentWeather.Models;
using FluentWeather.Utils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FluentWeather.Services
{
    /// <summary>
    /// The current air quality of a location, from the free Open-Meteo air quality API
    /// (air-quality-api.open-meteo.com). It needs no key and uses no weather.com request. The data
    /// comes from the Copernicus Atmosphere Monitoring Service (CAMS) and is licensed CC BY 4.0, so
    /// the app shows a credit with it. The values change once an hour, so each location is
    /// downloaded at most once an hour.
    /// </summary>
    public static class AirQualityService
    {
        private static readonly HttpClient Http = ApiUtils.CreateClient("https://air-quality-api.open-meteo.com/");
        private static readonly TimeSpan CacheTime = TimeSpan.FromMinutes(60);
        private static readonly Dictionary<string, (DateTime FetchedUtc, AirQualityCurrent Data)> Cache = new();
        private static readonly SemaphoreSlim Lock = new(1, 1);

        public static async Task<AirQualityCurrent?> GetAsync(double latitude, double longitude)
        {
            var ci = CultureInfo.InvariantCulture;
            var lat = latitude.ToString("0.00", ci);
            var lon = longitude.ToString("0.00", ci);
            var key = lat + "," + lon;

            await Lock.WaitAsync();
            try
            {
                if (Cache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.FetchedUtc < CacheTime)
                    return cached.Data;

                try
                {
                    var url = "v1/air-quality?latitude=" + lat + "&longitude=" + lon +
                              "&current=us_aqi,european_aqi,pm2_5,pm10,ozone,nitrogen_dioxide";
                    using var response = await ApiUtils.GetWithRetryAsync(Http, url);
                    if (!response.IsSuccessStatusCode)
                        return cached.Data;

                    var json = await response.Content.ReadAsStringAsync();
                    var data = JsonSerializer.Deserialize(json, FluentWeatherJsonContext.Default.AirQualityResponse)?.Current;
                    if (data == null)
                        return cached.Data;

                    Cache[key] = (DateTime.UtcNow, data);
                    return data;
                }
                catch (Exception)
                {
                    // An older value, or null when there is none. The card is then hidden.
                    return cached.Data;
                }
            }
            finally
            {
                Lock.Release();
            }
        }

        /// <summary>The category name of an index value, on the US EPA scale or the European scale.</summary>
        public static string CategoryText(int index, bool usScale)
        {
            string key;
            if (usScale)
            {
                key = index <= 50 ? "AirQualityGood"
                    : index <= 100 ? "AirQualityModerate"
                    : index <= 150 ? "AirQualityUnhealthySensitive"
                    : index <= 200 ? "AirQualityUnhealthy"
                    : index <= 300 ? "AirQualityVeryUnhealthy"
                    : "AirQualityHazardous";
            }
            else
            {
                key = index <= 20 ? "AirQualityGood"
                    : index <= 40 ? "AirQualityFair"
                    : index <= 60 ? "AirQualityModerate"
                    : index <= 80 ? "AirQualityPoor"
                    : index <= 100 ? "AirQualityVeryPoor"
                    : "AirQualityExtremelyPoor";
            }

            return key.GetLocalized();
        }
    }
}
