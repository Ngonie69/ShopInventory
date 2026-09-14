using System.Globalization;
using ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesAnalysis;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.Vending;

/// <summary>
/// The figures on /vending/sales, worked out from the desktop sales analysis and list confined to the
/// vending source: the month so far, the last fortnight day by day, each depot's share and the tenders.
///
/// As on a vendor's page, money is never added across currencies. Every figure is read in the currency
/// the depots sold in most, and the others are named rather than converted.
///
/// A depot is its business partner, and the analysis breaks sales down by warehouse, so a depot's figure
/// is the sum of the warehouses its cashiers draw from. A warehouse no depot claims is kept as a row of
/// its own rather than dropped: those are real vending sales, and a total that did not add up would be
/// read as a missing sale.
/// </summary>
public static class VendingSalesDigest
{
    public const string SourceSystem = "KefalosVending";

    public enum Range { Today, Week, Month }

    public sealed record Day(DateTime Date, decimal Gross, int SaleCount);

    public sealed record DepotRow(string Key, string Name, int SaleCount, decimal Gross, bool IsDepot);

    public sealed record Tender(string Name, decimal SharePercent);

    /// <summary>The first business date a range of the list covers, ending today.</summary>
    public static DateTime RangeStart(Range range, DateTime today) => range switch
    {
        Range.Today => today.Date,
        Range.Week => today.Date.AddDays(-6),
        _ => VendorSalesDigest.MonthStart(today.Date)
    };

    /// <summary>
    /// The currency most sales were made in, by count and then value, or null when nothing sold.
    /// </summary>
    public static string? PrimaryCurrency(DesktopSalesAnalysisResult? result) =>
        result?.Currencies
            .Where(section => section.SalesCount > 0)
            .OrderByDescending(section => section.SalesCount)
            .ThenByDescending(section => section.TotalAmount)
            .ThenBy(section => section.Currency, StringComparer.Ordinal)
            .Select(section => section.Currency)
            .FirstOrDefault();

    public static DesktopSalesCurrencyAnalysis? Section(DesktopSalesAnalysisResult? result, string? currency) =>
        currency is null
            ? null
            : result?.Currencies.FirstOrDefault(section => string.Equals(section.Currency, currency, StringComparison.OrdinalIgnoreCase));

    /// <summary>The currencies other than <paramref name="currency"/> that traded, as "ZWG 1,204.00".</summary>
    public static List<string> OtherCurrencies(DesktopSalesAnalysisResult? result, string? currency) =>
        (result?.Currencies ?? [])
            .Where(section => section.SalesCount > 0 && !string.Equals(section.Currency, currency, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(section => section.SalesCount)
            .Select(section => VendorSalesDigest.Money(section.Currency, section.TotalAmount))
            .ToList();

    /// <summary>
    /// Every day from <paramref name="from"/> to <paramref name="to"/>, a day without sales included as a
    /// zero so the chart shows the gap.
    /// </summary>
    public static List<Day> Daily(DesktopSalesCurrencyAnalysis? section, DateTime from, DateTime to)
    {
        var days = new List<Day>();
        for (var date = from.Date; date <= to.Date; date = date.AddDays(1))
        {
            var row = section?.ByDay.FirstOrDefault(day => day.Date.Date == date);
            days.Add(new Day(date, row?.TotalAmount ?? 0m, row?.SalesCount ?? 0));
        }

        return days;
    }

    /// <summary>
    /// Each depot's sales, from the warehouses its cashiers draw from, largest first; a depot that sold
    /// nothing is listed at zero, and a warehouse no depot claims gets a row of its own.
    /// </summary>
    public static List<DepotRow> ByDepot(
        DesktopSalesCurrencyAnalysis? section,
        IReadOnlyList<VendingDepotModel> depots,
        Func<string, string> depotName)
    {
        var warehouses = (section?.ByWarehouse ?? [])
            .GroupBy(row => row.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (Count: group.Sum(row => row.SalesCount), Gross: group.Sum(row => row.TotalAmount)),
                StringComparer.OrdinalIgnoreCase);

        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<DepotRow>();

        foreach (var depot in depots)
        {
            var count = 0;
            var gross = 0m;
            foreach (var code in depot.WarehouseCodes)
            {
                // A warehouse shared by two depots is counted once, against the first.
                if (claimed.Add(code) && warehouses.TryGetValue(code, out var sold))
                {
                    count += sold.Count;
                    gross += sold.Gross;
                }
            }

            rows.Add(new DepotRow(depot.BusinessPartnerCode, depotName(depot.BusinessPartnerCode), count, gross, IsDepot: true));
        }

        rows.AddRange(warehouses
            .Where(entry => !claimed.Contains(entry.Key))
            .Select(entry => new DepotRow(entry.Key, $"Warehouse {entry.Key}", entry.Value.Count, entry.Value.Gross, IsDepot: false)));

        return rows
            .OrderByDescending(row => row.Gross)
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The tenders taken, by share of value, leaving out the ones that took nothing.</summary>
    public static List<Tender> Tenders(DesktopSalesCurrencyAnalysis? section) =>
        (section?.ByPaymentMethod ?? [])
            .Where(row => row.SalesCount > 0)
            .OrderByDescending(row => row.ShareOfValuePercent)
            .Select(row => new Tender(row.PaymentMethod, row.ShareOfValuePercent))
            .ToList();

    /// <summary>
    /// The change on the same number of days just before, or null where there were none before — a rise
    /// from nothing is not a percentage.
    /// </summary>
    public static decimal? ChangePercent(DesktopSalesCurrencyAnalysis? section) =>
        section is { PreviousTotalAmount: > 0 }
            ? Math.Round((section.TotalAmount - section.PreviousTotalAmount) / section.PreviousTotalAmount * 100m, 1)
            : null;

    /// <summary>
    /// Where a listed sale got to on its way to SAP, from the list's consolidation status. The same four
    /// states a vendor's page reads from the route-customer report's sentence.
    /// </summary>
    public static VendorSalesDigest.PostingState PostingOf(string? consolidationStatus) => consolidationStatus switch
    {
        _ when string.Equals(consolidationStatus, "Consolidated", StringComparison.OrdinalIgnoreCase) => VendorSalesDigest.PostingState.Posted,
        _ when string.Equals(consolidationStatus, "Failed", StringComparison.OrdinalIgnoreCase) => VendorSalesDigest.PostingState.Failed,
        _ when string.Equals(consolidationStatus, "Excluded", StringComparison.OrdinalIgnoreCase) => VendorSalesDigest.PostingState.Excluded,
        _ => VendorSalesDigest.PostingState.Awaiting
    };

    /// <summary>When a sale was rung up, in CAT: "Today 11:42", "Yesterday 16:20", or "12 Sep 08:40".</summary>
    public static string When(DateTime createdAtUtc, DateTime today)
    {
        var at = IAuditService.ToCAT(createdAtUtc);
        var time = at.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (at.Date == today.Date)
        {
            return $"Today {time}";
        }

        return at.Date == today.Date.AddDays(-1)
            ? $"Yesterday {time}"
            : at.ToString("dd MMM HH:mm", CultureInfo.InvariantCulture);
    }
}
