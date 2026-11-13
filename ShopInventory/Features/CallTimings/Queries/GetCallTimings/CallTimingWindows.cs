using ShopInventory.Configuration;

namespace ShopInventory.Features.CallTimings.Queries.GetCallTimings;

/// <summary>The windows the call timings page can be read over.</summary>
public static class CallTimingWindows
{
    public const string FifteenMinutes = "15m";
    public const string OneHour = "1h";
    public const string Today = "today";

    public static readonly IReadOnlyList<string> All = [FifteenMinutes, OneHour, Today];

    public static bool IsKnown(string? window) =>
        window is not null && All.Contains(window, StringComparer.OrdinalIgnoreCase);

    /// <summary>When <paramref name="window"/> starts, UTC. <c>today</c> is from midnight CAT.</summary>
    public static DateTime StartOf(string window, DateTime utcNow) => window.ToLowerInvariant() switch
    {
        FifteenMinutes => utcNow.AddMinutes(-15),
        OneHour => utcNow.AddHours(-1),
        _ => CatMidnightUtc(utcNow),
    };

    private static DateTime CatMidnightUtc(DateTime utcNow)
    {
        var zone = QuartzConfiguration.CatTimeZone;
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), zone);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local.Date, DateTimeKind.Unspecified), zone);
    }
}
