using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Stock;
using ShopInventory.Configuration;
using ShopInventory.Data;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetLocalStock;

public sealed class GetLocalStockHandler(
    ApplicationDbContext context,
    IOptions<DailyStockSettings> dailyStock
) : IRequestHandler<GetLocalStockQuery, ErrorOr<LocalStockResult>>
{
    public async Task<ErrorOr<LocalStockResult>> Handle(
        GetLocalStockQuery query,
        CancellationToken cancellationToken)
    {
        // The day the snapshot in force belongs to, resolved the way the fetch that writes it and the
        // ledger that sells from it both resolve it. This read was the one caller left on
        // DateTime.UtcNow.Date, which rolls at 02:00 CAT — five hours before the 07:00 fetch produces
        // the day it was then asking for. Every till therefore lost its catalogue between 02:00 and
        // 07:00 every morning and was told today's figures were not available yet, while the snapshot
        // it should have been selling from sat in storage under yesterday's date.
        var snapshotDate = query.SnapshotDate?.Date
            ?? StockLedgerDay.Today(dailyStock.Value.StockFetchTimeCAT);

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
