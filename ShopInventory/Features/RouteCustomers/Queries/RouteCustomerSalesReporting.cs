using ShopInventory.DTOs;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.RouteCustomers.Queries;

/// <summary>
/// The pieces all three route-customer sales reports need in common: how a date window is read, and how
/// money is added up.
///
/// A van sale is recorded in one of two tables and they do not keep time the same way. An offline sale
/// carries <c>DocDate</c>, a bare trading day in the van's own clock. An online sale is a confirmed stock
/// reservation stamped <c>CreatedAt</c>, a UTC instant. Reading both against one window means converting
/// the second, and CAT is two hours ahead of UTC, so a sale made at 09:00 on the 1st is stored at 07:00
/// on the 1st and one made at 00:30 on the 2nd is stored at 22:30 on the 1st. Filtering the instants on
/// bare dates would move that second sale into the previous day's takings.
/// </summary>
internal static class RouteCustomerSalesReporting
{
    /// <summary>How far back an unbounded report looks. Long enough to see a trading pattern, short
    /// enough that a route with no filter set does not scan every sale ever made.</summary>
    public const int DefaultWindowDays = 90;

    /// <summary>Days without a sale after which a customer is called dormant, unless the caller says otherwise.</summary>
    public const int DefaultDormantDays = 30;

    /// <summary>
    /// Reads the requested window as whole trading days, clamping it so <c>to</c> never precedes
    /// <c>from</c> and an open-ended request gets a bounded one.
    /// </summary>
    public static (DateTime From, DateTime To) NormalizeWindow(DateTime? from, DateTime? to, int defaultDays = DefaultWindowDays)
    {
        var today = AuditService.ToCAT(DateTime.UtcNow).Date;
        var resolvedTo = (to?.Date ?? today);
        var resolvedFrom = from?.Date ?? resolvedTo.AddDays(-Math.Max(1, defaultDays) + 1);

        if (resolvedFrom > resolvedTo)
        {
            resolvedFrom = resolvedTo;
        }

        return (resolvedFrom, resolvedTo);
    }

    /// <summary>
    /// The same window as the UTC half-open interval a <c>timestamptz</c> column must be compared
    /// against. Half-open because the closing bound is the first instant of the next day: comparing
    /// <c>&lt;=</c> against an inclusive end would either drop the last day's evening trading or need a
    /// tick-precision bound that rounds differently in the database than it does here.
    /// </summary>
    public static (DateTime FromUtc, DateTime ToUtcExclusive) ToUtcWindow(DateTime fromDate, DateTime toDate) =>
        (AuditService.FromCAT(fromDate.Date), AuditService.FromCAT(toDate.Date.AddDays(1)));

    /// <summary>
    /// Folds per-currency subtotals into the list the DTOs carry, biggest first.
    ///
    /// Never collapses them into one figure. A van that took USD 40 and ZWG 900 did not take 940 of
    /// anything, and a report that says so is worse than one that says nothing.
    /// </summary>
    public static List<RouteCustomerSalesTotalsDto> SumByCurrency(IEnumerable<RouteCustomerSalesTotalsDto> parts) =>
        parts
            .GroupBy(part => NormalizeCurrency(part.Currency), StringComparer.OrdinalIgnoreCase)
            .Select(group => new RouteCustomerSalesTotalsDto
            {
                Currency = group.Key,
                SaleCount = group.Sum(part => part.SaleCount),
                Gross = group.Sum(part => part.Gross),
                Vat = group.Sum(part => part.Vat),
                AmountPaid = group.Sum(part => part.AmountPaid)
            })
            .OrderByDescending(total => total.Gross)
            .ThenBy(total => total.Currency, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static string NormalizeCurrency(string? currency) =>
        string.IsNullOrWhiteSpace(currency) ? "USD" : currency.Trim().ToUpperInvariant();

    /// <summary>
    /// Whole days between a customer's last sale and today, or null if they have never bought.
    ///
    /// Null and a large number are different findings. A shop that has bought nothing for 200 days has
    /// lapsed; one that has never bought was captured and never converted, and needs a different
    /// conversation.
    /// </summary>
    public static int? DaysSinceLastSale(DateTime? lastSaleAt)
    {
        if (!lastSaleAt.HasValue)
        {
            return null;
        }

        var today = AuditService.ToCAT(DateTime.UtcNow).Date;
        return Math.Max(0, (int)(today - lastSaleAt.Value.Date).TotalDays);
    }

    /// <summary>
    /// Where an offline van sale has got to, in words. Consolidation status is the spine of the answer —
    /// it is what says whether SAP has the sale — with the receipt's own fate added only when it is
    /// something a person has to act on.
    /// </summary>
    public static string DescribeOfflineSaleStatus(
        DesktopSaleConsolidationStatus consolidation,
        DesktopSaleReceiptIngestStatus receiptIngest,
        int? sapDocNum)
    {
        var posting = consolidation switch
        {
            DesktopSaleConsolidationStatus.Consolidated when sapDocNum.HasValue => $"Posted as {sapDocNum}",
            DesktopSaleConsolidationStatus.Consolidated => "Posted",
            DesktopSaleConsolidationStatus.Failed => "Posting failed",
            DesktopSaleConsolidationStatus.Excluded => "Excluded from posting",
            _ => "Awaiting posting"
        };

        return receiptIngest switch
        {
            DesktopSaleReceiptIngestStatus.ChainBroken => $"{posting} — receipt chain broken",
            DesktopSaleReceiptIngestStatus.Unsignable => $"{posting} — receipt unsignable",
            DesktopSaleReceiptIngestStatus.Failed => $"{posting} — receipt not yet with ZIMRA",
            _ => posting
        };
    }
}
