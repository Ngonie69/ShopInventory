namespace ShopInventory.Web.Features.Reports.Queries.GetItemVolumeSalesReport;

/// <summary>
/// The returnable crates, and how to take them back out of a loaded report.
/// </summary>
/// <remarks>
/// <para>
/// A crate is not a product. It goes out on the invoice beside the yoghurt and comes back on a
/// credit note when the customer returns it, so it moves quantity and money across the window
/// without ever being something the company sold. It also carries no conversion factor and never
/// will — a crate holds litres, it is not made of them — so on the volume lens every crate code
/// lands in the "no factor" notice, which is the one place that notice is not naming a gap somebody
/// ought to fill.
/// </para>
/// <para>
/// The exclusion is applied to the loaded result rather than to the request. The report is asked for
/// by item code and an exclusion is not expressible there; doing it here means the toggle re-reads
/// the window already in hand instead of asking SAP for it a second time, and the same filtered
/// result feeds the pivot, the chart and the workbook, so those three can never disagree about
/// whether a crate was counted.
/// </para>
/// </remarks>
public static class ItemVolumeSalesCrates
{
    /// <summary>
    /// CRA001–CRA006. Stated rather than matched on the <c>CRA</c> prefix: a code is a crate
    /// because the business says so, and a future code that merely sorts into the same block must
    /// not vanish from a report for looking the part.
    /// </summary>
    /// <remarks>
    /// Wider than the API's own crate set in <c>GetPodUploadStatusHandler</c>, which names only
    /// CRA001, CRA002, CRA003 and CRA006 because those are the codes that raise a crate POD. This
    /// list covers the whole block, which is what the report is asked to leave out.
    /// </remarks>
    public static readonly IReadOnlyList<string> Codes =
    [
        "CRA001",
        "CRA002",
        "CRA003",
        "CRA004",
        "CRA005",
        "CRA006"
    ];

    private static readonly HashSet<string> Lookup = new(Codes, StringComparer.OrdinalIgnoreCase);

    public static bool IsCrate(string? itemCode) =>
        !string.IsNullOrWhiteSpace(itemCode) && Lookup.Contains(itemCode);

    /// <summary>Crate codes that actually moved in this window, in the order the report lists them.</summary>
    public static List<string> CratesIn(GetItemVolumeSalesReportResult report) => report.ItemTotals
        .Where(item => IsCrate(item.ItemCode))
        .Select(item => item.ItemCode)
        .ToList();

    /// <summary>
    /// The same window with every crate line taken out of it. Returns the report unchanged when no
    /// crate moved, so the common case allocates nothing.
    /// </summary>
    /// <remarks>
    /// Every figure is reached by subtracting the crates' own contribution rather than by re-summing
    /// what is left, so a rounding difference between the API's aggregate and its parts cannot leak
    /// in here. The one thing that cannot be reached that way is the document counts: an invoice
    /// that carried a crate is still an invoice, and a credit note raised for nothing but returned
    /// crates is still a credit note that was raised. Those counts are left alone, and the page says
    /// so where it prints them.
    /// </remarks>
    public static GetItemVolumeSalesReportResult Exclude(GetItemVolumeSalesReportResult report)
    {
        var dropped = report.ItemTotals.Where(item => IsCrate(item.ItemCode)).ToList();

        if (dropped.Count == 0)
        {
            return report;
        }

        return new GetItemVolumeSalesReportResult
        {
            GeneratedAtUtc = report.GeneratedAtUtc,
            FromDateUtc = report.FromDateUtc,
            ToDateUtc = report.ToDateUtc,
            Grouping = report.Grouping,
            RequestedAccountCodes = report.RequestedAccountCodes,

            // What was asked of SAP, which the exclusion does not change.
            RequestedItemCodes = report.RequestedItemCodes,

            ItemCodesWithoutFactor = report.ItemCodesWithoutFactor
                .Where(code => !IsCrate(code))
                .ToList(),

            Summary = StripSummary(report.Summary, dropped),
            ItemTotals = report.ItemTotals.Where(item => !IsCrate(item.ItemCode)).ToList(),
            AccountTotals = report.AccountTotals.Select(StripAccount).ToList(),
            Periods = report.Periods.Select(StripPeriod).ToList(),
            DocumentLines = report.DocumentLines.Where(line => !IsCrate(line.ItemCode)).ToList()
        };
    }

