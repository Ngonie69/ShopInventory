using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Features.Stock.Queries.GetStockMovements;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.Stock.Queries.GetStockMovementTotals;

/// <summary>
/// Totals the journal per item. The rows are read narrow and folded here rather than in SQL: a
/// month of a shop is a few tens of thousands of four-column rows, and the fold is what keeps the
/// kind rules in one readable place.
/// </summary>
public sealed class GetStockMovementTotalsHandler(ApplicationDbContext context)
    : IRequestHandler<GetStockMovementTotalsQuery, ErrorOr<StockMovementTotalsResult>>
{
    private static readonly string[] SalesKinds =
        [StockMovementKinds.Commit, StockMovementKinds.Settle, StockMovementKinds.Release];

    public async Task<ErrorOr<StockMovementTotalsResult>> Handle(
        GetStockMovementTotalsQuery query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query.WarehouseCode))
            return Error.Validation("Stock.WarehouseRequired", "Name the warehouse to read.");

        var from = DateTime.SpecifyKind(query.FromUtc, DateTimeKind.Utc);
        var to = DateTime.SpecifyKind(query.ToUtc, DateTimeKind.Utc);
        if (to <= from)
            return Error.Validation("Stock.MovementWindow", "The end of the window must come after its start.");
        if (to - from > GetStockMovementsHandler.MaxWindow)
            return Error.Validation("Stock.MovementWindow",
                $"Ask for at most {GetStockMovementsHandler.MaxWindow.TotalDays:0} days of movements at a time.");

        var warehouse = query.WarehouseCode.Trim();
        var rows = await context.StockMovements
            .AsNoTracking()
            .Where(m => m.WarehouseCode == warehouse && m.OccurredAt > from && m.OccurredAt <= to)
            .Select(m => new { m.ItemCode, m.Kind, m.Quantity })
            .ToListAsync(cancellationToken);

        var items = rows
            .GroupBy(m => m.ItemCode, StringComparer.OrdinalIgnoreCase)
            .Select(g => new StockMovementTotalDto(
                g.Key,
                -g.Where(m => SalesKinds.Contains(m.Kind)).Sum(m => m.Quantity),
                g.Where(m => m.Kind == StockMovementKinds.Transfer && m.Quantity > 0).Sum(m => m.Quantity),
                -g.Where(m => m.Kind == StockMovementKinds.Transfer && m.Quantity < 0).Sum(m => m.Quantity),
                g.Where(m => m.Kind == StockMovementKinds.Reconciliation).Sum(m => m.Quantity),
                g.Count()))
            .OrderBy(item => item.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new StockMovementTotalsResult(warehouse, from, to, items);
    }
}
