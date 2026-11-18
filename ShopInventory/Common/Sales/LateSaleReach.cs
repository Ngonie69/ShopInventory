using System.Linq.Expressions;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Common.Sales;

/// <summary>
/// How a till or vending sale that was signed, or posted, long after it was made stays inside the posting
/// and daily payment windows.
/// </summary>
/// <remarks>
/// <para>
/// Both windows count back from a sale's <see cref="DesktopSaleEntity.DocDate"/>: three days for posting,
/// seven for the daily payment. <see cref="Sap.SapOutageReach"/> widens them for a SAP outage, because
/// one is recorded. A fiscal outage has no record, and is often one device rather than the whole
/// estate, so a sale that failed to sign on the first day of a four-day REVMax outage was too old to post
/// on its own once it was finally signed, and an invoice posted by hand more than a week late was never
/// put on a daily payment. It stayed open in SAP for finance to settle.
/// </para>
/// <para>
/// So the sale's own times widen each window instead. Posting also takes a sale signed within its window
/// (<see cref="DesktopSaleEntity.FiscalisedAtUtc"/>), and the daily payment also takes an invoice posted
/// within its window (<see cref="DesktopSaleEntity.PostedAt"/>). Neither reaches past
/// <see cref="Configuration.SapAvailabilitySettings.MaxLookbackExtensionDays"/>, the furthest back a SAP
/// outage may take them, so a sale signed or posted months late is still left for a person.
/// </para>
/// </remarks>
public static class LateSaleReach
{
    /// <summary>The oldest sale date either widening reaches: today in CAT, less the outage limit.</summary>
    public static DateTime OldestSaleDate(int maxExtensionDays, DateTime nowUtc) =>
        AuditService.ToCAT(nowUtc).Date.AddDays(-Math.Max(0, maxExtensionDays));

    /// <summary>
    /// The sales the posting pass may take: made on or after <paramref name="cutoffDate"/>, or signed at or
    /// after <paramref name="signedSinceUtc"/> and made on or after <paramref name="oldestSaleDate"/>.
    /// </summary>
    public static Expression<Func<DesktopSaleEntity, bool>> InPostingReach(
        DateTime cutoffDate,
        DateTime signedSinceUtc,
        DateTime oldestSaleDate) =>
        sale => sale.DocDate >= cutoffDate
                || (sale.FiscalisedAtUtc != null
                    && sale.FiscalisedAtUtc >= signedSinceUtc
                    && sale.DocDate >= oldestSaleDate);
}
