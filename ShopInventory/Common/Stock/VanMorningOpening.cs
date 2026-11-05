using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Common.Stock;

/// <summary>
/// Turns a van's read of SAP stock into the van's opening for the day, whenever the read was made.
/// </summary>
/// <remarks>
/// <para><b>Why a read can be late.</b> A van's snapshot is meant to be read at 07:00, before it has
/// loaded or sold anything. When SAP does not answer then, the read is made later: by the ten-minute
/// retry, by the catch-up after a restart, or by the Fetch stock button. On 2026-11-04 SAP failed the
/// 07:00 read for all fifteen vans and they were read at 10:50, after the restart.</para>
///
/// <para><b>Why a late read cannot be the opening as it stands.</b> Everything that reads a van's day
/// (<see cref="VanStockPosition"/>, the van stock report) takes the opening, adds every load recorded
/// for the day and takes off every sale made that day. SAP's figure at a late read already holds the
/// loads booked before it and has the posted sales taken off. Used as the opening, those loads would
/// count twice, so the van would read high, and those sales would be taken off twice.</para>
///
/// <para><b>What is done instead.</b> The opening is SAP's figure with those undone: less the loads the
/// ledger has recorded for the day, plus the sales SAP has already had. A sale SAP has not had is left
/// alone, because SAP's figure is still carrying it. At 07:00 there is normally nothing of either, so
/// the morning read is unchanged.</para>
///
/// <para><b>Only <c>OriginalQuantity</c> moves.</b> <c>AvailableQuantity</c> is the running figure, and
/// a van's runs from the opening through the loads and the posted sales, which is SAP's figure at the
/// moment of reading. So it opens at what SAP said, and the posts still to come take off what SAP has
/// not had yet.</para>
///
/// <para><b>Errs low.</b> A sale that posts while SAP is being read, or a load the listener delivers
/// while it is being read, can be undone when SAP's figure did not hold it yet. Both make the van read
/// lower, which refuses a sale rather than letting one through that SAP would then refuse.</para>
/// </remarks>
public static class VanMorningOpening
{
    /// <summary>
    /// Units of each item on the van's sales for the day that SAP has already had, per item.
    /// </summary>
    /// <remarks>
    /// Read before SAP's stock, so a sale that posts while SAP is being read is not among them: SAP's
    /// figure may not hold it, and putting it back would read high.
    /// </remarks>
    public static async Task<Dictionary<string, decimal>> SalesSapHasAsync(
        ApplicationDbContext db,
        string warehouseCode,
        DateTime tradingDate,
        CancellationToken cancellationToken)
    {
        var lines = await VanStockPosition.SaleLinesOfTheDay(db, warehouseCode, tradingDate)
            .Where(line => line.Sale.SapDocEntry != null || line.Sale.PostedAt != null)
            .GroupBy(line => line.ItemCode)
            .Select(group => new { ItemCode = group.Key, Quantity = group.Sum(line => line.Quantity) })
            .ToListAsync(cancellationToken);

        return Sum(lines.Select(line => (line.ItemCode, line.Quantity)));
    }

    /// <summary>
    /// The day's loads and returns the ledger has recorded for the van, per item: positive for stock in.
    /// </summary>
    public static async Task<Dictionary<string, decimal>> TransfersRecordedAsync(
        ApplicationDbContext db,
        string warehouseCode,
        DateTime tradingDate,
        CancellationToken cancellationToken)
    {
        var adjustments = await db.StockTransferAdjustments
            .AsNoTracking()
            .Where(adjustment => adjustment.SnapshotDate == tradingDate && adjustment.WarehouseCode == warehouseCode)
            .GroupBy(adjustment => adjustment.ItemCode)
            .Select(group => new { ItemCode = group.Key, Quantity = group.Sum(adjustment => adjustment.AdjustmentQuantity) })
            .ToListAsync(cancellationToken);

        return Sum(adjustments.Select(adjustment => (adjustment.ItemCode, adjustment.Quantity)));
    }

