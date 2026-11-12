using FluentWeather.Helpers;
using System;
using System.Threading.Tasks;
using Windows.Storage;

namespace FluentWeather.Services
{
    /// <summary>The "Weather data source" setting.</summary>
    public enum DataSourceMode
    {
        Automatic = 0,
        WeatherCom = 1,
        OpenMeteo = 2
    }

    /// <summary>
    /// Local build: which source the weather requests use.
    ///
    /// Automatic (the default) uses weather.com. When weather.com refuses the key twice in a row
    /// (HTTP 401 or 403, not a lost connection), the app switches to the Open-Meteo backup
    /// (BackupWeatherService). While on the backup it tries weather.com again once an hour and
    /// switches back when weather.com answers. "weather.com only" never switches. "Open-Meteo only"
    /// always uses the backup.
    ///
    /// The state is kept in LocalSettings, so the background task and the app agree.
    /// </summary>
    public static class DataSourceService
    {
        public const string ModeKey = "dataSourceMode";
        private const string RefusedCountKey = "dataSourceRefusedCount";
        private const string BackupSinceKey = "dataSourceBackupSinceTicks";
        private const string LastRetryKey = "dataSourceLastRetryTicks";
        private const int RefusalsBeforeSwitch = 2;
        private static readonly TimeSpan RetryEvery = TimeSpan.FromHours(1);

        /// <summary>Raised when the active source changes, so the page can update its note.</summary>
        public static event EventHandler? ActiveSourceChanged;

        public static async Task<DataSourceMode> GetModeAsync()
        {
            try
            {
                var value = await ApplicationData.Current.LocalSettings.ReadAsync<int>(ModeKey);
                return value is >= 0 and <= 2 ? (DataSourceMode) value : DataSourceMode.Automatic;
            }
            catch (Exception)
            {
                return DataSourceMode.Automatic;
            }
        }

        public static async Task SetModeAsync(DataSourceMode mode)
        {
            await ApplicationData.Current.LocalSettings.SaveAsync(ModeKey, (int) mode);
            ActiveSourceChanged?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>True when the backup is in use now, by choice or after weather.com refused the key.</summary>
        public static async Task<bool> IsBackupActiveAsync()
        {
            var mode = await GetModeAsync();
            if (mode == DataSourceMode.OpenMeteo)
                return true;
            if (mode == DataSourceMode.WeatherCom)
                return false;
            return BackupSince() != 0;
        }

        /// <summary>
        /// Whether the next request should go to weather.com. In Automatic mode on the backup, one
        /// request an hour goes to weather.com to see whether it answers again.
        /// </summary>
        public static async Task<bool> ShouldTryWeatherComAsync()
        {
            var mode = await GetModeAsync();
            if (mode == DataSourceMode.OpenMeteo)
                return false;
            if (mode == DataSourceMode.WeatherCom || BackupSince() == 0)
                return true;

            var last = ReadLong(LastRetryKey);
            if (DateTime.UtcNow.Ticks - last < RetryEvery.Ticks)
                return false;
            WriteLong(LastRetryKey, DateTime.UtcNow.Ticks);
            return true;
        }

        /// <summary>weather.com answered. Leaves the backup in Automatic mode.</summary>
        public static void ReportWeatherComSuccess()
        {
            var wasBackup = BackupSince() != 0;
            WriteLong(RefusedCountKey, 0);
            WriteLong(BackupSinceKey, 0);
            if (wasBackup)
                ActiveSourceChanged?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>weather.com refused the key. Returns true when the app now uses the backup.</summary>
        public static async Task<bool> ReportKeyRefusedAsync()
        {
            if (await GetModeAsync() != DataSourceMode.Automatic)
                return false;
            if (BackupSince() != 0)
                return true;

            var count = ReadLong(RefusedCountKey) + 1;
            WriteLong(RefusedCountKey, count);
            if (count < RefusalsBeforeSwitch)
                return false;

            WriteLong(BackupSinceKey, DateTime.UtcNow.Ticks);
            WriteLong(LastRetryKey, DateTime.UtcNow.Ticks);
            CrashLog.Write(null, "DataSourceService", "weather.com refused the key twice. Switched to the Open-Meteo backup.");
            ActiveSourceChanged?.Invoke(null, EventArgs.Empty);
            return true;
        }

        private static long BackupSince() => ReadLong(BackupSinceKey);

        private static long ReadLong(string key)
        {
            try
            {
                return ApplicationData.Current.LocalSettings.Values[key] is long value ? value : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static void WriteLong(string key, long value)
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[key] = value;
            }
            catch (Exception)
            {
                // The state is rebuilt by the next request.
            }
        }
    }
}
