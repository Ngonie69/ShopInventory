using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.DesktopIntegration.Commands.FetchDailyStock;

public sealed class FetchDailyStockHandler(
    ApplicationDbContext context,
    ISAPServiceLayerClient sapClient,
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

        foreach (var warehouseCode in warehouses)
        {
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
        }

        return new FetchDailyStockResult(snapshotDate, warehouses.Count, totalItemCount, results);
    }

    public async Task<WarehouseSnapshotResult> FetchWarehouseStockAsync(
        DateTime snapshotDate, string warehouseCode, CancellationToken cancellationToken)
    {
        // Check if snapshot already exists for this date/warehouse
        var existing = await context.DailyStockSnapshots
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
                ItemDescription = null,
                WarehouseCode = warehouseCode,
                BatchNumber = b.BatchNum,
                OriginalQuantity = b.Quantity,
                AvailableQuantity = b.Quantity,
                ExpiryDate = DateTime.TryParse(b.ExpiryDate, out var expiry) ? expiry : null
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