    private static ItemVolumeSalesSummaryResult StripSummary(
        ItemVolumeSalesSummaryResult summary,
        List<ItemVolumeSalesItemResult> dropped) =>
        new()
        {
            RequestedAccountCount = summary.RequestedAccountCount,
            ActiveAccountCount = summary.ActiveAccountCount,
            RequestedItemCount = summary.RequestedItemCount,
            ItemCount = Math.Max(0, summary.ItemCount - dropped.Count),
            TotalPeriods = summary.TotalPeriods,

            InvoiceCount = summary.InvoiceCount,
            CreditNoteCount = summary.CreditNoteCount,

            InvoicedQuantity = summary.InvoicedQuantity - dropped.Sum(item => item.InvoicedQuantity),
            CreditedQuantity = summary.CreditedQuantity - dropped.Sum(item => item.CreditedQuantity),
            NetQuantity = summary.NetQuantity - dropped.Sum(item => item.NetQuantity),

            InvoicedVolume = summary.InvoicedVolume - dropped.Sum(item => item.InvoicedVolume),
            CreditedVolume = summary.CreditedVolume - dropped.Sum(item => item.CreditedVolume),
            NetVolume = summary.NetVolume - dropped.Sum(item => item.NetVolume),

            ItemsWithoutFactorCount = Math.Max(
                0,
                summary.ItemsWithoutFactorCount - dropped.Count(item => !item.HasVolumeFactor)),
            QuantityWithoutFactor = summary.QuantityWithoutFactor
                - dropped.Where(item => !item.HasVolumeFactor).Sum(item => item.NetQuantity),

            InvoicedSalesUsd = summary.InvoicedSalesUsd - dropped.Sum(item => item.InvoicedSalesUsd),
            InvoicedSalesZig = summary.InvoicedSalesZig - dropped.Sum(item => item.InvoicedSalesZig),
            CreditedSalesUsd = summary.CreditedSalesUsd - dropped.Sum(item => item.CreditedSalesUsd),
            CreditedSalesZig = summary.CreditedSalesZig - dropped.Sum(item => item.CreditedSalesZig),
            NetRevenueUsd = summary.NetRevenueUsd - dropped.Sum(item => item.NetRevenueUsd),
            NetRevenueZig = summary.NetRevenueZig - dropped.Sum(item => item.NetRevenueZig)
        };

    private static ItemVolumeSalesAccountResult StripAccount(ItemVolumeSalesAccountResult account)
    {
        var kept = account.Items.Where(item => !IsCrate(item.ItemCode)).ToList();
        var dropped = account.Items.Where(item => IsCrate(item.ItemCode)).ToList();

        return new ItemVolumeSalesAccountResult
        {
            CardCode = account.CardCode,
            CardName = account.CardName,

            InvoiceCount = account.InvoiceCount,
            CreditNoteCount = account.CreditNoteCount,

            InvoicedQuantity = account.InvoicedQuantity - dropped.Sum(item => item.InvoicedQuantity),
            CreditedQuantity = account.CreditedQuantity - dropped.Sum(item => item.CreditedQuantity),
            NetQuantity = account.NetQuantity - dropped.Sum(item => item.NetQuantity),

            NetVolume = account.NetVolume - dropped.Sum(item => item.NetVolume),
            InvoicedVolume = account.InvoicedVolume - dropped.Sum(item => item.InvoicedVolume),
            CreditedVolume = account.CreditedVolume - dropped.Sum(item => item.CreditedVolume),

            ItemsWithoutFactorCount = kept.Count(item => !item.HasVolumeFactor),

            InvoicedSalesUsd = account.InvoicedSalesUsd - dropped.Sum(item => item.InvoicedSalesUsd),
            InvoicedSalesZig = account.InvoicedSalesZig - dropped.Sum(item => item.InvoicedSalesZig),
            CreditedSalesUsd = account.CreditedSalesUsd - dropped.Sum(item => item.CreditedSalesUsd),
            CreditedSalesZig = account.CreditedSalesZig - dropped.Sum(item => item.CreditedSalesZig),
            NetRevenueUsd = account.NetRevenueUsd - dropped.Sum(item => item.NetRevenueUsd),
            NetRevenueZig = account.NetRevenueZig - dropped.Sum(item => item.NetRevenueZig),

            Items = kept
        };
    }

    private static ItemVolumeSalesPeriodResult StripPeriod(ItemVolumeSalesPeriodResult period)
    {
        var dropped = period.Accounts
            .SelectMany(account => account.Items)
            .Where(item => IsCrate(item.ItemCode))
            .ToList();

        return new ItemVolumeSalesPeriodResult
        {
            Label = period.Label,
            PeriodStartUtc = period.PeriodStartUtc,
            PeriodEndUtc = period.PeriodEndUtc,

            InvoiceCount = period.InvoiceCount,
            CreditNoteCount = period.CreditNoteCount,

            NetQuantity = period.NetQuantity - dropped.Sum(item => item.NetQuantity),
            NetVolume = period.NetVolume - dropped.Sum(item => item.NetVolume),
            NetRevenueUsd = period.NetRevenueUsd - dropped.Sum(item => item.NetRevenueUsd),
            NetRevenueZig = period.NetRevenueZig - dropped.Sum(item => item.NetRevenueZig),

            Accounts = period.Accounts.Select(StripAccount).ToList()
        };
    }
}
