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
/// One item, taken apart: where it sold, to whom, through which channel and by whom, day by day against
/// the previous period, at what price, and at what margin.
/// </summary>
/// <remarks>
/// Measured on the item's lines rather than on whole sales, so a shop's figure here is what that shop took
/// for this item, not for every basket it appeared in. Scope, periods, names and margin all come from
/// <see cref="ManagementSalesRollup"/>, so the item's totals here equal its row on the report.
/// </remarks>
public sealed class GetManagementItemAnalysisHandler(
    ApplicationDbContext db,
    IAuditService auditService,
    ISaleInvoiceCostReader costReader,
    ILogger<GetManagementItemAnalysisHandler> logger)
    : IRequestHandler<GetManagementItemAnalysisQuery, ErrorOr<ManagementItemAnalysis>>
{
    public async Task<ErrorOr<ManagementItemAnalysis>> Handle(
        GetManagementItemAnalysisQuery request, CancellationToken cancellationToken)
    {
        var outcome = await BuildAsync(request, cancellationToken);

        await auditService.LogAsync(
            AuditActions.ViewDesktopSales,
            nameof(DesktopSaleEntity),
            request.ItemCode,
            outcome.IsError
                ? $"Refused an item analysis of {request.ItemCode} for warehouse {request.WarehouseCode}."
                : $"Item analysis of {request.ItemCode} from {outcome.Value.FromDate:yyyy-MM-dd} to {outcome.Value.ToDate:yyyy-MM-dd}.",
            !outcome.IsError,
            outcome.IsError ? outcome.FirstError.Description : null);

        return outcome;
    }

    private async Task<ErrorOr<ManagementItemAnalysis>> BuildAsync(
        GetManagementItemAnalysisQuery request, CancellationToken cancellationToken)
    {
        var resolved = await WindowAsync(
            db, request.CallerUserId, request.FromDate, request.ToDate, request.WarehouseCode, request.SourceSystem, request.CardCode, cancellationToken);
        if (resolved.IsError)
        {
            return resolved.Errors;
        }

        var window = resolved.Value;
        var itemCode = request.ItemCode.Trim();

        var current = await CellsAsync(Sales(db, window, window.From, window.To), itemCode, cancellationToken);
        var previous = await CellsAsync(Sales(db, window, window.PreviousFrom, window.PreviousTo), itemCode, cancellationToken);
        var (margin, marginStatus) = await MarginAsync(
            Sales(db, window, window.From, window.To), itemCode, window, costReader, logger, cancellationToken);

        var everyCell = current.Concat(previous).ToList();
        var labels = await LabelsAsync(
            db,
            everyCell.Select(c => c.CreatedBy),
            everyCell.Where(c => c.RouteCustomerId is not null).Select(c => c.RouteCustomerId!.Value),
            cancellationToken);
        var partners = PartnerDirectory(await PartnersAsync(db, window, cancellationToken));

        var description = await Sales(db, window, window.PreviousFrom, window.To)
            .SelectMany(s => s.Lines)
            .Where(line => line.ItemCode == itemCode)
            .Select(line => line.ItemDescription)
            .FirstOrDefaultAsync(cancellationToken);

        var group = await db.Products.AsNoTracking()
            .Where(product => product.ItemCode == itemCode)
            .Select(product => product.ItemsGroupCode)
            .FirstOrDefaultAsync(cancellationToken);

        var currencies = InCurrencyOrder(
                everyCell.Select(c => c.Currency)
                    .Distinct(StringComparer.Ordinal)
                    .Select(currency => Section(
                        currency,
                        window,
                        current.Where(c => c.Currency == currency).ToList(),
                        previous.Where(c => c.Currency == currency).ToList(),
                        margin,
                        labels,
                        partners)),
                section => section.Currency)
            .ToList();

        return new ManagementItemAnalysis(
            itemCode,
            description,
            group,
            window.From,
            window.To,
            window.PreviousFrom,
            window.PreviousTo,
            window.WarehouseCode,
            window.SourceSystem,
            window.CardCode,
            currencies,
            marginStatus);
    }

    private static async Task<List<Cell>> CellsAsync(
        IQueryable<DesktopSaleEntity> sales, string itemCode, CancellationToken cancellationToken) =>
        (await sales
            .SelectMany(s => s.Lines.Where(line => line.ItemCode == itemCode), (s, line) => new
            {
                s.Currency,
                s.DocDate,
                s.WarehouseCode,
                s.CardCode,
                s.SourceSystem,
                s.CreatedBy,
                s.RouteCustomerId,
                line.SaleId,
                line.Quantity,
                line.UnitPrice,
                line.LineTotal,
            })
            .GroupBy(x => new { x.Currency, x.DocDate, x.WarehouseCode, x.CardCode, x.SourceSystem, x.CreatedBy, x.RouteCustomerId })
            .Select(g => new
            {
                g.Key.Currency,
                g.Key.DocDate,
                g.Key.WarehouseCode,
                g.Key.CardCode,
                g.Key.SourceSystem,
                g.Key.CreatedBy,
                g.Key.RouteCustomerId,
                SalesCount = g.Select(x => x.SaleId).Distinct().Count(),
                Quantity = g.Sum(x => x.Quantity),
                NetAmount = g.Sum(x => x.LineTotal),
                ListAmount = g.Sum(x => x.Quantity * x.UnitPrice),
            })
            .ToListAsync(cancellationToken))
        .Select(c => new Cell(
            CurrencyKey(c.Currency),
            c.DocDate.Date,
            c.WarehouseCode ?? string.Empty,
            c.CardCode?.Trim() ?? string.Empty,
            c.SourceSystem ?? string.Empty,
            c.CreatedBy ?? string.Empty,
            c.RouteCustomerId,
            c.SalesCount,
            c.Quantity,
            c.NetAmount,
            Math.Max(0m, c.ListAmount - c.NetAmount)))
        .ToList();

    private static ManagementItemCurrencySection Section(
        string currency,
        Window window,
        List<Cell> current,
        List<Cell> previous,
        MarginBook margin,
        Labels labels,
        Dictionary<string, ManagementPartner> partners)
    {
        var quantity = current.Sum(c => c.Quantity);
        var net = current.Sum(c => c.NetAmount);
        var sales = current.Sum(c => c.SalesCount);
        var previousQuantity = previous.Sum(c => c.Quantity);
        var previousNet = previous.Sum(c => c.NetAmount);
        var price = PerUnit(net, quantity);
        var previousPrice = PerUnit(previousNet, previousQuantity);
        var total = margin.Total(currency);

        var summary = new ManagementItemSummary(
            quantity,
            net,
            sales,
            price,
            sales == 0 ? 0m : Math.Round(quantity / sales, 2, MidpointRounding.AwayFromZero),
            Math.Round(current.Sum(c => c.DiscountAmount), 2, MidpointRounding.AwayFromZero),
            previousQuantity,
            previousNet,
            previous.Sum(c => c.SalesCount),
            previousPrice,
            Change(quantity, previousQuantity),
            Change(net, previousNet),
            price == 0 ? null : Change(price, previousPrice),
            total?.GrossProfit,
            total?.MarginPercent);

        var byDay = Enumerable.Range(0, window.Days)
            .Select(offset =>
            {
                var date = window.From.AddDays(offset);
                var compared = window.PreviousFrom.AddDays(offset);
                var today = current.Where(c => c.DocDate == date).ToList();
                var then = previous.Where(c => c.DocDate == compared).ToList();
                return new ManagementItemDayRow(
                    date, today.Sum(c => c.Quantity), today.Sum(c => c.NetAmount),
                    compared, then.Sum(c => c.Quantity), then.Sum(c => c.NetAmount));
            })
            .ToList();

        var everyCell = current.Concat(previous).ToList();

        return new ManagementItemCurrencySection(
            currency,
            summary,
            byDay,
            Breakdown(current, previous, net, c => c.WarehouseCode,
                key => DepotLabel(key, labels, everyCell
                    .Where(c => string.Equals(c.WarehouseCode, key, StringComparison.OrdinalIgnoreCase))
                    .Select(c => c.CardCode)),
                key => margin.For(currency, Dimension.Depot, key)),
            Breakdown(current, previous, net, c => c.CardCode,
                key => PartnerLabel(key, partners, everyCell
                    .Where(c => string.Equals(c.CardCode, key, StringComparison.OrdinalIgnoreCase))
                    .Select(c => c.WarehouseCode)),
                key => margin.For(currency, Dimension.Partner, key)),
            Breakdown(
                current.Where(c => c.RouteCustomerId is not null).ToList(),
                previous.Where(c => c.RouteCustomerId is not null).ToList(),
                net,
                c => VendorKey(c.RouteCustomerId!.Value),
                key => VendorLabel(key, labels),
                key => margin.For(currency, Dimension.Vendor, key)),
            Breakdown(current, previous, net, c => c.SourceSystem, key => (SourceLabel(key), null),
                key => margin.For(currency, Dimension.Channel, key)),
            Breakdown(current, previous, net, c => c.CreatedBy, key => (OperatorLabel(key, labels), null),
                key => margin.For(currency, Dimension.Operator, key)));
    }

    private static List<ManagementItemBreakdownRow> Breakdown(
        List<Cell> current,
        List<Cell> previous,
        decimal total,
        Func<Cell, string> key,
        Func<string, (string Label, string? Hint)> label,
        Func<string, MarginFigure?> marginFor)
    {
        var before = previous
            .GroupBy(key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (Quantity: g.Sum(c => c.Quantity), Net: g.Sum(c => c.NetAmount)), StringComparer.OrdinalIgnoreCase);
        var now = current
            .GroupBy(key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        return now.Keys
            .Concat(before.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(rowKey =>
            {
                var cells = now.GetValueOrDefault(rowKey) ?? [];
                var quantity = cells.Sum(c => c.Quantity);
                var net = cells.Sum(c => c.NetAmount);
                var then = before.TryGetValue(rowKey, out var found) ? found : (Quantity: 0m, Net: 0m);
                var (rowLabel, hint) = label(rowKey);
                var rowMargin = marginFor(rowKey);

                return new ManagementItemBreakdownRow(
                    rowKey,
                    rowLabel,
                    hint,
                    quantity,
                    net,
                    cells.Sum(c => c.SalesCount),
                    Share(net, total),
                    PerUnit(net, quantity),
                    then.Quantity,
                    then.Net,
                    Change(net, then.Net),
                    rowMargin?.GrossProfit,
                    rowMargin?.MarginPercent);
            })
            .OrderByDescending(row => row.NetAmount)
            .ThenByDescending(row => row.PreviousNetAmount)
            .ThenBy(row => row.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private sealed record Cell(
        string Currency,
        DateTime DocDate,
        string WarehouseCode,
        string CardCode,
        string SourceSystem,
        string CreatedBy,
        int? RouteCustomerId,
        int SalesCount,
        decimal Quantity,
        decimal NetAmount,
        decimal DiscountAmount);
}
