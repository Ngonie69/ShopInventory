using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Features.Vending;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetCounterMonthOnMonth;

/// <summary>
/// Reads two months of shop and vending sales for the month-on-month report.
/// </summary>
/// <remarks>
/// Counters only (<see cref="SaleBusinesses.AtCounters"/>): the vans have their own reports. The figures
/// are grouped in the database, as the sales analysis does, and credits are taken off through
/// <see cref="CreditedSales"/> so a credited sale counts here as it does there. Item values are line totals,
/// which are before VAT; tender values are sale totals, which include it.
/// </remarks>
public sealed class GetCounterMonthOnMonthHandler(ApplicationDbContext db, IAuditService auditService)
    : IRequestHandler<GetCounterMonthOnMonthQuery, ErrorOr<CounterMonthOnMonthResult>>
{
    public async Task<ErrorOr<CounterMonthOnMonthResult>> Handle(
        GetCounterMonthOnMonthQuery request, CancellationToken cancellationToken)
    {
        var outcome = await ReadAsync(request, cancellationToken);

        await auditService.LogAsync(
            AuditActions.ViewDesktopSales,
            nameof(DesktopSaleEntity),
            "(caller scope)",
            outcome.IsError
                ? "Refused a month-on-month report."
                : $"Month-on-month report {outcome.Value.PreviousMonth:yyyy-MM} vs {outcome.Value.CurrentMonth:yyyy-MM}.",
            !outcome.IsError,
            outcome.IsError ? outcome.FirstError.Description : null);

        return outcome;
    }

    private async Task<ErrorOr<CounterMonthOnMonthResult>> ReadAsync(
        GetCounterMonthOnMonthQuery request, CancellationToken cancellationToken)
    {
        var caller = await db.Users
            .AsNoTracking()
            .Include(user => user.Shop)
            .FirstOrDefaultAsync(user => user.Id == request.CallerUserId, cancellationToken);

        var scope = DesktopSalesReadScopeResolver.Resolve(caller);
        if (scope.IsError)
        {
            return scope.Errors;
        }

        var readScope = scope.Value.Narrow(null);
        if (readScope.IsError)
        {
            return readScope.Errors;
        }

        var warehouse = readScope.Value.WarehouseCode;

        IQueryable<DesktopSaleEntity> Counters()
        {
            var sales = db.DesktopSales.AsNoTracking().AtCounters();
            return warehouse is null ? sales : sales.Where(s => s.WarehouseCode == warehouse);
        }

        var previous = FirstOfMonth(request.PreviousMonth);
        var current = FirstOfMonth(request.CurrentMonth);

        var tenders = new List<CounterTenderRow>();
        var items = new List<CounterItemRow>();
        var days = new Dictionary<(DateTime Month, string Business, string Warehouse), int>();

        foreach (var month in new[] { previous, current })
        {
            var from = month;
            var to = month.AddMonths(1);
            var sales = Counters().Where(s => s.DocDate >= from && s.DocDate < to);

            var credited = await CreditedSales.InAsync(db, sales, cancellationToken);
            var creditedByTender = credited
                .GroupBy(c => (Business: SaleBusinesses.Of(c.SourceSystem), Warehouse: c.WarehouseCode ?? "",
                    Currency: CurrencyKey(c.Currency), Tender: TenderTypes.ReportingName(c.PaymentMethod)))
                .ToDictionary(g => g.Key, g => (Sales: g.Count(), Amount: g.Sum(c => c.CreditedAmount)));
            var creditedByItem = credited
                .SelectMany(c => c.Lines, (c, line) => (Business: SaleBusinesses.Of(c.SourceSystem), Warehouse: c.WarehouseCode ?? "",
                    Currency: CurrencyKey(c.Currency), ItemCode: line.ItemCode.Trim().ToUpperInvariant(), line.Quantity, line.Value))
                .GroupBy(x => (x.Business, x.Warehouse, x.Currency, x.ItemCode))
                .ToDictionary(g => g.Key, g => (Quantity: g.Sum(x => x.Quantity), Value: g.Sum(x => x.Value)));

            var tenderCells = (await sales
                    .GroupBy(s => new { s.SourceSystem, s.WarehouseCode, s.Currency, s.PaymentMethod })
                    .Select(g => new
                    {
                        g.Key.SourceSystem,
                        g.Key.WarehouseCode,
                        g.Key.Currency,
                        g.Key.PaymentMethod,
                        Sales = g.Count(),
                        Gross = g.Sum(s => s.TotalAmount),
                        Vat = g.Sum(s => s.VatAmount)
                    })
                    .ToListAsync(cancellationToken))
                // Legacy spellings and sources fold together only after grouping, as the analysis does.
                .GroupBy(c => (Business: SaleBusinesses.Of(c.SourceSystem), Warehouse: c.WarehouseCode ?? "",
                    Currency: CurrencyKey(c.Currency), Tender: TenderTypes.ReportingName(c.PaymentMethod)))
                .ToList();

            foreach (var cell in tenderCells)
            {
                creditedByTender.TryGetValue(cell.Key, out var back);
                tenders.Add(new CounterTenderRow(
                    month, cell.Key.Business, cell.Key.Warehouse, cell.Key.Currency, cell.Key.Tender,
                    cell.Sum(c => c.Sales), cell.Sum(c => c.Gross), cell.Sum(c => c.Vat), back.Sales, back.Amount));
            }

            var itemCells = (await sales
                    .SelectMany(s => s.Lines, (s, line) => new
                    {
                        s.SourceSystem,
                        s.WarehouseCode,
                        s.Currency,
                        line.ItemCode,
                        line.ItemDescription,
                        line.Quantity,
                        line.LineTotal
                    })
                    .GroupBy(x => new { x.SourceSystem, x.WarehouseCode, x.Currency, x.ItemCode })
                    .Select(g => new
                    {
                        g.Key.SourceSystem,
                        g.Key.WarehouseCode,
                        g.Key.Currency,
                        g.Key.ItemCode,
                        ItemName = g.Max(x => x.ItemDescription),
                        Quantity = g.Sum(x => x.Quantity),
                        NetValue = g.Sum(x => x.LineTotal)
                    })
                    .ToListAsync(cancellationToken))
                .GroupBy(c => (Business: SaleBusinesses.Of(c.SourceSystem), Warehouse: c.WarehouseCode ?? "",
                    Currency: CurrencyKey(c.Currency), ItemCode: c.ItemCode.Trim().ToUpperInvariant()))
                .ToList();

            foreach (var cell in itemCells)
            {
                creditedByItem.TryGetValue(cell.Key, out var back);
                var sold = cell.Sum(c => c.Quantity);
                var value = cell.Sum(c => c.NetValue);
                items.Add(new CounterItemRow(
                    month, cell.Key.Business, cell.Key.Warehouse, cell.Key.Currency, cell.Key.ItemCode,
                    cell.Select(c => c.ItemName).Where(n => !string.IsNullOrWhiteSpace(n)).Max(StringComparer.Ordinal),
                    sold - back.Quantity, value - back.Value, back.Quantity, back.Value));
            }

            var dayCells = await sales
                .GroupBy(s => new { s.SourceSystem, s.WarehouseCode })
                .Select(g => new { g.Key.SourceSystem, g.Key.WarehouseCode, Days = g.Select(s => s.DocDate).Distinct().Count() })
                .ToListAsync(cancellationToken);

            // A place that sells under two sources (a legacy row, say) traded on the busier source's days at
            // least; taking the larger count avoids adding the same day twice.
            foreach (var cell in dayCells)
            {
                var key = (month, SaleBusinesses.Of(cell.SourceSystem), cell.WarehouseCode ?? "");
                days[key] = Math.Max(days.GetValueOrDefault(key), cell.Days);
            }
        }

        var firstSales = (await Counters()
                .GroupBy(s => new { s.SourceSystem, s.WarehouseCode })
                .Select(g => new { g.Key.SourceSystem, g.Key.WarehouseCode, First = g.Min(s => s.DocDate) })
                .ToListAsync(cancellationToken))
            .GroupBy(c => (Business: SaleBusinesses.Of(c.SourceSystem), Warehouse: c.WarehouseCode ?? ""))
            .ToDictionary(g => g.Key, g => g.Min(c => c.First));

        var shopNames = (await db.Shops
                .AsNoTracking()
                .OrderByDescending(s => s.IsActive)
                .ThenBy(s => s.Id)
                .Select(s => new { s.WarehouseCode, s.Name })
                .ToListAsync(cancellationToken))
            .GroupBy(s => s.WarehouseCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);

        // A vending point by its depot's business partner (KEFBYC → COR008 "Cortina Bulawayo Vending"):
        // that is what the business calls it, and the warehouse is only where its stock sits.
        var depots = await VendingDepots.PartnerByWarehouseAsync(db, cancellationToken);
        var depotNames = await VendingDepots.PartnerNamesAsync(db, depots.Values, cancellationToken);
        string? DepotName(string warehouse) =>
            depots.TryGetValue(warehouse, out var depot) ? depotNames.GetValueOrDefault(depot, depot) : null;

        var places = tenders
            .Select(t => (t.Business, t.WarehouseCode))
            .Concat(items.Select(i => (i.Business, i.WarehouseCode)))
            .Distinct()
            .Select(p => new CounterPlaceRow(
                p.Business,
                p.WarehouseCode,
                p.Business == SaleBusinesses.Shops ? shopNames.GetValueOrDefault(p.WarehouseCode) : DepotName(p.WarehouseCode),
                firstSales.TryGetValue((p.Business, p.WarehouseCode), out var first) ? first.Date : null,
                days.GetValueOrDefault((previous, p.Business, p.WarehouseCode)),
                days.GetValueOrDefault((current, p.Business, p.WarehouseCode))))
            .OrderBy(p => p.Business == SaleBusinesses.Shops ? 0 : 1)
            .ThenBy(p => p.WarehouseCode, StringComparer.Ordinal)
            .ToList();

        return new CounterMonthOnMonthResult(previous, current, places, tenders, items);
    }

    private static DateTime FirstOfMonth(DateTime date) => new(date.Year, date.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);

    private static string CurrencyKey(string? currency) =>
        string.IsNullOrWhiteSpace(currency) ? "Not recorded" : currency.Trim().ToUpperInvariant();
}
