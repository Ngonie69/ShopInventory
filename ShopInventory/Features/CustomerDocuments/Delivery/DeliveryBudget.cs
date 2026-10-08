using System.Globalization;
using ShopInventory.Configuration;
using ShopInventory.Services;

namespace ShopInventory.Features.CustomerDocuments.Delivery;

/// <summary>
/// When documents may go and how many: the CAT day, the automatic window, and the waits between tries.
/// </summary>
/// <remarks>
/// Days are CAT days, because the daily caps are about how the number looks to WhatsApp over a working
/// day, and the working day is Zimbabwe's. Every instant stored is UTC.
/// </remarks>
public static class DeliveryBudget
{
    /// <summary>Midnight CAT at the start of the day <paramref name="nowUtc"/> falls in, as UTC.</summary>
    public static DateTime CatDayStartUtc(DateTime nowUtc) =>
        AuditService.FromCAT(AuditService.ToCAT(nowUtc).Date);

    /// <summary>The next midnight CAT, as UTC.</summary>
    public static DateTime NextCatDayStartUtc(DateTime nowUtc) =>
        AuditService.FromCAT(AuditService.ToCAT(nowUtc).Date.AddDays(1));

    /// <summary>Whether automatic sends may go out at <paramref name="nowUtc"/>.</summary>
    public static bool IsWithinAutoWindow(DateTime nowUtc, CustomerDocumentDeliverySettings settings)
    {
        var cat = AuditService.ToCAT(nowUtc);

        if (cat.DayOfWeek == DayOfWeek.Sunday && !settings.AutoSendOnSundays)
        {
            return false;
        }

        var start = ParseTime(settings.AutoWindowStartCat, new TimeSpan(7, 30, 0));
        var end = ParseTime(settings.AutoWindowEndCat, new TimeSpan(18, 0, 0));

        return cat.TimeOfDay >= start && cat.TimeOfDay < end;
    }

    /// <summary>
    /// How long before a document waiting for its fiscal receipt is looked at again: often at first,
    /// when the receipt is usually seconds away, then hourly.
    /// </summary>
    public static TimeSpan FiscalRecheckDelay(TimeSpan age) => age switch
    {
        _ when age < TimeSpan.FromMinutes(10) => TimeSpan.FromMinutes(2),
        _ when age < TimeSpan.FromMinutes(30) => TimeSpan.FromMinutes(5),
        _ when age < TimeSpan.FromHours(2) => TimeSpan.FromMinutes(15),
        _ => TimeSpan.FromMinutes(60)
    };

    /// <summary>How long after the <paramref name="attempts"/>th send that provably did not leave the next is tried.</summary>
    public static TimeSpan DispatchBackoff(int attempts) => attempts switch
    {
        <= 1 => TimeSpan.FromMinutes(1),
        2 => TimeSpan.FromMinutes(5),
        3 => TimeSpan.FromMinutes(15),
        4 => TimeSpan.FromMinutes(30),
        _ => TimeSpan.FromMinutes(60)
    };

    /// <summary>The least time from one send to the next, with its random part.</summary>
    public static TimeSpan Gap(CustomerDocumentDeliverySettings settings, int jitterSeconds) =>
        TimeSpan.FromSeconds(Math.Max(0, settings.MinSecondsBetweenSends) + Math.Max(0, jitterSeconds));

    private static TimeSpan ParseTime(string? value, TimeSpan fallback) =>
        TimeSpan.TryParseExact(value?.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
}
