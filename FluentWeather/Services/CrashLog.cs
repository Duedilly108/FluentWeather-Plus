using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Core;
using Windows.Storage;

namespace FluentWeather.Services
{
    /// <summary>
    /// Local build: writes unhandled errors to crash.log in the app data folder
    /// (%LOCALAPPDATA%\Packages\FluentWeather.LocalBuild_qqrp5dnjcw9xy\LocalState\crash.log).
    /// Get-FluentWeatherCrashReport.ps1 in the build folder collects this file with the Windows
    /// event log entries of the app. The file stays under 256 KB. Nothing is sent anywhere.
    /// A native crash ends the process before this code runs. The event log has those.
    /// </summary>
    public static class CrashLog
    {
        private const string FileName = "crash.log";
        private const long MaxBytes = 256 * 1024;
        private static readonly object Gate = new();

        /// <summary>Hooks the error events of the process. Called once from the App constructor.</summary>
        public static void Install()
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Write(e.ExceptionObject as Exception, "AppDomain.UnhandledException", e.ExceptionObject?.ToString());

            TaskScheduler.UnobservedTaskException += (_, e) =>
                Write(e.Exception, "TaskScheduler.UnobservedTaskException");

            try
            {
                CoreApplication.UnhandledErrorDetected += (_, e) =>
                {
                    try
                    {
                        e.UnhandledError.Propagate();
                    }
                    catch (Exception ex)
                    {
                        Write(ex, "CoreApplication.UnhandledErrorDetected");
                        throw;
                    }
                };
            }
            catch (Exception)
            {
                // Not available in this process. The other hooks still work.
            }
        }

        public static void Write(Exception? exception, string source, string? fallbackText = null)
        {
            try
            {
                var text = new StringBuilder()
                    .Append("=== ").Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"))
                    .Append("  ").Append(Version())
                    .Append("  ").Append(source).Append("\r\n")
                    .Append(exception?.ToString() ?? fallbackText ?? "(no details)").Append("\r\n");
                if (exception != null)
                    text.Append("HResult: 0x").Append(exception.HResult.ToString("X8")).Append("\r\n");
                text.Append("\r\n");

                lock (Gate)
                {
                    var path = Path.Combine(ApplicationData.Current.LocalFolder.Path, FileName);
                    TrimIfLarge(path);
                    File.AppendAllText(path, text.ToString(), Encoding.UTF8);
                }
            }
            catch (Exception)
            {
                // The log must never cause an error of its own.
            }
        }

        private static void TrimIfLarge(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MaxBytes)
                return;

            // Keep the newer half.
            var all = File.ReadAllText(path, Encoding.UTF8);
            var keep = all.Substring(all.Length / 2);
            var start = keep.IndexOf("=== ", StringComparison.Ordinal);
            File.WriteAllText(path, start >= 0 ? keep.Substring(start) : keep, Encoding.UTF8);
        }

        private static string Version()
        {
            try
            {
                var v = Package.Current.Id.Version;
                return "v" + v.Revision + " (" + v.Major + "." + v.Minor + "." + v.Build + "." + v.Revision + ")";
            }
            catch (Exception)
            {
                return "version unknown";
            }
        }
    }
}
