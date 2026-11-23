using System;

namespace FluentWeather.Helpers
{
    /// <summary>
    /// Local build: the phase of the moon on a given day, for the moon row of the day buttons.
    ///
    /// weather.com sends the name of the phase of each forecast day but no percentage of the
    /// lit part. So the app computes it. The calculation uses the mean length of the lunar cycle
    /// (29.530588853 days) from a known new moon (2000-01-06 18:14 UTC). The lit part follows
    /// from the age of the moon with (1 - cos(2 * pi * age / cycle)) / 2. The true phase differs
    /// from this mean phase by a few percent. The tray program uses the same method.
    ///
    /// The age is taken at noon of the local day, so a day shows the phase of its middle.
    /// </summary>
    internal static class MoonMath
    {
        private const double Synodic = 29.530588853;

        private static readonly DateTime KnownNewMoon = new(2000, 1, 6, 18, 14, 0, DateTimeKind.Utc);

        /// <summary>Days since the last new moon, 0 up to 29.53.</summary>
        public static double Age(DateTime utc)
        {
            var days = (utc - KnownNewMoon).TotalDays % Synodic;
            return days < 0 ? days + Synodic : days;
        }

        /// <summary>The age of the moon at noon of the local day. Only the date and the offset of the value count.</summary>
        public static double AgeAtLocalNoon(DateTimeOffset day)
        {
            var noon = new DateTimeOffset(day.Year, day.Month, day.Day, 12, 0, 0, day.Offset);
            return Age(noon.UtcDateTime);
        }

        /// <summary>True from new moon to full moon, when the lit part grows.</summary>
        public static bool IsWaxing(double age) => age < Synodic / 2;

        /// <summary>The lit part of the disc, 0 to 100.</summary>
        public static int LitPercent(double age)
        {
            var lit = (1 - Math.Cos(2 * Math.PI * age / Synodic)) / 2;
            return (int) Math.Round(lit * 100, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// The phase code in the form of weather.com: N new moon, WXC waxing crescent, FQ first quarter,
        /// WXG waxing gibbous, F full moon, WNG waning gibbous, LQ last quarter and WNC waning crescent.
        /// </summary>
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

        /// <summary>True for the eight phase codes that weather.com and the Open-Meteo data source use.</summary>
        public static bool IsPhaseCode(string? code) => code is "N" or "WXC" or "FQ" or "WXG" or "F" or "WNG" or "LQ" or "WNC";

        /// <summary>
        /// The phase code to show for a day. It is the code from the weather data when that code is
        /// valid, and the computed code of the given moon age otherwise.
        /// </summary>
        public static string CodeOrComputed(string? code, double age)
        {
            var trimmed = code?.Trim();
            return IsPhaseCode(trimmed) ? trimmed! : PhaseCode(age);
        }

        /// <summary>The names that weather.com sends in English.</summary>
        private static readonly string[] EnglishNames =
        {
            "New Moon", "Waxing Crescent", "First Quarter", "Waxing Gibbous",
            "Full Moon", "Waning Gibbous", "Last Quarter", "Waning Crescent"
        };

        /// <summary>
        /// The phase name to show for a forecast day (v55). weather.com's own name is used when it is
        /// in the language of the app. When the day has no name, or weather.com sent the English name
        /// while the app shows another language, the app's own name of the phase is used. The Open-Meteo
        /// data source writes the app's own names, so they pass through unchanged.
        /// </summary>
        public static string DisplayName(string? sourceName, string? code, double age)
        {
            var name = sourceName?.Trim();
            if (!string.IsNullOrEmpty(name))
            {
                var english = Array.Exists(EnglishNames, n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
                if (!english || Loc.IsEnglish)
                    return name!;
            }

            return PhaseName(CodeOrComputed(code, age));
        }

        /// <summary>
        /// The name of a phase from the string resources, in the language of the app. The 33 languages
        /// of the app all have the eight names. The Open-Meteo data source and the recorded past days use
        /// these names. Forecast days from weather.com use them when weather.com has no name in the
        /// language of the app (see DisplayName). English is the fallback.
        /// </summary>
        public static string PhaseName(string code)
        {
            var (key, english) = code switch
            {
                "WXC" => ("MoonPhaseWaxingCrescent", "Waxing Crescent"),
                "FQ" => ("MoonPhaseFirstQuarter", "First Quarter"),
                "WXG" => ("MoonPhaseWaxingGibbous", "Waxing Gibbous"),
                "F" => ("MoonPhaseFull", "Full Moon"),
                "WNG" => ("MoonPhaseWaningGibbous", "Waning Gibbous"),
                "LQ" => ("MoonPhaseLastQuarter", "Last Quarter"),
                "WNC" => ("MoonPhaseWaningCrescent", "Waning Crescent"),
                _ => ("MoonPhaseNew", "New Moon")
            };

            string text;
            try
            {
                text = key.GetLocalized();
            }
            catch (Exception)
            {
                text = "";
            }

            return string.IsNullOrWhiteSpace(text) ? english : text;
        }
    }
}
