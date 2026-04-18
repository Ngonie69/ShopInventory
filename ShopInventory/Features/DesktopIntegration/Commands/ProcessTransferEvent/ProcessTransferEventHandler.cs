using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.DesktopIntegration.Commands.ProcessTransferEvent;

public sealed class ProcessTransferEventHandler(
    ApplicationDbContext context,
    IOptions<DailyStockSettings> settings,
    ILogger<ProcessTransferEventHandler> logger
) : IRequestHandler<ProcessTransferEventCommand, ErrorOr<ProcessTransferEventResult>>
{
    public async Task<ErrorOr<ProcessTransferEventResult>> Handle(
        ProcessTransferEventCommand command,
        CancellationToken cancellationToken)
    {
        var today = DateTime.UtcNow.Date;
        var monitored = settings.Value.MonitoredWarehouses;
        var adjustments = new List<StockAdjustmentDetail>();

        var sourceIsMonitored = monitored.Contains(command.SourceWarehouse, StringComparer.OrdinalIgnoreCase);
        var destIsMonitored = monitored.Contains(command.DestinationWarehouse, StringComparer.OrdinalIgnoreCase);

        if (!sourceIsMonitored && !destIsMonitored)
        {
            return new ProcessTransferEventResult(false, "Neither warehouse is monitored", adjustments);
        }

        // Process OUT from source warehouse
        if (sourceIsMonitored)
        {
            var adj = await AdjustStockAsync(
                today, command.ItemCode, command.SourceWarehouse,
                -command.Quantity, "OUT",
                command.SourceWarehouse, command.DestinationWarehouse,
                command.SapDocEntry, command.SapDocNum, cancellationToken);

            if (adj != null)
                adjustments.Add(adj);
        }

        // Process IN to destination warehouse
        if (destIsMonitored)
        {
            var adj = await AdjustStockAsync(
                today, command.ItemCode, command.DestinationWarehouse,
                command.Quantity, "IN",
                command.SourceWarehouse, command.DestinationWarehouse,
                command.SapDocEntry, command.SapDocNum, cancellationToken);

            if (adj != null)
                adjustments.Add(adj);
        }

        await context.SaveChangesAsync(cancellationToken);

        return new ProcessTransferEventResult(
            adjustments.Count > 0,
            $"Applied {adjustments.Count} adjustment(s)",
            adjustments);
    }

    private async Task<StockAdjustmentDetail?> AdjustStockAsync(
        DateTime snapshotDate, string itemCode, string warehouseCode,
        decimal adjustmentQty, string direction,
        string sourceWarehouse, string destinationWarehouse,
        int? docEntry, int? docNum,
        CancellationToken cancellationToken)
    {
        // Check for duplicate adjustment
        if (docEntry.HasValue)
        {
            var exists = await context.StockTransferAdjustments
                .AnyAsync(a =>
                    a.SnapshotDate == snapshotDate &&
                    a.ItemCode == itemCode &&
                    a.WarehouseCode == warehouseCode &&
                    a.TransferDocEntry == docEntry &&
                    a.Direction == direction,
                    cancellationToken);

            if (exists)
            {
                logger.LogInformation(
                    "Duplicate transfer adjustment skipped: DocEntry={DocEntry}, Item={ItemCode}, WH={Warehouse}, Dir={Direction}",
                    docEntry, itemCode, warehouseCode, direction);
                return null;
            }
        }

        // Record the adjustment
        context.StockTransferAdjustments.Add(new StockTransferAdjustmentEntity
        {
            SnapshotDate = snapshotDate,
            ItemCode = itemCode,
            WarehouseCode = warehouseCode,
            AdjustmentQuantity = adjustmentQty,
            Direction = direction,
            TransferDocEntry = docEntry,
            TransferDocNum = docNum,
            SourceWarehouse = sourceWarehouse,
            DestinationWarehouse = destinationWarehouse,
            DetectedAt = DateTime.UtcNow
        });

        // Update the snapshot item(s) for this item in this warehouse
        // For IN transfers, we add to the first matching batch row (or create one if none exists)
        // For OUT transfers, we deduct proportionally from available batch rows
        var snapshotItems = await context.DailyStockSnapshotItems
            .Where(i => i.Snapshot.SnapshotDate == snapshotDate &&
                        i.ItemCode == itemCode &&
                        i.WarehouseCode == warehouseCode &&
                        i.AvailableQuantity > 0)
            .OrderBy(i => i.ExpiryDate) // FEFO order
            .ToListAsync(cancellationToken);

        decimal newAvailable;

        if (direction == "IN")
        {
            // For inbound, add to first matching row or create a new row
            if (snapshotItems.Count > 0)
            {
                snapshotItems[0].AvailableQuantity += adjustmentQty;
                newAvailable = snapshotItems[0].AvailableQuantity;
            }
            else
            {
                // Find the snapshot header for this warehouse/date
                var snapshot = await context.DailyStockSnapshots
                    .FirstOrDefaultAsync(s => s.SnapshotDate == snapshotDate && s.WarehouseCode == warehouseCode,
                        cancellationToken);

                if (snapshot == null)
                {
                    logger.LogWarning("No snapshot found for {Warehouse} on {Date}, cannot adjust IN transfer",
                        warehouseCode, snapshotDate);
                    return null;
                }

                context.DailyStockSnapshotItems.Add(new DailyStockSnapshotItemEntity
                {
                    SnapshotId = snapshot.Id,
                    ItemCode = itemCode,
                    WarehouseCode = warehouseCode,
                    OriginalQuantity = 0,
                    AvailableQuantity = adjustmentQty,
                    ExpiryDate = null
                });
                newAvailable = adjustmentQty;
            }
        }
        else
        {
            // For outbound, deduct from available batches (FEFO order)
            var remaining = Math.Abs(adjustmentQty);
            newAvailable = 0;
            foreach (var item in snapshotItems)
            {
                if (remaining <= 0) break;

                var deduct = Math.Min(item.AvailableQuantity, remaining);
                item.AvailableQuantity -= deduct;
                remaining -= deduct;
            }

            newAvailable = snapshotItems.Sum(i => i.AvailableQuantity);
        }

        logger.LogInformation(
            "Stock adjustment: {Direction} {Qty} of {ItemCode} in {Warehouse} (DocEntry={DocEntry})",
            direction, Math.Abs(adjustmentQty), itemCode, warehouseCode, docEntry);

        return new StockAdjustmentDetail(warehouseCode, direction, adjustmentQty, newAvailable);
    }
}
