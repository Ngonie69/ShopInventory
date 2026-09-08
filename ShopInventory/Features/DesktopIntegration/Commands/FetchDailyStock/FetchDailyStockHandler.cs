using ErrorOr;
using MediatR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
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
    ILogger<FetchDailyStockHandler> logger
) : IRequestHandler<FetchDailyStockCommand, ErrorOr<FetchDailyStockResult>>
{
    public async Task<ErrorOr<FetchDailyStockResult>> Handle(
        FetchDailyStockCommand command,
        CancellationToken cancellationToken)
    {
        var snapshotDate = command.SnapshotDate?.Date ?? DateTime.UtcNow.Date;
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
            logger.LogInformation("Fetching batch stock from SAP for warehouse {Warehouse}", warehouseCode);

            var batches = await sapClient.GetAllBatchNumbersInWarehouseAsync(warehouseCode, cancellationToken);

            var snapshotItems = batches.Select(b => new DailyStockSnapshotItemEntity
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
                "Could not read unbatched stock for warehouse {Warehouse}; the snapshot will hold only batch-managed items",
                warehouseCode);
            return ([], $"Unbatched stock could not be read from SAP: {ex.Message}");
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

        return (rows, null);
    }
}
