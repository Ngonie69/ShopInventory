using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;

namespace ShopInventory.Common.Stock;

/// <summary>
/// What a van is carrying, rebuilt from its morning count, the loads since and the sales received.
/// </summary>
/// <remarks>
/// <para>
/// The stored <c>AvailableQuantity</c> moves only when a sale posts, so it lags the van and is not the
/// answer. <c>OriginalQuantity</c> is SAP's book stock at the 07:00 read — a handset's count no longer
/// becomes it, see <c>ReportVanSalesStockPositionHandler</c> — transfer adjustments are how a mid-day
/// load reaches this system, and sales count whether or not they have posted, because the question is
/// what is on the van, not what SAP has been told.
/// </para>
/// <para>
/// Two readers. The stock-position endpoint answers a rep, and counts every sale. The reservation
/// check reads it while SAP is down, and leaves out online sales whose reservation is still holding
/// stock: that check subtracts live holds itself, and counting the sale as well would take its units
/// off twice and refuse a sale the van can make.
/// </para>
/// <para>
/// Dated by the CAT calendar date. The 07:00 read writes the day's row under the ledger day, which is
/// the same date from then on; before it there is no row for the day and the position is unknown.
/// </para>
/// </remarks>
public static class VanStockPosition
{
    /// <param name="db">The operational database.</param>
    /// <param name="warehouseCode">The van's warehouse.</param>
    /// <param name="tradingDate">The CAT calendar date.</param>
    /// <param name="leaveOutSalesStillHeld">
    /// True to leave out sales whose reservation is still a live hold, for a caller that subtracts holds.
    /// </param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public static async Task<VanStockPositionReading> ReadAsync(
        ApplicationDbContext db,
        string warehouseCode,
        DateTime tradingDate,
        bool leaveOutSalesStillHeld,
        CancellationToken cancellationToken)
    {
        var snapshotId = await db.DailyStockSnapshots
            .AsNoTracking()
            .Where(header => header.WarehouseCode == warehouseCode && header.SnapshotDate == tradingDate)
            .Select(header => (int?)header.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (snapshotId is not { } id)
        {
            return VanStockPositionReading.NotCounted;
        }

        var opening = await db.DailyStockSnapshotItems
            .AsNoTracking()
            .Where(row => row.SnapshotId == id)
            .GroupBy(row => row.ItemCode)
            .Select(group => new
            {
                ItemCode = group.Key,
                Quantity = group.Sum(row => row.OriginalQuantity),
                Description = group.Max(row => row.ItemDescription)
            })
            .ToListAsync(cancellationToken);

        var transfers = await db.StockTransferAdjustments
            .AsNoTracking()
            .Where(adjustment => adjustment.SnapshotDate == tradingDate && adjustment.WarehouseCode == warehouseCode)
            .GroupBy(adjustment => adjustment.ItemCode)
            .Select(group => new { ItemCode = group.Key, Quantity = group.Sum(a => a.AdjustmentQuantity) })
            .ToListAsync(cancellationToken);

        // The CAT trading day in UTC terms: CAT runs two hours ahead, so the day opens at 22:00 UTC the
        // evening before.
        var from = tradingDate.AddHours(-2);
        var to = from.AddDays(1);

        var soldLines = db.DesktopSaleLines
            .AsNoTracking()
            .Where(line => line.WarehouseCode == warehouseCode
                        && line.Sale.CreatedAt >= from
                        && line.Sale.CreatedAt < to);

        if (leaveOutSalesStillHeld)
        {
            var live = ReservationHolds.LiveAsOf(db, DateTime.UtcNow);
            soldLines = soldLines.Where(line => !db.StockReservations.Any(reservation =>
                live.Contains(reservation.Id)
                && reservation.ExternalReferenceId == line.Sale.ExternalReferenceId));
        }

        var sold = await soldLines
            .GroupBy(line => line.ItemCode)
            .Select(group => new { ItemCode = group.Key, Quantity = group.Sum(line => line.Quantity) })
            .ToListAsync(cancellationToken);

        var openingByItem = opening.ToDictionary(row => row.ItemCode, row => row.Quantity, StringComparer.OrdinalIgnoreCase);
        var descriptionByItem = opening
            .Where(row => row.Description is not null)
            .ToDictionary(row => row.ItemCode, row => row.Description, StringComparer.OrdinalIgnoreCase);
        var transferByItem = transfers.ToDictionary(row => row.ItemCode, row => row.Quantity, StringComparer.OrdinalIgnoreCase);
        var soldByItem = sold.ToDictionary(row => row.ItemCode, row => row.Quantity, StringComparer.OrdinalIgnoreCase);

        // Every item any of the three knows about. An item loaded mid-day has no opening row, and one
        // sold down to nothing still belongs on the list.
        var items = openingByItem.Keys
            .Concat(transferByItem.Keys)
            .Concat(soldByItem.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .Select(code => new VanItemPosition(
                code,
                descriptionByItem.GetValueOrDefault(code),
                openingByItem.GetValueOrDefault(code),
                transferByItem.GetValueOrDefault(code),
                soldByItem.GetValueOrDefault(code)))
            .ToList();

        return new VanStockPositionReading(true, items);
    }
}

/// <param name="Counted">False when the van has filed no opening count for the day: its position is unknown.</param>
/// <param name="Items">One entry per item, in item-code order.</param>
public sealed record VanStockPositionReading(bool Counted, IReadOnlyList<VanItemPosition> Items)
{
    public static readonly VanStockPositionReading NotCounted = new(false, []);
}

public sealed record VanItemPosition(
    string ItemCode,
    string? Description,
    decimal Opening,
    decimal Transferred,
    decimal Sold)
{
    /// <summary>
    /// What is left, floored at zero: a van that has sold more than this system thinks it loaded is a
    /// known condition the end-of-day posting reports as a shortfall, not stock owed.
    /// </summary>
    public decimal Quantity => Math.Max(0m, Opening + Transferred - Sold);
}
