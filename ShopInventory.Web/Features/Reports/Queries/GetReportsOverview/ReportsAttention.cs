using static ShopInventory.Web.Features.Reports.Queries.GetReportsOverview.ReportsHubFormat;

namespace ShopInventory.Web.Features.Reports.Queries.GetReportsOverview;

/// <summary>
/// Decides what the Reports Hub's "Needs attention" list says. Each rule reads one report the hub
/// already loaded and says nothing when that report has not arrived or shows nothing wrong.
/// </summary>
public static class ReportsAttention
{
    /// <summary>A share of receivables over 90 days at or above this is critical rather than one to watch.</summary>
    public const decimal Over90CriticalPercent = 10m;

    /// <summary>Credit notes are flagged from this share of sales…</summary>
    public const decimal CreditRatioFloorPercent = 2m;

    /// <summary>…when they rose by at least this many points on the period before (or there is no period before).</summary>
    public const decimal CreditRatioRisePoints = 0.5m;

    public static IReadOnlyList<ReportsAttentionItem> Build(
        ReportsPeriod period,
        ReportsHeadlineResult? headline,
        ReportsActivityResult? activity,
        ReportsStockWatchResult? stock)
    {
        var items = new List<ReportsAttentionItem>();

        AddLowStock(items, period, stock?.LowStock);
        AddOverdue(items, period, headline?.Aging);
        AddOpenOrders(items, period, activity?.Orders);
        AddCreditNotes(items, period, activity?.CreditNotes, headline?.Previous);
        AddSlowMoving(items, period, stock?.SlowMoving);

        // Stable: within a severity, the order above is the order of importance.
        return items.OrderBy(item => item.Severity).ToList();
    }

    private static void AddLowStock(List<ReportsAttentionItem> items, ReportsPeriod period, Models.LowStockAlertReport? report)
    {
        if (report is null)
        {
            return;
        }

        var href = ReportsHubLinks.Tab(ReportsHubLinks.LowStock, period);
        var warehouses = report.Items.Select(item => item.WarehouseCode).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var across = warehouses > 0 ? $" · {Count(warehouses)} {Plural(warehouses, "warehouse", "warehouses")}" : string.Empty;

        if (report.CriticalCount > 0)
        {
            var more = report.WarningCount > 0
                ? $"{Count(report.WarningCount)} more below reorder level"
                : "Out of stock or nearly";
            items.Add(new(ReportsAttentionSeverity.Critical,
                $"{Count(report.CriticalCount)} stock {Plural(report.CriticalCount, "line", "lines")} critically low",
                more + across, "Low stock", href));
        }
        else if (report.WarningCount > 0)
        {
            items.Add(new(ReportsAttentionSeverity.Watch,
                $"{Count(report.WarningCount)} stock {Plural(report.WarningCount, "line", "lines")} below reorder level",
                "None critical yet" + across, "Low stock", href));
        }
    }

    private static void AddOverdue(List<ReportsAttentionItem> items, ReportsPeriod period, Models.ReceivablesAgingReport? aging)
    {
        var over90 = aging?.Over90Days;
        if (aging is null || over90 is null || over90.AmountUSD <= 0)
        {
            return;
        }

        var owing = aging.CustomerAging
            .Where(customer => customer.Over90DaysUSD > 0)
            .OrderByDescending(customer => customer.Over90DaysUSD)
            .ToList();
        var detail = owing.Count switch
        {
            0 => $"{Count(over90.InvoiceCount)} {Plural(over90.InvoiceCount, "invoice", "invoices")}",
            1 => $"All of it {owing[0].CardName}",
            _ => $"{Count(owing.Count)} customers · largest {owing[0].CardName} ({Usd(owing[0].Over90DaysUSD)})"
        };

        items.Add(new(
            over90.PercentOfTotal >= Over90CriticalPercent ? ReportsAttentionSeverity.Critical : ReportsAttentionSeverity.Watch,
            $"{Usd(over90.AmountUSD)} owed for more than 90 days",
            detail, "Receivables", ReportsHubLinks.Tab(ReportsHubLinks.Aging, period)));
    }

    private static void AddOpenOrders(List<ReportsAttentionItem> items, ReportsPeriod period, Models.OrderFulfillmentReport? orders)
    {
        if (orders is null || orders.OpenOrders <= 0)
        {
            return;
        }

        var waiting = orders.TotalPendingValueUSD > 0
            ? $"{Usd(orders.TotalPendingValueUSD)} not yet invoiced"
            : "Not yet invoiced";
        items.Add(new(ReportsAttentionSeverity.Watch,
            $"{Count(orders.OpenOrders)} sales {Plural(orders.OpenOrders, "order", "orders")} still open",
            $"{waiting} · raised in this period", "Orders", ReportsHubLinks.Tab(ReportsHubLinks.Fulfillment, period)));
    }

    private static void AddCreditNotes(
        List<ReportsAttentionItem> items,
        ReportsPeriod period,
        Models.CreditNoteSummaryReport? creditNotes,
        Models.ProfitOverviewReport? previous)
    {
        if (creditNotes is null || creditNotes.CreditToSalesRatioPercent < CreditRatioFloorPercent)
        {
            return;
        }

        decimal? before = previous is { TotalRevenueUSD: > 0 }
            ? Math.Round(previous.TotalCreditNotesUSD / previous.TotalRevenueUSD * 100m, 1)
            : null;
        if (before is not null && creditNotes.CreditToSalesRatioPercent - before.Value < CreditRatioRisePoints)
        {
            return;
        }

        var why = before is not null
            ? $"Up from {Percent(before.Value)} the period before"
            : $"{Count(creditNotes.TotalCreditNotes)} credit {Plural(creditNotes.TotalCreditNotes, "note", "notes")} for {Usd(creditNotes.TotalCreditAmountUSD)}";
        var most = creditNotes.ByCustomer.OrderByDescending(customer => customer.TotalAmountUSD).FirstOrDefault();
        if (most is not null && !string.IsNullOrWhiteSpace(most.CardName))
        {
            why += $" · most to {most.CardName}";
        }

        items.Add(new(ReportsAttentionSeverity.Watch,
            $"Credit notes at {Percent(creditNotes.CreditToSalesRatioPercent)} of sales",
            why, "Credit notes", ReportsHubLinks.Tab(ReportsHubLinks.CreditNotes, period)));
    }

    private static void AddSlowMoving(List<ReportsAttentionItem> items, ReportsPeriod period, Models.SlowMovingProductsReport? report)
    {
        if (report is null || report.Products.Count == 0)
        {
            return;
        }

        var count = report.Products.Count;
        var neverSold = report.Products.Count(product => product.LastSoldDate is null);
        var detail = neverSold > 0
            ? $"{Count(neverSold)} with no sale in the last {GetReportsStockWatchQuery.SalesLookbackDays / 30} months"
            : $"Longest: {report.Products.OrderByDescending(product => product.DaysSinceLastSale).First().ItemName}";

        items.Add(new(ReportsAttentionSeverity.Fyi,
            $"{Count(count)} {Plural(count, "product", "products")} in stock unsold for {GetReportsStockWatchQuery.SlowMovingDays}+ days",
            detail, "Slow stock", ReportsHubLinks.Tab(ReportsHubLinks.SlowMoving, period)));
    }
}
