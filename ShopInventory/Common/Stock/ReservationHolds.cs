using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Common.Stock;

/// <summary>
/// Which reservations are still holding stock, asked in one place so every reader answers the same.
/// </summary>
/// <remarks>
/// <para><b>Why one definition.</b> A reservation is netted off in a dozen queries — the stock ledger
/// the tills read, the SAP-side reads that gate web invoices and batch allocation, the validation that
/// gates a new reservation, the summary the reservations page renders, and the job that expires them.
/// Each of those used to spell the rule out for itself as <c>Pending &amp;&amp; ExpiresAt &gt; now</c>,
/// so a correction to the rule had to be made a dozen times and was only ever made in some of them.</para>
///
/// <para><b>The rule.</b> A reservation holds stock while it is <see cref="ReservationStatus.Pending"/>
/// and its queued invoice has not been settled. A reservation with no queue entry behind it — a rep
/// holding stock for a customer in the shop — holds until <see cref="StockReservationEntity.ExpiresAt"/>
/// and no longer, which is what that column is for. A reservation created by a queued invoice is
/// governed by the queue instead, because the queue is the thing that knows whether the sale happened.</para>
///
/// <para><b>Why the queue outranks the clock.</b> A desktop invoice queued at 09:00 reserves its stock
/// for an hour. It is fiscalised within minutes — the goods leave, the receipt is lodged with ZIMRA —
/// and SAP is not told until the 16:45 consolidation posts one invoice per business partner. At 10:00
/// the hold used to lapse, so for most of the trading day the same units were on the ledger and on
/// SAP's shelf, free to be sold a second time at a till, with nothing to catch it: the hourly
/// reconciliation only asks SAP about rows whose quantity has changed, and a reservation changes no
/// row. Holding until the queue entry is settled closes that window.</para>
///
/// <para><b>Settled means the units are counted somewhere else, or were never sold.</b>
/// <see cref="InvoiceQueueStatus.Completed"/> — consolidation posted them to SAP and took them off the
/// ledger, so counting the hold as well would take them twice — and
/// <see cref="InvoiceQueueStatus.Cancelled"/>, where there is no sale. Every other state, including
/// <see cref="InvoiceQueueStatus.Failed"/> and <see cref="InvoiceQueueStatus.RequiresReview"/>, is a
/// sale still in flight: a parked entry may already carry a fiscal receipt, and a hold that outstays
/// its usefulness refuses a sale, where one that ends early sells stock that has gone.</para>
/// </remarks>
public static class ReservationHolds
{
    /// <summary>
    /// The ids of the reservations holding stock at <paramref name="asOf"/>, as a subquery for the
    /// caller to test its own rows against.
    /// </summary>
    /// <remarks>
    /// Ids rather than a predicate because the callers filter lines and batch allocations rather than
    /// reservations, and EF cannot invoke a stored expression inside another one. As a subquery this
    /// stays on the server: <c>WHERE ReservationId IN (SELECT ...)</c>.
    /// </remarks>
    public static IQueryable<int> LiveAsOf(ApplicationDbContext db, DateTime asOf) =>
        db.StockReservations
            .Where(reservation => reservation.Status == ReservationStatus.Pending
                // Settled: consolidation has the units, or there was never a sale.
                && !db.InvoiceQueue.Any(queued =>
                    queued.ReservationId == reservation.ReservationId
                    && (queued.Status == InvoiceQueueStatus.Completed
                        || queued.Status == InvoiceQueueStatus.Cancelled))
                // Unexpired, or still owed by a queue entry. Given the clause above, any entry that
                // survives to here is one still working towards a document.
                && (reservation.ExpiresAt > asOf
                    || db.InvoiceQueue.Any(queued => queued.ReservationId == reservation.ReservationId)))
            .Select(reservation => reservation.Id);

    /// <summary>
    /// The lines of every reservation holding stock at <paramref name="asOf"/>.
    /// </summary>
    public static IQueryable<StockReservationLineEntity> LiveLinesAsOf(
        ApplicationDbContext db,
        DateTime asOf)
    {
        var live = LiveAsOf(db, asOf);
        return db.StockReservationLines.Where(line => live.Contains(line.ReservationId));
    }

    /// <summary>
    /// The batch allocations of every reservation holding stock at <paramref name="asOf"/>.
    /// </summary>
    public static IQueryable<StockReservationBatchEntity> LiveBatchesAsOf(
        ApplicationDbContext db,
        DateTime asOf)
    {
        var live = LiveAsOf(db, asOf);
        return db.StockReservationBatches.Where(batch => live.Contains(batch.ReservationLine.ReservationId));
    }
}
