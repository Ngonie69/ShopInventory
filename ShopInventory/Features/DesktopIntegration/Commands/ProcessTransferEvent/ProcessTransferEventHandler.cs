using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Stock;
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
        // The snapshot day in force, resolved the way the fetch that writes the snapshot, the till
        // that reads it and the ledger that sells from it all resolve it. This was the last caller
        // left on DateTime.UtcNow.Date, which rolls at 02:00 CAT — five hours before the 07:00 fetch
        // produces the day it would then be writing to. A transfer landing in that window wrote its
        // adjustment against a snapshot day that does not exist yet, so the row the till is actually
        // selling from never moved and the stock arrived invisibly.
        var today = StockLedgerDay.Today(settings.Value.StockFetchTimeCAT);
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
        // Every row, not only the ones with something on them. The FEFO walk below still considers
        // only rows that can give, exactly as before — but the movement this writes has to report
        // the position across the whole item, and a row resting at zero is part of that total. A
        // balance that silently omits empty rows is what makes a later comparison against SAP look
        // like a divergence when nothing has diverged.
        var allRows = await context.DailyStockSnapshotItems
            .Where(i => i.Snapshot.SnapshotDate == snapshotDate &&
                        i.ItemCode == itemCode &&
                        i.WarehouseCode == warehouseCode)
            .OrderBy(i => i.ExpiryDate) // FEFO order
            .ToListAsync(cancellationToken);

        var snapshotItems = allRows.Where(i => i.AvailableQuantity > 0).ToList();

        decimal newAvailable;

        // What the ledger actually moved, which is not always what the transfer said: an outbound
        // can run the rows out. Journalling the ask rather than the movement would break the
        // invariant for exactly the items that are already in trouble.
        decimal moved;

        if (direction == "IN")
        {
            // For inbound, add to first matching row or create a new row
            if (snapshotItems.Count > 0)
            {
                snapshotItems[0].AvailableQuantity += adjustmentQty;
                newAvailable = snapshotItems[0].AvailableQuantity;
                moved = adjustmentQty;
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

                var created = new DailyStockSnapshotItemEntity
                {
                    SnapshotId = snapshot.Id,
                    ItemCode = itemCode,
                    WarehouseCode = warehouseCode,
                    OriginalQuantity = 0,
                    AvailableQuantity = adjustmentQty,
                    ExpiryDate = null
                };

                context.DailyStockSnapshotItems.Add(created);

                // Counts towards the total the movement reports, even though it did not exist when
                // the rows were read. Opening quantity zero plus this movement is its balance, which
                // is the invariant holding for a row born mid-day.
                allRows.Add(created);
                newAvailable = adjustmentQty;
                moved = adjustmentQty;
            }
        }
        else
        {
            // For outbound, deduct from available batches (FEFO order)
            var wanted = Math.Abs(adjustmentQty);
            var remaining = wanted;
            newAvailable = 0;
            foreach (var item in snapshotItems)
            {
                if (remaining <= 0) break;

                var deduct = Math.Min(item.AvailableQuantity, remaining);
                item.AvailableQuantity -= deduct;
                remaining -= deduct;
            }

            newAvailable = snapshotItems.Sum(i => i.AvailableQuantity);

            // Short when the rows ran out before the transfer did. The stock left the warehouse in
            // SAP either way, so this is not refusable — it is the transfer equivalent of a settled
            // document taking more than the ledger held, and worth saying out loud.
            moved = -(wanted - remaining);

            if (remaining > 0)
            {
                logger.LogWarning(
                    "Transfer OUT of {Quantity} of {ItemCode} from {Warehouse} found only {Moved} on the "
                    + "snapshot (DocEntry={DocEntry}). The ledger and the shelf had already drifted apart.",
                    wanted, itemCode, warehouseCode, wanted - remaining, docEntry);
            }
        }

        // The same journal the sales ledger writes to, so the day's movements are one list rather
        // than two that have to be reconciled to be read. The key matches the grain the duplicate
        // check above already works at — a document entry moves one item, in one warehouse, one way
        // — so the index says the same thing a second time, in the table where the invariant lives.
        StockMovementJournal.Append(
            context,
            snapshotDate,
            StockMovementKinds.Transfer,
            docEntry.HasValue ? $"transfer:{docEntry.Value}:{direction}" : null,
            itemCode,
            warehouseCode,
            moved,
            allRows.Sum(row => row.AvailableQuantity),
            $"transfer {docNum?.ToString() ?? "?"} {direction} {sourceWarehouse} to {destinationWarehouse}");

        logger.LogInformation(
            "Stock adjustment: {Direction} {Qty} of {ItemCode} in {Warehouse} (DocEntry={DocEntry})",
            direction, Math.Abs(adjustmentQty), itemCode, warehouseCode, docEntry);

        return new StockAdjustmentDetail(warehouseCode, direction, adjustmentQty, newAvailable);
    }
}
