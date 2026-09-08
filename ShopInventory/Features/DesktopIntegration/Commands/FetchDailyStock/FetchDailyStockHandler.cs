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

            // Two reads, because either one alone leaves a hole. Batches carry the expiry dates FEFO
            // needs and are the only place a batch-managed item's position is broken down; the
            // warehouse read is the only place an item SAP does not manage by batch appears at all,
            // and the only place commitments are visible. See SnapshotComposer.
            var batches = await sapClient.GetAllBatchNumbersInWarehouseAsync(warehouseCode, cancellationToken);
            var warehouseStock = await sapClient.GetStockQuantitiesInWarehouseAsync(warehouseCode, cancellationToken);

            var composed = SnapshotComposer.Compose(batches, warehouseStock);

            foreach (var note in composed.Notes)
            {
                // Warning rather than information: each of these is an item a till will not be able
                // to sell today, or a figure that does not add up. Silence is how the missing
                // non-batch items went unnoticed for as long as they did.
                logger.LogWarning("Stock snapshot for {Warehouse}: {Note}", warehouseCode, note);
            }

            var snapshotItems = composed.Rows.Select(row => new DailyStockSnapshotItemEntity
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

            context.DailyStockSnapshotItems.AddRange(snapshotItems);

            snapshot.Status = StockSnapshotStatus.Complete;
            snapshot.ItemCount = snapshotItems.Count;
            snapshot.CompletedAt = DateTime.UtcNow;

            await context.SaveChangesAsync(cancellationToken);

            logger.LogInformation("Snapshot complete for {Warehouse}: {Count} batch items",
                warehouseCode, snapshotItems.Count);

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
}
