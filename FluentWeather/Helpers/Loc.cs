using System;
using System.Globalization;

namespace FluentWeather.Helpers
{
    /// <summary>
    /// Local build (v55): texts from the string resources in the language of the app. The English text
    /// of the code is the fallback, for a missing key or when the resources cannot be read.
    /// </summary>
    internal static class Loc
    {
        /// <summary>The text of the key, or the English text.</summary>
        public static string Get(string key, string english)
        {
            try
            {
                var text = key.GetLocalized();
                return string.IsNullOrEmpty(text) ? english : text;
            }
            catch (Exception)
            {
                return english;
            }
        }

        /// <summary>The text of the key with the values filled in at {0}, {1} and so on.</summary>
        public static string Format(string key, string english, params object?[] values)
        {
            var format = Get(key, english);
            try
            {
                return string.Format(CultureInfo.CurrentCulture, format, values);
            }
            catch (FormatException)
            {
                return string.Format(CultureInfo.CurrentCulture, english, values);
            }
        }

        /// <summary>The culture of the language the app shows, for day names. The culture of Windows is the fallback.</summary>
        public static CultureInfo Culture
        {
            get
            {
                try
                {
                    var languages = Windows.Globalization.ApplicationLanguages.Languages;
                    if (languages.Count > 0)
                        return new CultureInfo(languages[0]);
                }
                catch (Exception)
                {
                    // The fallback applies.
                }

                return CultureInfo.CurrentUICulture;
            }
        }

        /// <summary>True when the app shows English.</summary>
        public static bool IsEnglish
        {
            get
            {
                try
                {
                    var languages = Windows.Globalization.ApplicationLanguages.Languages;
                    return languages.Count == 0 || languages[0].StartsWith("en", StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception)
                {
                    return true;
                }
            }
        }
    }
}
