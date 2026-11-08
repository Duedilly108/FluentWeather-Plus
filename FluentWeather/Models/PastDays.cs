using System.Collections.Generic;

namespace FluentWeather.Models
{
    /// <summary>
    /// One recorded day of a saved location, for the "Past days" cards.
    /// Date is the location's own local date, as yyyy-MM-dd.
    /// </summary>
    public class PastDayRecord
    {
        public string PlaceId { get; set; } = "";
        public string Date { get; set; } = "";
        public int? High { get; set; }
        public int? Low { get; set; }
        public int? IconCode { get; set; }
        public string? Phrase { get; set; }
        public int? PrecipChance { get; set; }

        // The units of High and Low: "e" is Fahrenheit, the other unit codes are Celsius.
        public string Units { get; set; } = "e";

        public bool LowCaptured { get; set; }
        public bool HighCaptured { get; set; }
    }

    /// <summary>
    /// When to capture a location. The times are minutes after the location's local midnight,
    /// taken from the hourly forecast: the predicted low and the predicted high of the next 24 hours.
    /// </summary>
    public class PastDaySchedule
    {
        public string PlaceId { get; set; } = "";
        public int LowMinutes { get; set; } = 8 * 60;
        public int HighMinutes { get; set; } = 17 * 60;
        public int UtcOffsetMinutes { get; set; }
        public bool OffsetKnown { get; set; }

        // The last capture request that failed, as UTC ticks. The next attempt waits an hour.
        public long LastFailedUtcTicks { get; set; }
    }

    public class PastDaysStore
    {
        public List<PastDayRecord> Records { get; set; } = [];
        public List<PastDaySchedule> Schedules { get; set; } = [];

        // A random delay of 0 to 20 minutes for this PC, added to every capture time, so the
        // captures of many PCs do not reach weather.com at the same minute.
        public int SpreadMinutes { get; set; } = -1;
    }
}
