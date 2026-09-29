using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Stock;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetLocalStock;

public sealed class GetLocalStockHandler(
    ApplicationDbContext context,
    IOptions<DailyStockSettings> dailyStock,
    IUnpostedTillClaims tillClaims
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
        //
        // And not always today's: a shop whose snapshot for today is still being fetched is offered
        // yesterday's finished one, the same rows the ledger is selling from meanwhile, rather than
        // being told its catalogue is still loading. See StockSnapshotInForce. A day the caller names
        // is still exactly the day it gets.
        var snapshotDate = query.SnapshotDate?.Date
            ?? (await StockSnapshotInForce.ResolveAsync(
                context, query.WarehouseCode, dailyStock.Value, cancellationToken)).Day;

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

        // What sales took off the rows today, net of what was handed back. Without it the page showed
        // In stock beside Original and Adjustment with nothing to account for the difference, and a van
        // that had sold its whole load read as transfers that never arrived. Commit, Settle and Release
        // are the sales ledger's own kinds; a transfer is already the Adjustment column, and the hourly
        // SAP correction is not a sale.
        var salesKinds = new[] { StockMovementKinds.Commit, StockMovementKinds.Settle, StockMovementKinds.Release };
        var sold = await context.StockMovements
            .AsNoTracking()
            .Where(m => m.LedgerDay == snapshotDate
                        && m.WarehouseCode == query.WarehouseCode
                        && salesKinds.Contains(m.Kind))
            .GroupBy(m => m.ItemCode)
            .Select(g => new { ItemCode = g.Key, Net = g.Sum(m => m.Quantity) })
            .ToDictionaryAsync(x => x.ItemCode, x => -x.Net, cancellationToken);

        // What SAP still shows that the tills have already sold. A depot keying a transfer into the SAP
        // client cannot see it and must leave it behind (docs/operations/stock-out-of-till-warehouses.md);
        // this is the one number that rule needs, per item. It describes now, not the snapshot's day, so
        // a past snapshot gets none rather than today's figure under yesterday's date. The snapshot in
        // force counts as current even when it is yesterday's, standing in while today's is fetched.
        IReadOnlyDictionary<string, decimal>? notInSap =
            query.SnapshotDate is null || snapshotDate >= StockLedgerDay.Today(dailyStock.Value.StockFetchTimeCAT)
                ? await tillClaims.ForWarehouseAsync(query.WarehouseCode, cancellationToken)
                : null;

        // Group by item code and aggregate
        var items = snapshotItems
            .GroupBy(i => i.ItemCode)
            .Select(g =>
            {
                var first = g.First();
                adjustments.TryGetValue(g.Key, out var transferAdj);
                sold.TryGetValue(g.Key, out var soldToday);

                return new LocalStockItemDto(
                    ItemCode: g.Key,
                    ItemDescription: first.ItemDescription,
                    WarehouseCode: query.WarehouseCode,
                    AvailableQuantity: g.Sum(i => i.AvailableQuantity),
                    OriginalQuantity: g.Sum(i => i.OriginalQuantity),
                    TransferAdjustment: transferAdj,
                    SoldToday: soldToday,
                    SoldNotInSap: notInSap is null ? null : notInSap.GetValueOrDefault(g.Key),
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
            items,
            snapshot.LastError);
    }
}
