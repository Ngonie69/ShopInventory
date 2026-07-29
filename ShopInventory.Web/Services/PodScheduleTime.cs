using System.Globalization;

namespace ShopInventory.Web.Services;

/// <summary>
/// Local-time helpers for POD report email schedules.
///
/// Schedules are authored, stored and evaluated in the business timezone (CAT, UTC+2, no DST)
/// rather than UTC — "Monday 06:00" means Monday 06:00 on the wall clock in the shop, regardless
/// of what timezone the server happens to run in. Only the persisted "last sent" instant stays UTC.
/// </summary>
public static class PodScheduleTime
{
    /// <summary>Minutes in a day; send times are stored as a minute-of-day offset.</summary>
    public const int MinutesPerDay = 24 * 60;

    /// <summary>Short label for the business timezone, shown next to times in the UI.</summary>
    public const string ZoneAbbreviation = "CAT";

    public static TimeZoneInfo Zone => IAuditService.CatTimeZone;

    /// <summary>Current wall-clock time in the business timezone (Kind = Unspecified).</summary>
    public static DateTime NowLocal() => ToLocal(DateTime.UtcNow);

    public static DateTime ToLocal(DateTime utc) => IAuditService.ToCAT(utc);

    public static DateTime ToUtc(DateTime local) => IAuditService.ToUTC(local);

    /// <summary>Clamps an arbitrary value into a valid minute-of-day (0 - 1439).</summary>
    public static int NormalizeMinuteOfDay(int minuteOfDay) => Math.Clamp(minuteOfDay, 0, MinutesPerDay - 1);

    public static TimeSpan ToTimeOfDay(int minuteOfDay) => TimeSpan.FromMinutes(NormalizeMinuteOfDay(minuteOfDay));

    public static int FromTimeOfDay(TimeSpan timeOfDay) => NormalizeMinuteOfDay((int)timeOfDay.TotalMinutes);

    /// <summary>Formats a minute-of-day as "HH:mm" (e.g. 390 -> "06:30").</summary>
    public static string FormatTime(int minuteOfDay)
    {
        var time = ToTimeOfDay(minuteOfDay);
        return string.Create(CultureInfo.InvariantCulture, $"{time.Hours:00}:{time.Minutes:00}");
    }

    /// <summary>Formats a minute-of-day as "HH:mm CAT" for display.</summary>
    public static string FormatTimeWithZone(int minuteOfDay) => $"{FormatTime(minuteOfDay)} {ZoneAbbreviation}";
}
