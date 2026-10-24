using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.VanSalesReports.Queries;

/// <summary>
/// Which stock reservations are sales, for every report that reads online van sales.
/// </summary>
/// <remarks>
/// <para>
/// A confirmed reservation is a sale SAP has taken. So is one the invoice queue holds: when SAP is down
/// the handset's receipt is signed, the rep is told the sale went through, and the sale waits in the
/// queue with its reservation still Pending until <c>PostQueuedVanInvoices</c> confirms it. Reading
/// confirmed reservations only left those sales out of every van report for as long as SAP was away, and
/// for good where SAP then refused one and it was parked for review, so a bad day read as a quiet one.
/// </para>
/// <para>
/// A cancelled queue entry is a sale that never happened, and a cancelled reservation is never a sale.
/// A queued sale is counted once either way: it is in this set while it waits and still in it, as
/// confirmed, once SAP has it. Consolidation builds its sales in memory and saves no row of its own, so
/// nothing else counts it a second time.
/// </para>
/// </remarks>
public static class VanSaleReservations
{
    /// <summary>Every reservation that is a sale: posted to SAP, or signed and waiting in the queue for it.</summary>
    public static IQueryable<StockReservationEntity> Sold(ApplicationDbContext db) =>
        db.StockReservations.Where(reservation =>
            reservation.Status == ReservationStatus.Confirmed
            || (reservation.Status != ReservationStatus.Cancelled
                && db.InvoiceQueue.Any(queued =>
                    queued.ReservationId == reservation.ReservationId
                    && queued.Status != InvoiceQueueStatus.Cancelled)));

    /// <summary>The ids of <see cref="Sold"/>, for reads that start from the line table.</summary>
    public static IQueryable<int> SoldIds(ApplicationDbContext db) =>
        Sold(db).Select(reservation => reservation.Id);
}
