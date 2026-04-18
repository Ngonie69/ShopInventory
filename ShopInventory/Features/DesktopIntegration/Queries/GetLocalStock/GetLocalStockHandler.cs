using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetLocalStock;

public sealed class GetLocalStockHandler(
    ApplicationDbContext context,
    ILogger<GetLocalStockHandler> logger
) : IRequestHandler<GetLocalStockQuery, ErrorOr<LocalStockResult>>
{
    public async Task<ErrorOr<LocalStockResult>> Handle(
        GetLocalStockQuery query,
        CancellationToken cancellationToken)
    {
        var snapshotDate = query.SnapshotDate?.Date ?? DateTime.UtcNow.Date;

        var snapshot = await context.DailyStockSnapshots
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.SnapshotDate == snapshotDate && s.WarehouseCode == query.WarehouseCode,
                cancellationToken);

        if (snapshot == null)
            return Errors.DesktopSales.SnapshotNotFound(query.WarehouseCode, snapshotDate);

        if (snapshot.Status == Models.Entities.StockSnapshotStatus.Pending)
            return Errors.DesktopSales.SnapshotNotReady(query.WarehouseCode);

        // Get all batch-level items for this snapshot
        var snapshotItems = await context.DailyStockSnapshotItems
            .AsNoTracking()
            .Where(i => i.SnapshotId == snapshot.Id)
            .OrderBy(i => i.ItemCode)
            .ThenBy(i => i.ExpiryDate)
            .ToListAsync(cancellationToken);

        // Get transfer adjustments for context
        var adjustments = await context.StockTransferAdjustments
            .AsNoTracking()
            .Where(a => a.SnapshotDate == snapshotDate && a.WarehouseCode == query.WarehouseCode)
            .GroupBy(a => a.ItemCode)
            .Select(g => new { ItemCode = g.Key, TotalAdjustment = g.Sum(a => a.AdjustmentQuantity) })
            .ToDictionaryAsync(x => x.ItemCode, x => x.TotalAdjustment, cancellationToken);

        // Group by item code and aggregate
        var items = snapshotItems
            .GroupBy(i => i.ItemCode)
            .Select(g =>
            {
                var first = g.First();
                adjustments.TryGetValue(g.Key, out var transferAdj);

                return new LocalStockItemDto(
                    ItemCode: g.Key,
                    ItemDescription: first.ItemDescription,
                    WarehouseCode: query.WarehouseCode,
                    AvailableQuantity: g.Sum(i => i.AvailableQuantity),
                    OriginalQuantity: g.Sum(i => i.OriginalQuantity),
                    TransferAdjustment: transferAdj,
                    Batches: g.Select(b => new LocalStockBatchDto(
                        BatchNumber: b.BatchNumber,
                        AvailableQuantity: b.AvailableQuantity,
                        OriginalQuantity: b.OriginalQuantity,
                        ExpiryDate: b.ExpiryDate
                    )).ToList()
                );
            })
            .ToList();

        return new LocalStockResult(
            query.WarehouseCode,
            snapshotDate,
            snapshot.Status.ToString(),
            items);
    }
}
