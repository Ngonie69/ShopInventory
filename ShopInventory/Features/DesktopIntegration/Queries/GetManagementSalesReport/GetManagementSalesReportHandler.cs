using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using static ShopInventory.Features.DesktopIntegration.Queries.GetManagementSalesReport.ManagementSalesRollup;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetManagementSalesReport;

/// <summary>
/// Builds the management sales report: this period against the last, by every dimension a decision is
/// made on, with SAP's booked margin and the state of posting and fiscalisation.
/// </summary>
/// <remarks>
/// <para>
/// Read the way the till analysis is read — grouped queries, rolled up in memory — and under the same
/// scope and source rules, so the two reports can never disagree about which sales exist.
/// </para>
/// <para>
/// Margin comes from SAP, not from a price list: the gross profit SAP booked on the invoice each sale was
/// posted as. That is the only cost in the system that is the cost at the time of sale, and it means a
/// sale SAP does not have yet has no margin — so every margin figure is measured on the sales SAP costed,
/// and the report says how much that is. A SAP read that fails leaves the margin unavailable and the rest
/// of the report intact. See <see cref="ManagementSalesRollup"/>, which the item drill-down shares.
/// </para>
/// </remarks>
public sealed class GetManagementSalesReportHandler(
    ApplicationDbContext db,
    IAuditService auditService,
    ISaleInvoiceCostReader costReader,
    ILogger<GetManagementSalesReportHandler> logger)
    : IRequestHandler<GetManagementSalesReportQuery, ErrorOr<ManagementSalesReport>>
{
    /// <inheritdoc cref="ManagementSalesRollup.PostingGraceDays"/>
    public const int PostingGraceDays = ManagementSalesRollup.PostingGraceDays;

    public async Task<ErrorOr<ManagementSalesReport>> Handle(
        GetManagementSalesReportQuery request, CancellationToken cancellationToken)
    {
        var outcome = await BuildAsync(request, cancellationToken);

        await auditService.LogAsync(
            AuditActions.ViewDesktopSales,
            nameof(DesktopSaleEntity),
            string.IsNullOrWhiteSpace(request.WarehouseCode) ? "(caller scope)" : request.WarehouseCode.Trim(),
            outcome.IsError
                ? $"Refused a management sales report for warehouse {request.WarehouseCode}, partner {request.CardCode}."
                : $"Management sales report on {outcome.Value.Health.SalesCount} sale(s) from "
                    + $"{outcome.Value.FromDate:yyyy-MM-dd} to {outcome.Value.ToDate:yyyy-MM-dd}.",
            !outcome.IsError,
            outcome.IsError ? outcome.FirstError.Description : null);

        return outcome;
    }

    private async Task<ErrorOr<ManagementSalesReport>> BuildAsync(
        GetManagementSalesReportQuery request, CancellationToken cancellationToken)
    {
        var resolved = await WindowAsync(
            db, request.CallerUserId, request.FromDate, request.ToDate, request.WarehouseCode, request.SourceSystem, request.CardCode, cancellationToken);
        if (resolved.IsError)
        {
            return resolved.Errors;
        }

        var window = resolved.Value;
        var current = await CellsAsync(Sales(db, window, window.From, window.To), cancellationToken);
        var previous = await CellsAsync(Sales(db, window, window.PreviousFrom, window.PreviousTo), cancellationToken);
        var currentItems = await ItemsAsync(Sales(db, window, window.From, window.To), cancellationToken);
        var previousItems = await ItemsAsync(Sales(db, window, window.PreviousFrom, window.PreviousTo), cancellationToken);
        var matrix = await MatrixAsync(Sales(db, window, window.From, window.To), cancellationToken);
        var partnerMatrix = await PartnerMatrixAsync(Sales(db, window, window.From, window.To), cancellationToken);
        var partners = await PartnersAsync(db, window, cancellationToken);
        var directory = PartnerDirectory(partners);
        var health = await HealthAsync(Sales(db, window, window.From, window.To), cancellationToken);
        var (margin, marginStatus) = await MarginAsync(
            Sales(db, window, window.From, window.To), null, window, costReader, logger, cancellationToken);

        var everyCell = current.Concat(previous).ToList();
        var labels = await LabelsAsync(
            db,
            everyCell.Select(c => c.CreatedBy),
            everyCell.Where(c => c.RouteCustomerId is not null).Select(c => c.RouteCustomerId!.Value),
            cancellationToken);
        var groups = await ItemGroupsAsync(currentItems.Concat(previousItems).Select(i => i.ItemCode), cancellationToken);

        var currencies = InCurrencyOrder(
                current.Select(c => c.Currency)
                    .Concat(previous.Select(c => c.Currency))
                    .Distinct(StringComparer.Ordinal)
                    .Select(currency => Section(
                        currency,
                        window,
                        current.Where(c => c.Currency == currency).ToList(),
                        previous.Where(c => c.Currency == currency).ToList(),
                        currentItems.Where(i => i.Currency == currency).ToList(),
                        previousItems.Where(i => i.Currency == currency).ToList(),
                        matrix.Where(m => m.Currency == currency).ToList(),
                        partnerMatrix.Where(m => m.Currency == currency).ToList(),
                        margin,
                        labels,
                        directory,
                        groups)),
                section => section.Currency)
            .ToList();

        return new ManagementSalesReport(
            window.From,
            window.To,
            window.PreviousFrom,
            window.PreviousTo,
            window.WarehouseCode,
            window.SourceSystem,
            window.CardCode,
            DateTime.UtcNow,
            currencies,
            health,
            marginStatus,
            partners);
    }

    // ── Reading ────────────────────────────────────────────────────────────────────────────────────

    private static async Task<List<Cell>> CellsAsync(IQueryable<DesktopSaleEntity> sales, CancellationToken cancellationToken) =>
        (await sales
            .GroupBy(s => new
            {
                s.Currency,
                s.DocDate,
                s.WarehouseCode,
                s.CardCode,
                s.SourceSystem,
                s.CreatedBy,
                s.PaymentMethod,
                s.CostCentreCode,
                s.RouteCustomerId,
            })
            .Select(g => new
            {
                g.Key.Currency,
                g.Key.DocDate,
                g.Key.WarehouseCode,
                g.Key.CardCode,
                g.Key.SourceSystem,
                g.Key.CreatedBy,
                g.Key.PaymentMethod,
                g.Key.CostCentreCode,
                g.Key.RouteCustomerId,
                SalesCount = g.Count(),
                TotalAmount = g.Sum(s => s.TotalAmount),
                VatAmount = g.Sum(s => s.VatAmount),
            })
            .ToListAsync(cancellationToken))
        .Select(c => new Cell(
            CurrencyKey(c.Currency),
            c.DocDate.Date,
            c.WarehouseCode ?? string.Empty,
            c.CardCode?.Trim() ?? string.Empty,
            c.SourceSystem ?? string.Empty,
            c.CreatedBy ?? string.Empty,
            TenderTypes.ReportingName(c.PaymentMethod),
            c.CostCentreCode?.Trim() ?? string.Empty,
            c.RouteCustomerId,
            c.SalesCount,
            c.TotalAmount,
            c.VatAmount))
        .ToList();

    private static async Task<List<ItemCell>> ItemsAsync(IQueryable<DesktopSaleEntity> sales, CancellationToken cancellationToken) =>
        (await sales
            .SelectMany(s => s.Lines, (s, line) => new
            {
                s.Currency,
                line.SaleId,
                line.ItemCode,
                line.ItemDescription,
                line.Quantity,
                line.UnitPrice,
                line.LineTotal,
            })
            .GroupBy(x => new { x.Currency, x.ItemCode })
            .Select(g => new
            {
                g.Key.Currency,
                g.Key.ItemCode,
                ItemDescription = g.Max(x => x.ItemDescription),
                Quantity = g.Sum(x => x.Quantity),
                NetAmount = g.Sum(x => x.LineTotal),
                ListAmount = g.Sum(x => x.Quantity * x.UnitPrice),
                SalesCount = g.Select(x => x.SaleId).Distinct().Count(),
            })
            .ToListAsync(cancellationToken))
        .Select(i => new ItemCell(
            CurrencyKey(i.Currency),
            i.ItemCode,
            i.ItemDescription,
            i.Quantity,
            i.NetAmount,
            // Rounding on a line can leave a cent the other way; a discount is never negative.
            Math.Max(0m, Math.Round(i.ListAmount - i.NetAmount, 2, MidpointRounding.AwayFromZero)),
            i.SalesCount))
        .ToList();

    private static async Task<List<MatrixCell>> MatrixAsync(IQueryable<DesktopSaleEntity> sales, CancellationToken cancellationToken) =>
        (await sales
            .SelectMany(s => s.Lines, (s, line) => new { s.Currency, s.WarehouseCode, line.ItemCode, line.Quantity, line.LineTotal })
            .GroupBy(x => new { x.Currency, x.ItemCode, x.WarehouseCode })
            .Select(g => new
            {
                g.Key.Currency,
                g.Key.ItemCode,
                g.Key.WarehouseCode,
                Quantity = g.Sum(x => x.Quantity),
                NetAmount = g.Sum(x => x.LineTotal),
            })
            .ToListAsync(cancellationToken))
        .Select(m => new MatrixCell(CurrencyKey(m.Currency), m.ItemCode, m.WarehouseCode ?? string.Empty, m.Quantity, m.NetAmount))
        .ToList();

    private static async Task<List<MatrixCell>> PartnerMatrixAsync(IQueryable<DesktopSaleEntity> sales, CancellationToken cancellationToken) =>
        (await sales
            .SelectMany(s => s.Lines, (s, line) => new { s.Currency, s.CardCode, line.ItemCode, line.Quantity, line.LineTotal })
            .GroupBy(x => new { x.Currency, x.ItemCode, x.CardCode })
            .Select(g => new
            {
                g.Key.Currency,
                g.Key.ItemCode,
                g.Key.CardCode,
                Quantity = g.Sum(x => x.Quantity),
                NetAmount = g.Sum(x => x.LineTotal),
            })
            .ToListAsync(cancellationToken))
        .Select(m => new MatrixCell(CurrencyKey(m.Currency), m.ItemCode, m.CardCode?.Trim() ?? string.Empty, m.Quantity, m.NetAmount))
        .ToList();

    /// <summary>
    /// Each item's SAP item group, from the product master. An item the master does not hold — a
    /// discontinued code still on old sales — is left ungrouped rather than guessed from its name.
    /// </summary>
    private async Task<Dictionary<string, int?>> ItemGroupsAsync(IEnumerable<string> itemCodes, CancellationToken cancellationToken)
    {
        var codes = itemCodes.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (codes.Count == 0)
        {
            return new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        }

        return (await db.Products.AsNoTracking()
                .Where(product => codes.Contains(product.ItemCode))
                .Select(product => new { product.ItemCode, product.ItemsGroupCode })
                .ToListAsync(cancellationToken))
            .GroupBy(product => product.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().ItemsGroupCode, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<ManagementSalesHealth> HealthAsync(
        IQueryable<DesktopSaleEntity> sales, CancellationToken cancellationToken)
    {
        var groups = (await sales
                .GroupBy(s => new
                {
                    s.Currency,
                    s.SourceSystem,
                    s.FiscalizationStatus,
                    s.FiscalizationRequiresReconciliation,
                    Posted = s.SapDocEntry != null || s.ConsolidationStatus == DesktopSaleConsolidationStatus.Consolidated,
                    Attempted = s.PostingAttempts > 0,
                    PaymentFailed = s.PaymentStatus == DesktopSalePaymentStatuses.Failed,
                })
                .Select(g => new
                {
                    g.Key.Currency,
                    g.Key.SourceSystem,
                    g.Key.FiscalizationStatus,
                    g.Key.FiscalizationRequiresReconciliation,
                    g.Key.Posted,
                    g.Key.Attempted,
                    g.Key.PaymentFailed,
                    SalesCount = g.Count(),
                    TotalAmount = g.Sum(s => s.TotalAmount),
                    OldestDate = g.Min(s => s.DocDate),
                })
                .ToListAsync(cancellationToken))
            .Select(g => new HealthCell(
                CurrencyKey(g.Currency),
                SaleSourceSystems.PostedByDesktopSaleJob.Contains(g.SourceSystem ?? string.Empty),
                g.FiscalizationStatus,
                g.FiscalizationRequiresReconciliation,
                g.Posted,
                g.Attempted,
                g.PaymentFailed,
                g.SalesCount,
                g.TotalAmount,
                g.OldestDate.Date))
            .ToList();

        var posting = groups.Where(g => g.PostedByJob).ToList();
        var unposted = posting.Where(g => !g.Posted).ToList();

        var latestError = await sales
            .Where(s => SaleSourceSystems.PostedByDesktopSaleJob.Contains(s.SourceSystem!)
                && s.SapDocEntry == null
                && s.ConsolidationStatus != DesktopSaleConsolidationStatus.Consolidated
                && s.LastPostingError != null)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => s.LastPostingError)
            .FirstOrDefaultAsync(cancellationToken);

        return new ManagementSalesHealth(
            groups.Sum(g => g.SalesCount),
            Bucket(groups.Where(g => g.FiscalStatus == DesktopSaleFiscalizationStatus.Success)),
            Bucket(groups.Where(g => g.FiscalStatus == DesktopSaleFiscalizationStatus.Pending)),
            Bucket(groups.Where(g => g.FiscalStatus == DesktopSaleFiscalizationStatus.Failed)),
            Bucket(groups.Where(g => g.FiscalStatus == DesktopSaleFiscalizationStatus.Skipped)),
            Bucket(groups.Where(g => g.NeedsReconciliation)),
            posting.Sum(g => g.SalesCount),
            Bucket(posting.Where(g => g.Posted)),
            Bucket(unposted.Where(g => !g.Attempted)),
            Bucket(unposted.Where(g => g.Attempted)),
            Bucket(posting.Where(g => g.PaymentFailed)),
            unposted.Count == 0 ? null : unposted.Min(g => g.OldestDate),
            latestError);
    }

    private static ManagementHealthBucket Bucket(IEnumerable<HealthCell> cells)
    {
        var rows = cells.ToList();
        return new ManagementHealthBucket(
            rows.Sum(r => r.SalesCount),
            InCurrencyOrder(rows.GroupBy(r => r.Currency), g => g.Key)
                .Select(g => new ManagementCurrencyAmount(g.Key, g.Sum(r => r.TotalAmount)))
                .ToList());
    }

    // ── Rolling up ─────────────────────────────────────────────────────────────────────────────────

    private static ManagementCurrencySection Section(
        string currency,
        Window window,
        List<Cell> current,
        List<Cell> previous,
        List<ItemCell> currentItems,
        List<ItemCell> previousItems,
        List<MatrixCell> matrix,
        List<MatrixCell> partnerMatrix,
        MarginBook margin,
        Labels labels,
        Dictionary<string, ManagementPartner> partners,
        Dictionary<string, int?> groups)
    {
        var salesCount = current.Sum(c => c.SalesCount);
        var total = current.Sum(c => c.TotalAmount);
        var vat = current.Sum(c => c.VatAmount);
        var previousCount = previous.Sum(c => c.SalesCount);
        var previousTotal = previous.Sum(c => c.TotalAmount);
        var average = Average(total, salesCount);
        var previousAverage = Average(previousTotal, previousCount);
        var summaryMargin = margin.Total(currency);

        var summary = new ManagementSummary(
            salesCount,
            total,
            vat,
            total - vat,
            average,
            currentItems.Sum(i => i.Quantity),
            current.Select(c => c.DocDate).Distinct().Count(),
            current.Where(c => c.RouteCustomerId is not null).Select(c => c.RouteCustomerId).Distinct().Count(),
            previousCount,
            previousTotal,
            previousTotal - previous.Sum(c => c.VatAmount),
            previousAverage,
            previous.Where(c => c.RouteCustomerId is not null).Select(c => c.RouteCustomerId).Distinct().Count(),
            Change(total, previousTotal),
            Change(salesCount, previousCount),
            Change(average, previousAverage),
            summaryMargin?.GrossProfit,
            summaryMargin?.MarginPercent,
            summaryMargin?.Revenue ?? 0m,
            margin.CostedSales(currency));

        var byDay = Enumerable.Range(0, window.Days)
            .Select(offset =>
            {
                var date = window.From.AddDays(offset);
                var compared = window.PreviousFrom.AddDays(offset);
                var today = current.Where(c => c.DocDate == date).ToList();
                var then = previous.Where(c => c.DocDate == compared).ToList();
                return new ManagementDayRow(
                    date, today.Sum(c => c.SalesCount), today.Sum(c => c.TotalAmount),
                    compared, then.Sum(c => c.SalesCount), then.Sum(c => c.TotalAmount));
            })
            .ToList();

        var everyCell = current.Concat(previous).ToList();
        var byDepot = Breakdown(current, previous, total, c => c.WarehouseCode,
            key => DepotLabel(key, labels, everyCell
                .Where(c => string.Equals(c.WarehouseCode, key, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.CardCode)),
            key => margin.For(currency, Dimension.Depot, key));

        var byPartner = Breakdown(current, previous, total, c => c.CardCode,
            key => PartnerLabel(key, partners, everyCell
                .Where(c => string.Equals(c.CardCode, key, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.WarehouseCode)),
            key => margin.For(currency, Dimension.Partner, key));

        var byVendor = Breakdown(
            current.Where(c => c.RouteCustomerId is not null).ToList(),
            previous.Where(c => c.RouteCustomerId is not null).ToList(),
            total,
            c => VendorKey(c.RouteCustomerId!.Value),
            key => VendorLabel(key, labels),
            key => margin.For(currency, Dimension.Vendor, key));

        var lapsed = previous
            .Where(c => c.RouteCustomerId is not null)
            .GroupBy(c => c.RouteCustomerId!.Value)
            .Where(group => !current.Any(c => c.RouteCustomerId == group.Key))
            .Select(group =>
            {
                var key = VendorKey(group.Key);
                var (label, hint) = VendorLabel(key, labels);
                return new ManagementLapsedVendorRow(
                    key, label, hint, group.Sum(c => c.SalesCount), group.Sum(c => c.TotalAmount), group.Max(c => c.DocDate));
            })
            .OrderByDescending(row => row.PreviousTotalAmount)
            .ThenBy(row => row.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var byItem = Items(currency, currentItems, previousItems, margin, groups);

        return new ManagementCurrencySection(
            currency,
            summary,
            byDay,
            Breakdown(current, previous, total, c => c.SourceSystem, key => (SourceLabel(key), null),
                key => margin.For(currency, Dimension.Channel, key)),
            byDepot,
            byPartner,
            Breakdown(current, previous, total, c => c.CostCentreCode, key => (key.Length == 0 ? NotRecorded : key, null),
                key => margin.For(currency, Dimension.CostCentre, key)),
            Breakdown(current, previous, total, c => c.CreatedBy, key => (OperatorLabel(key, labels), null),
                key => margin.For(currency, Dimension.Operator, key)),
            Breakdown(current, previous, total, c => c.PaymentMethod, key => (key, null), _ => null),
            byVendor,
            lapsed,
            byItem,
            ItemGroups(currency, byItem, margin),
            matrix
                .OrderBy(m => m.ItemCode, StringComparer.OrdinalIgnoreCase)
                .ThenBy(m => m.WarehouseCode, StringComparer.OrdinalIgnoreCase)
                .Select(m => new ManagementItemDepotCell(m.ItemCode, m.WarehouseCode, m.Quantity, m.NetAmount))
                .ToList(),
            partnerMatrix
                .OrderBy(m => m.ItemCode, StringComparer.OrdinalIgnoreCase)
                .ThenBy(m => m.WarehouseCode, StringComparer.OrdinalIgnoreCase)
                .Select(m => new ManagementItemPartnerCell(m.ItemCode, m.WarehouseCode, m.Quantity, m.NetAmount))
                .ToList());
    }

    private static List<ManagementItemRow> Items(
        string currency,
        List<ItemCell> currentItems,
        List<ItemCell> previousItems,
        MarginBook margin,
        Dictionary<string, int?> groups)
    {
        var lineValue = currentItems.Sum(i => i.NetAmount);
        var previousByItem = previousItems.ToDictionary(i => i.ItemCode, StringComparer.OrdinalIgnoreCase);
        var currentCodes = currentItems.Select(i => i.ItemCode).ToHashSet(StringComparer.OrdinalIgnoreCase);

        ManagementItemRow Row(ItemCell? now, ItemCell? before)
        {
            var code = now?.ItemCode ?? before!.ItemCode;
            var quantity = now?.Quantity ?? 0m;
            var net = now?.NetAmount ?? 0m;
            var price = PerUnit(net, quantity);
            var previousPrice = PerUnit(before?.NetAmount ?? 0m, before?.Quantity ?? 0m);
            var itemMargin = now is null ? null : margin.For(currency, Dimension.Item, code);

            return new ManagementItemRow(
                code,
                now?.ItemDescription ?? before?.ItemDescription,
                groups.GetValueOrDefault(code),
                quantity,
                net,
                now?.SalesCount ?? 0,
                Share(net, lineValue),
                before?.Quantity ?? 0m,
                before?.NetAmount ?? 0m,
                Change(net, before?.NetAmount ?? 0m),
                Change(quantity, before?.Quantity ?? 0m),
                price,
                previousPrice,
                price == 0 ? null : Change(price, previousPrice),
                now is null || now.SalesCount == 0 ? 0m : Math.Round(quantity / now.SalesCount, 2, MidpointRounding.AwayFromZero),
                now?.DiscountAmount ?? 0m,
                itemMargin?.GrossProfit,
                itemMargin?.MarginPercent);
        }

        return currentItems
            .Select(item => Row(item, previousByItem.GetValueOrDefault(item.ItemCode)))
            // Items that sold last period and not at all this one: a line that stopped selling is a finding.
            .Concat(previousItems.Where(before => !currentCodes.Contains(before.ItemCode)).Select(before => Row(null, before)))
            .OrderByDescending(row => row.NetAmount)
            .ThenByDescending(row => row.PreviousNetAmount)
            .ThenBy(row => row.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The items rolled up by group. Margin is the sum of the items' SAP figures, which add.</summary>
    private static List<ManagementItemGroupRow> ItemGroups(string currency, List<ManagementItemRow> items, MarginBook margin)
    {
        var lineValue = items.Sum(i => i.NetAmount);

        return items
            .GroupBy(item => item.ItemsGroupCode)
            .Select(group =>
            {
                var net = group.Sum(i => i.NetAmount);
                var before = group.Sum(i => i.PreviousNetAmount);
                var groupMargin = MarginFigure.Sum(group.Select(i => margin.For(currency, Dimension.Item, i.ItemCode)));
                return new ManagementItemGroupRow(
                    group.Key,
                    group.Count(),
                    group.Sum(i => i.Quantity),
                    net,
                    Share(net, lineValue),
                    group.Sum(i => i.PreviousQuantity),
                    before,
                    Change(net, before),
                    groupMargin?.GrossProfit,
                    groupMargin?.MarginPercent);
            })
            .OrderByDescending(row => row.NetAmount)
            .ThenByDescending(row => row.PreviousNetAmount)
            .ToList();
    }

    private static List<ManagementBreakdownRow> Breakdown(
        List<Cell> current,
        List<Cell> previous,
        decimal total,
        Func<Cell, string> key,
        Func<string, (string Label, string? Hint)> label,
        Func<string, MarginFigure?> marginFor)
    {
        var before = previous
            .GroupBy(key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (Count: g.Sum(c => c.SalesCount), Total: g.Sum(c => c.TotalAmount)), StringComparer.OrdinalIgnoreCase);

        var now = current
            .GroupBy(key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        return now.Keys
            .Concat(before.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(rowKey =>
            {
                var cells = now.TryGetValue(rowKey, out var found) ? found : [];
                var rowTotal = cells.Sum(c => c.TotalAmount);
                var then = before.TryGetValue(rowKey, out var previousRow) ? previousRow : (Count: 0, Total: 0m);
                var (rowLabel, hint) = label(rowKey);
                var rowMargin = marginFor(rowKey);

                return new ManagementBreakdownRow(
                    rowKey,
                    rowLabel,
                    hint,
                    cells.Sum(c => c.SalesCount),
                    rowTotal,
                    rowTotal - cells.Sum(c => c.VatAmount),
                    Share(rowTotal, total),
                    then.Count,
                    then.Total,
                    Change(rowTotal, then.Total),
                    rowMargin?.GrossProfit,
                    rowMargin?.MarginPercent);
            })
            .OrderByDescending(row => row.TotalAmount)
            .ThenByDescending(row => row.PreviousTotalAmount)
            .ThenBy(row => row.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ── Shapes ─────────────────────────────────────────────────────────────────────────────────────

    private sealed record Cell(
        string Currency,
        DateTime DocDate,
        string WarehouseCode,
        string CardCode,
        string SourceSystem,
        string CreatedBy,
        string PaymentMethod,
        string CostCentreCode,
        int? RouteCustomerId,
        int SalesCount,
        decimal TotalAmount,
        decimal VatAmount);

    private sealed record ItemCell(
        string Currency, string ItemCode, string? ItemDescription, decimal Quantity, decimal NetAmount, decimal DiscountAmount, int SalesCount);

    /// <summary>An item at one place — a warehouse in the depot grid, a CardCode in the partner grid.</summary>
    private sealed record MatrixCell(string Currency, string ItemCode, string WarehouseCode, decimal Quantity, decimal NetAmount);

    private sealed record HealthCell(
        string Currency,
        bool PostedByJob,
        DesktopSaleFiscalizationStatus FiscalStatus,
        bool NeedsReconciliation,
        bool Posted,
        bool Attempted,
        bool PaymentFailed,
        int SalesCount,
        decimal TotalAmount,
        DateTime OldestDate);
}