    /// <summary>
    /// Moves the rows' <c>OriginalQuantity</c> from SAP's figure to the van's opening for the day.
    /// </summary>
    /// <param name="rows">The rows about to be written, at SAP's figure. Rows may be added.</param>
    /// <param name="snapshotId">The snapshot the rows belong to, for a row this has to add.</param>
    /// <param name="warehouseCode">The van.</param>
    /// <param name="salesSapHas">From <see cref="SalesSapHasAsync"/>: put back.</param>
    /// <param name="transfers">
    /// The day's transfers SAP's figure holds, signed, per item: taken back out. Stock in is positive,
    /// so a return to the depot is put back.
    /// </param>
    /// <returns>
    /// How many items' openings moved, and a sentence for each item whose opening could not go as low
    /// as the arithmetic said. An opening cannot be negative, so such an item reads high by the amount
    /// given; it means the day's records and SAP's figure disagree.
    /// </returns>
    public static VanOpeningAdjustment Apply(
        List<DailyStockSnapshotItemEntity> rows,
        int snapshotId,
        string warehouseCode,
        IReadOnlyDictionary<string, decimal> salesSapHas,
        IReadOnlyDictionary<string, decimal> transfers)
    {
        var problems = new List<string>();
        var moved = 0;

        var items = salesSapHas.Keys
            .Concat(transfers.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase);

        foreach (var itemCode in items)
        {
            var change = salesSapHas.GetValueOrDefault(itemCode) - transfers.GetValueOrDefault(itemCode);

            if (change == 0m)
            {
                continue;
            }

            moved++;

            // Soonest to expire first, as everything else that draws a van's rows down orders them.
            var itemRows = rows
                .Where(row => string.Equals(row.ItemCode, itemCode, StringComparison.OrdinalIgnoreCase))
                .OrderBy(row => row.ExpiryDate ?? DateTime.MaxValue)
                .ToList();

            if (change > 0m)
            {
                if (itemRows.Count > 0)
                {
                    itemRows[0].OriginalQuantity += change;
                    continue;
                }

                // Sold out by the time SAP was read, so SAP gave no row. The van still opened with these,
                // and without a row they would be taken off a zero opening and read as an over-sale.
                var row = new DailyStockSnapshotItemEntity
                {
                    SnapshotId = snapshotId,
                    ItemCode = itemCode,
                    WarehouseCode = warehouseCode,
                    BatchNumber = null,
                    ExpiryDate = null
                }.Opening(0m);
                row.OriginalQuantity = change;
                rows.Add(row);
                continue;
            }

            var outstanding = -change;

            foreach (var row in itemRows)
            {
                var taken = Math.Min(row.OriginalQuantity, outstanding);
                row.OriginalQuantity -= taken;
                outstanding -= taken;

                if (outstanding == 0m)
                {
                    break;
                }
            }

            if (outstanding > 0m)
            {
                problems.Add(
                    $"{itemCode}: the day's loads come to {outstanding} more than SAP's figure and the "
                    + "sales it has had allow for, so its opening is left at zero and reads high by that much");
            }
        }

        return new VanOpeningAdjustment(moved, problems);
    }

    private static Dictionary<string, decimal> Sum(IEnumerable<(string ItemCode, decimal Quantity)> entries)
    {
        var totals = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        foreach (var (itemCode, quantity) in entries)
        {
            totals[itemCode] = totals.GetValueOrDefault(itemCode) + quantity;
        }

        return totals;
    }
}

/// <param name="ItemsMoved">Items whose opening is not SAP's figure as read.</param>
/// <param name="Problems">One sentence per item whose opening reads high; see <see cref="VanMorningOpening.Apply"/>.</param>
public sealed record VanOpeningAdjustment(int ItemsMoved, IReadOnlyList<string> Problems);
