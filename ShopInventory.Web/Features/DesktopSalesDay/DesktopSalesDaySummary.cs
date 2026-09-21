using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.DesktopSalesDay;

// The namespace deliberately is not ...Features.DesktopSales: the page class is
// DesktopSales, and a namespace of the same name would shadow it wherever both
// are in scope — the page injects ILogger<DesktopSales>.

/// <summary>
/// The two summary cards at the head of /desktop-sales: how the day's sales divide between the device
/// and the back office, and what is still sitting on the device.
///
/// It is here rather than in the page because both cards had been reading a figure that answers a
/// different question, and neither could be tested where it sat.
///
/// The one rule the whole file rests on: <c>EndOfDayReportDto.UnpostedSales</c> lists every sale of the
/// day whose consolidation status is not Consolidated, and nothing else. So the day's total minus that
/// list is the consolidated count, and the list itself is the device's contents — both exactly, without
/// a second read.
/// </summary>
public static class DesktopSalesDaySummary
{
    /// <summary>One of the four counts the day divides into. <c>Family</c> is the Nocturne hue class.</summary>
    public sealed record Lane(string Name, int Count, string Family);

    /// <summary>A customer's share of what is still on the device.</summary>
    public sealed record PartnerValue(string CardCode, string? CardName, decimal TotalAmount)
    {
        /// <summary>The customer by name where its sales carried one, else by code.</summary>
        public string Label => string.IsNullOrWhiteSpace(CardName) ? CardCode : CardName.Trim();

        /// <summary>Both, for the row's tooltip: the name it shows and the code SAP knows it by.</summary>
        public string Title => Label == CardCode ? CardCode : $"{Label} · {CardCode}";
    }

    /// <summary>
    /// The four lanes, all counting <i>sales</i>, adding up to <see cref="EndOfDayReportDto.TotalSalesCount"/>.
    /// </summary>
    /// <remarks>
    /// The consolidated lane cannot be <c>PostedInvoiceCount</c>. That counts the customers the 18:00 run
    /// raised a consolidation document for — a different unit, and zero for a day whose sales reached SAP
    /// one invoice at a time, which is how the till and van routes post. A day of three such sales drew
    /// four empty lanes under a tally of three.
    /// </remarks>
    public static List<Lane> Lanes(EndOfDayReportDto? report)
    {
        if (report == null)
        {
            return new();
        }

        var listed = report.UnpostedSales;
        var failed = listed.Count(s => Is(s.ConsolidationStatus, "Failed"));
        var excluded = listed.Count(s => Is(s.ConsolidationStatus, "Excluded"));

        // Whatever the unposted list does not explain is still awaiting close, so an unmapped status
        // lands in the lane the Consolidate button acts on rather than vanishing from the strip.
        var awaiting = Math.Max(0, report.UnpostedInvoiceCount - failed - excluded);

        return new()
        {
            new Lane("Awaiting close", awaiting, "ops-fam-accent"),
            new Lane("Consolidated", ConsolidatedCount(report), "ops-fam-good"),
            new Lane("Failed", failed, "ops-fam-bad"),
            new Lane("Excluded", excluded, "ops-fam-neutral")
        };
    }

    /// <summary>Sales the day no longer owes SAP a document for.</summary>
    public static int ConsolidatedCount(EndOfDayReportDto? report) =>
        report == null ? 0 : Math.Max(0, report.TotalSalesCount - report.UnpostedInvoiceCount);

    /// <summary>What the device still holds, in the report's currency.</summary>
    public static decimal AwaitingValue(EndOfDayReportDto? report) =>
        report?.UnpostedSales.Sum(s => s.Amount) ?? 0m;

    /// <summary>
    /// The customers that value is made of, largest first.
    /// </summary>
    /// <remarks>
    /// Built from the unposted rows, not from <c>BusinessPartnerSummaries</c>, which is the whole day
    /// including everything already consolidated. Under a headline that is deliberately not the day's
    /// total, the day-wide breakdown put a customer and a four-figure amount beneath "USD 0.00".
    /// </remarks>
    public static List<PartnerValue> OnDevice(EndOfDayReportDto? report, int take = 5) =>
        report?.UnpostedSales
            .GroupBy(s => s.CardCode)
            .Select(g => new PartnerValue(g.Key, g.First().CardName, g.Sum(s => s.Amount)))
            .Where(p => p.TotalAmount > 0)
            .OrderByDescending(p => p.TotalAmount)
            .ThenBy(p => p.CardCode, StringComparer.Ordinal)
            .Take(take)
            .ToList() ?? new();

    private static bool Is(string? status, string value) =>
        string.Equals(status, value, StringComparison.OrdinalIgnoreCase);
}
