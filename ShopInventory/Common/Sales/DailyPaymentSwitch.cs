using ShopInventory.Configuration;
using ShopInventory.Models.Entities;

namespace ShopInventory.Common.Sales;

/// <summary>
/// Turns a partner's daily payment on or off. Both ways of changing the switch go through here, so
/// switching on always moves <see cref="IncomingPaymentGlMappingEntity.PaysFrom"/> to that day.
/// </summary>
public static class DailyPaymentSwitch
{
    /// <summary>
    /// Sets the switch. Going from off to on starts paying from today, in CAT. Staying on keeps the
    /// earlier date, and switching off leaves it alone, since it only matters while the partner is on.
    /// </summary>
    public static void Set(IncomingPaymentGlMappingEntity mapping, bool isActive, DateTime utcNow, bool isNew = false)
    {
        if (isActive && (isNew || !mapping.IsActive))
        {
            mapping.PaysFrom = TradingDay(utcNow);
        }

        mapping.IsActive = isActive;
    }

    /// <summary>The CAT calendar date of an instant.</summary>
    public static DateTime TradingDay(DateTime utcNow) =>
        DateTime.SpecifyKind(
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), QuartzConfiguration.CatTimeZone).Date,
            DateTimeKind.Unspecified);
}
