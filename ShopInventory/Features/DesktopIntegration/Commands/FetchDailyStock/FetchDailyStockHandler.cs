using ErrorOr;
using MediatR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Stock;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Hubs;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.DesktopIntegration.Commands.FetchDailyStock;

public sealed class FetchDailyStockHandler(
    ApplicationDbContext context,
    ISAPServiceLayerClient sapClient,
    IHubContext<NotificationHub> hubContext,
    IOptions<DailyStockSettings> settings,
    ITransferEventListenerClient listenerClient,
    IOptions<TransferEventListenerSettings> listenerSettings,
    ILogger<FetchDailyStockHandler> logger
) : IRequestHandler<FetchDailyStockCommand, ErrorOr<FetchDailyStockResult>>
{
    public async Task<ErrorOr<FetchDailyStockResult>> Handle(
        FetchDailyStockCommand command,
        CancellationToken cancellationToken)
    {
        var snapshotDate = command.SnapshotDate?.Date ?? StockLedgerDay.Today(settings.Value.StockFetchTimeCAT);
        var warehouses = command.Warehouses ?? settings.Value.MonitoredWarehouses;
        var results = new List<WarehouseSnapshotResult>();
        var totalItemCount = 0;
        var completedCount = 0;

        // Notify clients that fetch has started
        await hubContext.Clients.Group("all").SendAsync("StockFetchProgress", new
        {
            CompletedCount = 0,
            TotalCount = warehouses.Count,
            CurrentWarehouse = warehouses.FirstOrDefault() ?? "",
            Status = "Started",
            CompletedWarehouses = new List<string>()
        }, cancellationToken);

        foreach (var warehouseCode in warehouses)
        {
            // Clear EF change tracker between warehouses to prevent identity conflicts
            context.ChangeTracker.Clear();

            try
            {
                var result = await FetchWarehouseStockAsync(snapshotDate, warehouseCode, cancellationToken);
                results.Add(result);
                totalItemCount += result.ItemCount;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to fetch stock for warehouse {Warehouse}", warehouseCode);
                results.Add(new WarehouseSnapshotResult(warehouseCode, 0, "Failed"));
            }

            completedCount++;

            // Send per-warehouse progress update
            await hubContext.Clients.Group("all").SendAsync("StockFetchProgress", new
            {
                CompletedCount = completedCount,
                TotalCount = warehouses.Count,
                CurrentWarehouse = warehouseCode,
                Status = completedCount == warehouses.Count ? "Complete" : "InProgress",
                CompletedWarehouses = results.Select(r => r.WarehouseCode).ToList()
            }, cancellationToken);
        }

        var fetchResult = new FetchDailyStockResult(snapshotDate, warehouses.Count, totalItemCount, results);

        // Broadcast real-time event to connected Web clients
        await hubContext.Clients.Group("all").SendAsync("StockSnapshotUpdated", new
        {
            SnapshotDate = snapshotDate,
            WarehouseCount = warehouses.Count,
            TotalItemCount = totalItemCount
        });

        return fetchResult;
    }

    public async Task<WarehouseSnapshotResult> FetchWarehouseStockAsync(
        DateTime snapshotDate, string warehouseCode, CancellationToken cancellationToken)
    {
        // Check if snapshot already exists for this date/warehouse
        var existing = await context.DailyStockSnapshots
            .AsTracking()
            .FirstOrDefaultAsync(s => s.SnapshotDate == snapshotDate && s.WarehouseCode == warehouseCode,
                cancellationToken);

        if (existing is { Status: StockSnapshotStatus.Complete })
        {
            logger.LogInformation("Snapshot already exists for {Warehouse} on {Date}, skipping",
                warehouseCode, snapshotDate);
            return new WarehouseSnapshotResult(warehouseCode, existing.ItemCount, "AlreadyExists");
        }

        // Create or reuse snapshot header
        var snapshot = existing ?? new DailyStockSnapshotEntity
        {
            SnapshotDate = snapshotDate,
            WarehouseCode = warehouseCode,
            Status = StockSnapshotStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };

        if (existing == null)
            context.DailyStockSnapshots.Add(snapshot);
        else
        {
            // Clear previous failed attempt items
            var oldItems = await context.DailyStockSnapshotItems
                .Where(i => i.SnapshotId == existing.Id)
                .ToListAsync(cancellationToken);
            context.DailyStockSnapshotItems.RemoveRange(oldItems);
            snapshot.Status = StockSnapshotStatus.Pending;
            snapshot.LastError = null;
        }

        await context.SaveChangesAsync(cancellationToken);

        try
        {
            logger.LogInformation("Fetching stock from SAP for warehouse {Warehouse}", warehouseCode);

            // Three reads, each answering something the others cannot.
            //
            // Batches carry the expiry dates FEFO needs and are the only place a batch-managed
            // item's position is broken down. The warehouse read is the only place commitments are
            // visible, so it is what turns a gross batch quantity into a sellable one. And
            // FetchUnbatchedStockAsync below owns the items SAP does not batch-manage.
            //
            // That last division matters and is easy to undo by accident. SnapshotComposer can also
            // emit a row for an item with no batches, and this method used to keep those — which
            // quietly made the unbatched read redundant and its TransferEventListener fallback
            // unreachable. The dedicated read wins: it filters on ManBtchNum rather than inferring
            // from the absence of batch rows, and it degrades instead of failing. So the composer's
            // batch rows are kept and its unbatched ones dropped.
            var batches = await sapClient.GetAllBatchNumbersInWarehouseAsync(warehouseCode, cancellationToken);

            // Deliberately not inside the outer try's fatal path. A commitment read that fails is a
            // worse snapshot, not an absent one: the batch half falls back to the gross quantities
            // this snapshot held before commitments were subtracted at all, and — the point — the
            // unbatched read still runs. Letting this throw would kill the snapshot before the
            // fallback that exists for exactly the SAP-slot starvation most likely to cause it.
            List<DTOs.StockQuantityDto>? warehouseStock = null;
            try
            {
                warehouseStock = await sapClient.GetStockQuantitiesInWarehouseAsync(warehouseCode, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Could not read warehouse stock for {Warehouse}; the batch half will be recorded gross, "
                    + "without commitments taken off", warehouseCode);
            }

            List<DailyStockSnapshotItemEntity> snapshotItems;

            if (warehouseStock is null)
            {
                snapshotItems = batches.Select(b => new DailyStockSnapshotItemEntity
                {
                    SnapshotId = snapshot.Id,
                    ItemCode = b.ItemCode ?? string.Empty,
                    ItemDescription = b.ItemName,
                    WarehouseCode = warehouseCode,
                    BatchNumber = b.BatchNum,
                    OriginalQuantity = b.Quantity,
                    AvailableQuantity = b.Quantity,
                    ExpiryDate = DateTime.TryParse(b.ExpiryDate, out var expiry) ? expiry : null
                }).ToList();
            }
            else
            {
                var composed = SnapshotComposer.Compose(batches, warehouseStock);

                foreach (var note in composed.Notes)
                {
                    // Warning rather than information: each of these is an item a till will not be able
                    // to sell today, or a figure that does not add up. Silence is how the missing
                    // non-batch items went unnoticed for as long as they did.
                    logger.LogWarning("Stock snapshot for {Warehouse}: {Note}", warehouseCode, note);
                }

                snapshotItems = composed.Rows
                    .Where(row => row.BatchNumber is not null)
                    .Select(row => new DailyStockSnapshotItemEntity
                    {
                        SnapshotId = snapshot.Id,
                        ItemCode = row.ItemCode,
                        ItemDescription = row.ItemDescription,
                        WarehouseCode = warehouseCode,
                        BatchNumber = row.BatchNumber,
                        OriginalQuantity = row.OriginalQuantity,
                        AvailableQuantity = row.AvailableQuantity,
                        ExpiryDate = row.ExpiryDate
                    }).ToList();
            }

            var (unbatched, unbatchedProblem) =
                await FetchUnbatchedStockAsync(snapshot.Id, warehouseCode, snapshotItems, cancellationToken);
            snapshotItems.AddRange(unbatched);

            context.DailyStockSnapshotItems.AddRange(snapshotItems);

            snapshot.Status = StockSnapshotStatus.Complete;
            snapshot.ItemCount = snapshotItems.Count;
            snapshot.CompletedAt = DateTime.UtcNow;

            // Complete, because the batch half of the warehouse is on the till and a snapshot marked
            // Failed puts nothing there at all. The gap is recorded rather than swallowed: without it
            // a shop short of exactly its unbatched lines is indistinguishable from one that holds
            // none, which is the shape this whole read exists to fix.
            snapshot.LastError = unbatchedProblem;

            await context.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Snapshot complete for {Warehouse}: {Count} rows ({BatchCount} batch, {UnbatchedCount} unbatched)",
                warehouseCode, snapshotItems.Count, batches.Count, unbatched.Count);

            return new WarehouseSnapshotResult(warehouseCode, snapshotItems.Count, "Complete");
        }
        catch (Exception ex)
        {
            snapshot.Status = StockSnapshotStatus.Failed;
            snapshot.LastError = ex.Message;
            await context.SaveChangesAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// The warehouse's stock in items SAP does not batch-manage, as snapshot rows carrying no batch.
    /// </summary>
    /// <remarks>
    /// The snapshot used to be the <c>OBTN ⋈ OBTQ</c> read alone, which is only the batch-managed
    /// half of a warehouse. Everything else — the bought-in lines a shop resells, the Complimentary
    /// Products group among them — has no batch row to join to and so was absent from every till's
    /// catalogue no matter how much of it the shop held. A cashier could see the item on the shelf
    /// and not on the screen.
    ///
    /// A row with a null <see cref="DailyStockSnapshotItemEntity.BatchNumber"/> is not a new shape
    /// for this table: <c>ProcessTransferEventHandler</c> already writes one when stock arrives for
    /// an item the morning read did not cover, and both the sale's stock check and its FEFO deduction
    /// sum over item and warehouse without looking at the batch. Invoicing at the end of the day asks
    /// SAP whether each item is batch-managed and skips allocation for the ones that are not, so an
    /// unbatched line posts on its own terms.
    ///
    /// <paramref name="batchRows"/> is what the batch read already produced, and any code appearing
    /// there is skipped: the SQL excludes batch-managed items, but an item whose management flag
    /// changed between the two reads would otherwise be counted twice and offer a cashier stock the
    /// warehouse does not hold.
    /// </remarks>
    /// <returns>
    /// The rows to add, and — when the read failed — the sentence recorded against the snapshot. The
    /// two are never both meaningful: a failure yields no rows.
    /// </returns>
    private async Task<(List<DailyStockSnapshotItemEntity> Rows, string? Problem)> FetchUnbatchedStockAsync(
        int snapshotId,
        string warehouseCode,
        IReadOnlyCollection<DailyStockSnapshotItemEntity> batchRows,
        CancellationToken cancellationToken)
    {
        List<DTOs.StockQuantityDto> stock;
        string? sourceNote = null;

        try
        {
            stock = await sapClient.GetNonBatchStockQuantitiesInWarehouseAsync(warehouseCode, cancellationToken);
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown. The batch rows already read are worth having on the till,
            // and the caller records the gap on the snapshot so it is visible rather than inferred
            // from an item nobody can find.
            logger.LogError(ex,
                "Could not read unbatched stock for warehouse {Warehouse}; trying TransferEventListener before giving up",
                warehouseCode);

            var (fallbackRows, fallbackNote) =
                await FetchUnbatchedStockFromListenerAsync(warehouseCode, ex, cancellationToken);

            if (fallbackRows is null)
            {
                return ([], fallbackNote);
            }

            stock = fallbackRows;
            sourceNote = fallbackNote;
        }

        var alreadyRead = batchRows
            .Select(row => row.ItemCode)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var rows = stock
            .Where(item => !string.IsNullOrWhiteSpace(item.ItemCode))
            .Where(item => item.InStock > 0)
            .Where(item => !alreadyRead.Contains(item.ItemCode!.Trim()))
            .GroupBy(item => item.ItemCode!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new DailyStockSnapshotItemEntity
            {
                SnapshotId = snapshotId,
                ItemCode = group.Key,
                ItemDescription = group.Select(item => item.ItemName)
                    .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)),
                WarehouseCode = warehouseCode,

                // No batch, and so no expiry to order by. The sale's FEFO pass orders on a null
                // ExpiryDate like any other and there is only ever one row per item here, so nothing
                // downstream has to choose between them.
                BatchNumber = null,
                OriginalQuantity = group.Sum(item => item.InStock),
                AvailableQuantity = group.Sum(item => item.InStock),
                ExpiryDate = null
            })
            .ToList();

        return (rows, sourceNote);
    }

    /// <summary>
    /// Second attempt at the same warehouse's non-batch stock, through TransferEventListener.
    /// </summary>
    /// <remarks>
    /// Worth having because the listener is a separate process holding its own Service Layer session:
    /// it does not queue behind this API's six SAP slots, so it can still answer while every one of
    /// them is held by a read that has hung. That is the failure this path exists for, and the one the
    /// morning fetch is most exposed to — it runs as background work, capped at four of those slots.
    ///
    /// It is only ever reached after the primary read has thrown, so it costs nothing on a normal day.
    /// </remarks>
    /// <returns>
    /// The rows to use and a note for the snapshot, or a null row list and the sentence explaining why
    /// there are none. A note is returned even on success: the primary read failing is worth recording
    /// whether or not the fallback covered for it.
    /// </returns>
    private async Task<(List<DTOs.StockQuantityDto>? Rows, string? Note)> FetchUnbatchedStockFromListenerAsync(
        string warehouseCode,
        Exception primaryFailure,
        CancellationToken cancellationToken)
    {
        var options = listenerSettings.Value;

        if (!options.UseForUnbatchedStockFallback || !listenerClient.IsEnabled)
        {
            return (null, $"Unbatched stock could not be read from SAP: {primaryFailure.Message}");
        }

        DTOs.TransferListenerWarehouseStockDto? reading;
        try
        {
            reading = await listenerClient.GetWarehouseNonBatchStockAsync(warehouseCode, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex,
                "TransferEventListener could not supply unbatched stock for {Warehouse} either; the snapshot will hold only batch-managed items",
                warehouseCode);

            return (null,
                $"Unbatched stock could not be read from SAP: {primaryFailure.Message}. "
                + $"TransferEventListener could not supply it either: {ex.Message}");
        }

        // Null is the listener saying it could not establish anything, which is not an empty warehouse.
        if (reading is null)
        {
            return (null,
                $"Unbatched stock could not be read from SAP: {primaryFailure.Message}. "
                + "TransferEventListener could not reach SAP either.");
        }

        var degraded = string.Equals(reading.Source, "item-scan-fallback", StringComparison.OrdinalIgnoreCase);

        if (degraded && !options.AcceptDegradedStockFallback)
        {
            return (null,
                $"Unbatched stock could not be read from SAP: {primaryFailure.Message}. "
                + "TransferEventListener answered from its degraded item scan, which is configured "
                + "as not good enough to build a till catalogue from.");
        }

        // InStock, never the listener's Available: that one is InStock - Committed + Ordered, so it
        // counts stock still on order. A till needs what is on the shelf.
        var rows = reading.Items
            .Where(item => !string.IsNullOrWhiteSpace(item.ItemCode))
            .Select(item => new DTOs.StockQuantityDto
            {
                ItemCode = item.ItemCode,
                ItemName = item.ItemName,
                WarehouseCode = item.WarehouseCode ?? warehouseCode,
                InStock = item.InStock,
                Committed = item.Committed,
                Ordered = item.Ordered,
                Available = item.Available
            })
            .ToList();

        logger.LogWarning(
            "Unbatched stock for {Warehouse} came from TransferEventListener ({Count} item(s), source {Source}) after this API's own read failed",
            warehouseCode, rows.Count, reading.Source);

        var note =
            $"Unbatched stock could not be read from SAP directly ({primaryFailure.Message}); "
            + $"it was read through TransferEventListener instead ({rows.Count} item(s)).";

        if (degraded)
        {
            // Said plainly because the shortfall is invisible in the data: the scan covers only the
            // listener's configured item groups, and an item in any other group is simply absent.
            note += " That reading came from its degraded item scan, which covers only the item groups "
                + "the listener is configured for, so stock in any other group is missing from it.";
        }

        return (rows, note);
    }
}
