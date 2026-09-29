using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Common.Stock;

/// <summary>
/// What a van has sold that this server holds and SAP's warehouse figure does not yet reflect, per item.
/// </summary>
/// <remarks>
/// <para><b>Why the handset needs it.</b> The van app's ledger reads SAP's figure for its warehouse and
/// takes off what it believes SAP has not heard about. It used to believe that of every sale made that
/// day, because SAP once heard about van sales only at the end-of-day run. It now hears within minutes:
/// an online sale is signed and handed to the invoice queue, which posts it in seconds, and an offline
/// one posts on the next half-hourly pass. So every sale came off twice — once in SAP's figure and again
/// on the handset — and a van showed out of stock on goods it was carrying. Measured on VAN004 on
/// 2026-09-29: invoice 781775 posted at 11:22, and at 12:57 the handset showed YOG008 at 1 against SAP's
/// 6, and YOG004 and YOG015 as out with 2 and 1 on the van.</para>
///
/// <para>Only this server knows which of a van's sales SAP has absorbed, so it answers that here and the
/// handset takes off only what the server cannot know about: sales it has not sent yet.</para>
///
/// <para><b>What counts.</b> An uploaded van sale that has not reached SAP, the same rule
/// <see cref="UnpostedTillSales"/> applies to a till; a reservation still holding stock, which is how an
/// online sale waits for the queue; and a reservation being posted right now. A sale SAP has taken still
/// counts for <see cref="PostedWithin"/> afterwards, because the figure it is netted against can be that
/// old — see there.</para>
///
/// <para><b>Wrong in the safe direction, deliberately.</b> Counting a sale SAP has already taken makes
/// the handset read low for a few minutes and refuse a sale it could have made. Missing one makes it read
/// high and sell goods that are not on the van, under a fiscal receipt that cannot be withdrawn. Where
/// the two times cannot be told apart this counts.</para>
///
/// <para>Quantities are in the unit each line was recorded in, which for a van is the unit it sells in —
/// the same figure the posting run settles the ledger with.</para>
/// </remarks>
public static class VanSalesAwaitingSap
{
    /// <summary>
    /// How long after SAP takes a sale it is still counted as awaiting.
    /// </summary>
    /// <remarks>
    /// The warehouse figure a catalogue page is built from is a whole-warehouse read reused for
    /// <see cref="SAPServiceLayerClient.WarehouseBatchSnapshotLifetime"/>, so a sale posted inside that
    /// window may not be in it yet. The extra minute covers the posted time being stamped by whichever
    /// node posted the sale, while the read was timed by this one.
    /// </remarks>
    public static readonly TimeSpan PostedWithin =
        SAPServiceLayerClient.WarehouseBatchSnapshotLifetime + TimeSpan.FromMinutes(1);

    /// <summary>
    /// Units of each of <paramref name="itemCodes"/> awaiting SAP in <paramref name="warehouseCode"/>.
    /// </summary>
    /// <param name="db">The application database.</param>
    /// <param name="warehouseCode">The van's warehouse.</param>
    /// <param name="itemCodes">The items being answered for — one catalogue page.</param>
    /// <param name="nowUtc">When SAP was read, or just after; a sale taken within <see cref="PostedWithin"/> of it still counts.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <param name="lookbackDays">
    /// How far back a sale that never reached SAP is still counted — the till's
    /// <c>DailyStock:UnpostedSaleLookbackDays</c>. Past it the sale is the exception centre's to chase.
    /// </param>
    /// <returns>Only items with something awaiting; an absent code means none.</returns>
    public static async Task<Dictionary<string, decimal>> ByItemAsync(
        ApplicationDbContext db,
        string warehouseCode,
        IReadOnlyCollection<string> itemCodes,
        DateTime nowUtc,
        int lookbackDays,
        CancellationToken cancellationToken)
    {
        var awaiting = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(warehouseCode) || itemCodes.Count == 0)
        {
            return awaiting;
        }

        var codes = itemCodes.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var takenBefore = nowUtc - PostedWithin;
        var earliest = nowUtc.AddDays(-Math.Max(1, lookbackDays));

        var uploaded = await UploadedQuery(db, warehouseCode, codes, takenBefore, earliest)
            .ToListAsync(cancellationToken);

        var reserved = await ReservedQuery(db, warehouseCode, codes, nowUtc, takenBefore, earliest)
            .ToListAsync(cancellationToken);

        foreach (var line in uploaded.Concat(reserved))
        {
            if (line.Quantity > 0)
            {
                awaiting[line.ItemCode] = awaiting.GetValueOrDefault(line.ItemCode) + line.Quantity;
            }
        }

        return awaiting;
    }

    /// <summary>
    /// Offline sales, uploaded and waiting for the posting pass, per item.
    /// </summary>
    /// <remarks>
    /// <c>VanSalesOnline</c> rows are receipts for sales a reservation already speaks for, so they are left
    /// out by source. Internal so the PostgreSQL translation can be asserted on its own.
    /// </remarks>
    internal static IQueryable<ItemQuantity> UploadedQuery(
        ApplicationDbContext db,
        string warehouseCode,
        List<string> codes,
        DateTime takenBefore,
        DateTime earliest) =>
        db.DesktopSaleLines
            .AsNoTracking()
            .Where(line => line.WarehouseCode == warehouseCode
                        && codes.Contains(line.ItemCode)
                        && line.Sale.SourceSystem == SaleSourceSystems.VanSales
                        && line.Sale.CreatedAt >= earliest
                        && (line.Sale.ConsolidationStatus != DesktopSaleConsolidationStatus.Consolidated
                            || (line.Sale.PostedAt != null && line.Sale.PostedAt > takenBefore)))
            .GroupBy(line => line.ItemCode)
            .Select(group => new ItemQuantity
            {
                ItemCode = group.Key,
                Quantity = group.Sum(line => line.Quantity)
            });

    /// <summary>
    /// Online sales, which hold their stock through a reservation until the queue posts them, per item.
    /// </summary>
    internal static IQueryable<ItemQuantity> ReservedQuery(
        ApplicationDbContext db,
        string warehouseCode,
        List<string> codes,
        DateTime nowUtc,
        DateTime takenBefore,
        DateTime earliest)
    {
        var live = ReservationHolds.LiveAsOf(db, nowUtc);

        return db.StockReservationLines
            .AsNoTracking()
            .Where(line => line.WarehouseCode == warehouseCode
                        && codes.Contains(line.ItemCode)
                        && line.Reservation.CreatedAt >= earliest
                        && (live.Contains(line.ReservationId)
                            // Being posted: SAP may or may not have it yet.
                            || line.Reservation.Status == ReservationStatus.Confirming
                            || (line.Reservation.Status == ReservationStatus.Confirmed
                                && line.Reservation.ConfirmedAt != null
                                && line.Reservation.ConfirmedAt > takenBefore)
                            // Settled by the queue rather than confirmed — consolidation leaves the
                            // reservation Pending and completes its entry instead.
                            || db.InvoiceQueue.Any(queued =>
                                queued.ReservationId == line.Reservation.ReservationId
                                && queued.Status == InvoiceQueueStatus.Completed
                                && queued.ProcessedAt != null
                                && queued.ProcessedAt > takenBefore)))
            .GroupBy(line => line.ItemCode)
            .Select(group => new ItemQuantity
            {
                ItemCode = group.Key,
                Quantity = group.Sum(line => line.ReservedQuantity)
            });
    }

    internal sealed class ItemQuantity
    {
        public string ItemCode { get; init; } = string.Empty;

        public decimal Quantity { get; init; }
    }
}
