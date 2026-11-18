using ShopInventory.Web.Services;

namespace ShopInventory.Web.Common;

/// <summary>
/// When a shop's stock day opens. The API's morning snapshot is taken at 07:00 CAT and governs until
/// the next one (StockLedgerDay in the API), so "today" for stock starts there and not at midnight:
/// at 06:30 the shop is still on yesterday's figures.
/// </summary>
public static class StockDayClock
{
    /// <summary>The fetch time the API ships with (DailyStock:StockFetchTimeCAT).</summary>
    public static readonly TimeSpan FetchTimeCat = new(7, 0, 0);

    /// <summary>The CAT moment the stock day in force at <paramref name="utcNow"/> opened.</summary>
    public static DateTime OpeningCat(DateTime utcNow)
    {
        var cat = IAuditService.ToCAT(utcNow);
        var opening = cat.Date + FetchTimeCat;
        return cat < opening ? opening.AddDays(-1) : opening;
    }

    /// <summary>The same moment in UTC.</summary>
    public static DateTime OpeningUtc(DateTime utcNow) => IAuditService.ToUTC(OpeningCat(utcNow));
}
