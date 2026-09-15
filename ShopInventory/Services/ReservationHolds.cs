using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

/// <summary>
/// Which reservations are still holding stock — asked in one place, so every reader of "how much of
/// this is spoken for" answers the same question.
/// </summary>
/// <remarks>
/// There are two readers and they used to disagree by construction. <see cref="StockLedger"/> nets
/// holds off the morning snapshot for till sales; <see cref="StockReservationService"/> nets them off
/// SAP's issuable figure for everything that posts through SAP. Both wrote the rule out by hand — the
/// same two clauses, in eight places — so a change to what counts as a hold could be applied to one
/// side and not the other, and the two ledgers would quietly start promising different stock.
///
/// <para>
/// The rule is: a reservation holds while it is Pending, unexpired, and its invoice has not yet been
/// posted to SAP. The third clause is the one a hand-written copy kept missing. A queued desktop
/// invoice holds its units for 60 minutes and end-of-day consolidation posts it to SAP inside that
/// window, marking the queue entry <see cref="InvoiceQueueStatus.Completed"/> — but the reservation
/// stays Pending until it expires. Between the two, SAP has taken the units off and the hold is still
/// counting them, so the same units are subtracted twice and a till refuses sales for stock the shelf
/// has. The queue entry is the evidence the document exists, which is exactly when a hold stops being
/// a hold and becomes a decrement.
/// </para>
/// </remarks>
public static class ReservationHolds
{
    /// <summary>
    /// Reservation lines still holding stock as at <paramref name="asOfUtc"/>.
    /// </summary>
    public static IQueryable<StockReservationLineEntity> Lines(
        ApplicationDbContext context,
        DateTime asOfUtc) =>
        context.StockReservationLines.Where(line =>
            line.Reservation.Status == ReservationStatus.Pending
            && line.Reservation.ExpiresAt > asOfUtc
            && !context.InvoiceQueue.Any(queued =>
                queued.ReservationId == line.Reservation.ReservationId
                && queued.Status == InvoiceQueueStatus.Completed));

    /// <summary>
    /// Batch allocations still holding stock as at <paramref name="asOfUtc"/>. The same rule as
    /// <see cref="Lines"/>, one level further down.
    /// </summary>
    public static IQueryable<StockReservationBatchEntity> Batches(
        ApplicationDbContext context,
        DateTime asOfUtc) =>
        context.StockReservationBatches.Where(batch =>
            batch.ReservationLine.Reservation.Status == ReservationStatus.Pending
            && batch.ReservationLine.Reservation.ExpiresAt > asOfUtc
            && !context.InvoiceQueue.Any(queued =>
                queued.ReservationId == batch.ReservationLine.Reservation.ReservationId
                && queued.Status == InvoiceQueueStatus.Completed));
}
