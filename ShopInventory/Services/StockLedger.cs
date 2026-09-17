using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Stock;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

/// <summary>
/// One line's claim on the ledger.
/// </summary>
public sealed record StockLedgerLine(string ItemCode, string WarehouseCode, decimal Quantity);

/// <summary>
/// Whether the ledger has anything to say about a warehouse.
/// </summary>
public enum StockLedgerCoverage
{
    /// <summary>
    /// The warehouse has a completed snapshot for the day, so the ledger is authoritative for it.
    /// </summary>
    Tracked,

    /// <summary>
    /// No snapshot exists. Not the same as no stock, and the difference matters: reading an absent
    /// snapshot as zero would refuse every document for every warehouse the morning job does not
    /// cover.
    /// </summary>
    NotTracked
}

/// <param name="ItemCode">The item the document took more of than the ledger held.</param>
/// <param name="WarehouseCode">Where it took it from.</param>
/// <param name="Taken">What the document says left.</param>
/// <param name="Held">What the ledger had to give.</param>
public sealed record StockLedgerShortfall(
    string ItemCode,
    string WarehouseCode,
    decimal Taken,
    decimal Held)
{
    /// <summary>How much more left than the ledger knew about.</summary>
    public decimal Excess => Taken - Held;
}

/// <param name="Coverage">Whether the ledger tracks this warehouse at all.</param>
/// <param name="Available">Units left to promise. Meaningless when <see cref="Coverage"/> is NotTracked.</param>
public sealed record StockLedgerReading(StockLedgerCoverage Coverage, decimal Available);

/// <param name="Committed">False when at least one line could not be covered; nothing was taken.</param>
/// <param name="Shortfalls">One message per line that could not be covered.</param>
/// <param name="UntrackedWarehouses">
/// Warehouses with no snapshot for the day, whose lines were passed over rather than checked.
/// </param>
/// <remarks>
/// <see cref="UntrackedWarehouses"/> is reported rather than decided on, because the two kinds of
/// caller want opposite things from it. A till must refuse: it sells only from warehouses the
/// morning job covers, so a missing snapshot means the figures it would sell against do not exist.
/// A web invoice must carry on: most warehouses are not covered, they have no second consumer to
/// collide with, and SAP is authority enough for them.
/// </remarks>
public sealed record StockLedgerOutcome(
    bool Committed,
    IReadOnlyList<string> Shortfalls,
    IReadOnlyList<string> UntrackedWarehouses)
{
    public static readonly StockLedgerOutcome Success = new(true, [], []);
}

/// <summary>
/// The single running answer to "how much of this is left to promise today".
/// </summary>
/// <remarks>
/// Two stock ledgers used to gate the documents this system posts, and they could not see each
/// other. Shop tills validated against the morning snapshot; web invoices and the end-of-day
/// consolidation read live SAP. The same units could therefore be sold twice, once from each side,
/// and both documents were individually valid against the ledger each consulted:
///
/// <list type="bullet">
/// <item>a till sale is captured and sits Pending for up to a minute before it reaches SAP, and a
/// web invoice raised in that window reads SAP, sees the stock, and posts;</item>
/// <item>a web invoice posts and decrements SAP, but nothing decremented the snapshot, so the till
/// went on selling the same units for the rest of the trading day.</item>
/// </list>
///
/// <para><b>What the number means.</b> Snapshot at 07:00, less everything this system has committed
/// since. That makes it the conservative of the two figures whenever a sale is captured but not yet
/// posted, and equal to SAP otherwise — so a caller that checks both and takes the tighter is
/// correct in both directions. It is deliberately <i>not</i> a replacement for the SAP read: work
/// done directly in SAP B1 is invisible here, and only the live read catches that.</para>
///
/// <para><b>Who commits.</b> A document commits once, at the moment it becomes an obligation. A till
/// sale commits when it is captured, because the goods leave then; the job that posts it to SAP
/// later must not commit again, and neither must the end-of-day consolidation, which invoices sales
/// that were each accounted for at capture. Double-committing is the failure mode to watch for
/// here, and it is quieter than the one this replaces: stock simply goes missing from the ledger.</para>
/// </remarks>
public interface IStockLedger
{
    /// <summary>What is left to promise for one item in one warehouse.</summary>
    Task<StockLedgerReading> ReadAsync(
        string itemCode,
        string warehouseCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes every line or none. Lines in warehouses the ledger does not track are passed over and
    /// named in <see cref="StockLedgerOutcome.UntrackedWarehouses"/> for the caller to rule on.
    /// </summary>
    /// <param name="lines">The document's claim on the shelf, one entry per line.</param>
    /// <param name="reference">How the movement reads to a person. A label, never an identity.</param>
    /// <param name="documentKey">
    /// A stable identity for the document, or null when the caller has none. Given one, a second
    /// call for the same document takes nothing and reports the first call's success — which is what
    /// makes a retried command safe. Pass null rather than something that might repeat across
    /// documents: a colliding key would silently stop the second real document taking its units,
    /// which is worse than the double-take it was meant to prevent.
    /// </param>
    /// <param name="cancellationToken">Cancels the read and the save.</param>
    Task<StockLedgerOutcome> TryCommitAsync(
        IReadOnlyList<StockLedgerLine> lines,
        string reference,
        string? documentKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records units that have already gone, on a document that exists. Cannot refuse.
    /// </summary>
    /// <returns>
    /// Lines the ledger did not hold enough of. Never a reason to stop the document — it is already
    /// posted — but each one says the ledger and the shelf had drifted apart before it arrived, and
    /// for a van that is the shape an over-sale takes.
    /// </returns>
    /// <remarks>
    /// Distinct from <see cref="TryCommitAsync"/>, which asks. By the time this is called the answer
    /// is not in doubt — SAP has the document — so refusing would only leave the ledger claiming
    /// stock that is physically gone, which is the direction that oversells.
    /// </remarks>
    /// <param name="lines">The document's claim on the shelf, one entry per line.</param>
    /// <param name="reference">How the movement reads to a person. A label, never an identity.</param>
    /// <param name="documentKey">
    /// As on <see cref="TryCommitAsync"/>. A repeat under a known key takes nothing and reports no
    /// shortfalls — the shortfalls of the first call were reported then, and re-deriving them now
    /// would describe a ledger that has already moved on.
    /// </param>
    /// <param name="cancellationToken">Cancels the read and the save.</param>
    Task<IReadOnlyList<StockLedgerShortfall>> TakeSettledAsync(
        IReadOnlyList<StockLedgerLine> lines,
        string reference,
        string? documentKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts units back — a document that was refused after its claim was taken, or one that was
    /// reversed. Never fails on an untracked warehouse.
    /// </summary>
    /// <param name="lines">The document's claim on the shelf, one entry per line.</param>
    /// <param name="reference">How the movement reads to a person. A label, never an identity.</param>
    /// <param name="documentKey">As on <see cref="TryCommitAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the read and the save.</param>
    Task ReleaseAsync(
        IReadOnlyList<StockLedgerLine> lines,
        string reference,
        string? documentKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The day whose snapshot the ledger is reading. Exposed so callers that already resolve a date
    /// for themselves can be checked against it.
    /// </summary>
    DateTime CurrentLedgerDay { get; }
}

public sealed class StockLedger(
    ApplicationDbContext context,
    IOptions<DailyStockSettings> dailyStock,
    ILogger<StockLedger> logger) : IStockLedger
{
    private const int MaxConcurrencyRetries = 3;

    /// <summary>
    /// The day whose snapshot is in force now. See <see cref="StockLedgerDay"/> for why this is
    /// neither the UTC date nor the CAT date.
    /// </summary>
    /// <remarks>
    /// The day the journal is kept under, and not always the day of the rows being moved. While a
    /// shop's snapshot for today is still being fetched the ledger moves yesterday's rows (see
    /// <see cref="StockSnapshotInForce"/>) but journals those movements here, under today. That is
    /// deliberate, for two reasons. The duplicate check reads this day, so a sale retried after today's
    /// snapshot lands is still recognised instead of taking its units a second time. And the fetch
    /// that finishes today's snapshot reads today's journal to take those movements off the new rows,
    /// which is what stops them coming back onto the shelf.
    /// </remarks>
    public DateTime CurrentLedgerDay => StockLedgerDay.Today(dailyStock.Value.StockFetchTimeCAT);

    public async Task<StockLedgerReading> ReadAsync(
        string itemCode,
        string warehouseCode,
        CancellationToken cancellationToken = default)
    {
        if (await TrackedDayAsync(warehouseCode, cancellationToken) is not { } snapshotDay)
        {
            return new StockLedgerReading(StockLedgerCoverage.NotTracked, 0m);
        }

        var onSnapshot = await RowsFor(itemCode, warehouseCode, snapshotDay)
            .SumAsync(row => row.AvailableQuantity, cancellationToken);

        var held = await HeldByReservationsAsync(itemCode, warehouseCode, cancellationToken);

        return new StockLedgerReading(StockLedgerCoverage.Tracked, onSnapshot - held);
    }

    public async Task<StockLedgerOutcome> TryCommitAsync(
        IReadOnlyList<StockLedgerLine> lines,
        string reference,
        string? documentKey,
        CancellationToken cancellationToken = default)
    {
        var claims = Aggregate(lines);
        if (claims.Count == 0)
        {
            return StockLedgerOutcome.Success;
        }

        if (await AlreadyRecordedAsync(StockMovementKinds.Commit, documentKey, cancellationToken))
        {
            // The document took its units already. Reporting success is not a convenience: the
            // caller asked whether it may proceed, and the answer that made it an obligation was
            // yes. Taking them a second time is the failure this key exists to prevent.
            logger.LogInformation(
                "Stock ledger already holds a commit for {Reference} ({DocumentKey}); took nothing",
                reference, documentKey);
            return StockLedgerOutcome.Success;
        }

        for (var attempt = 1; attempt <= MaxConcurrencyRetries; attempt++)
        {
            var trackedClaims = new List<(StockLedgerLine Claim, List<DailyStockSnapshotItemEntity> Rows)>();
            var shortfalls = new List<string>();
            var untracked = new List<string>();

            foreach (var claim in claims)
            {
                if (await TrackedDayAsync(claim.WarehouseCode, cancellationToken) is not { } snapshotDay)
                {
                    // Passed over, and reported. What it means is the caller's to decide — see
                    // StockLedgerOutcome.UntrackedWarehouses.
                    if (!untracked.Contains(claim.WarehouseCode, StringComparer.OrdinalIgnoreCase))
                    {
                        untracked.Add(claim.WarehouseCode);
                    }

                    continue;
                }

                var rows = await RowsFor(claim.ItemCode, claim.WarehouseCode, snapshotDay)
                    .OrderBy(row => row.ExpiryDate)
                    .ToListAsync(cancellationToken);

                var held = await HeldByReservationsAsync(claim.ItemCode, claim.WarehouseCode, cancellationToken);
                var available = rows.Sum(row => row.AvailableQuantity) - held;

                if (available < claim.Quantity)
                {
                    var heldNote = held > 0 ? $" ({Quantity(held)} of it reserved)" : string.Empty;
                    shortfalls.Add(
                        $"{claim.ItemCode} in {claim.WarehouseCode}: {Quantity(claim.Quantity)} requested, "
                        + $"{Quantity(available)} left to promise today{heldNote}");
                    continue;
                }

                trackedClaims.Add((claim, rows));
            }

            if (shortfalls.Count > 0)
            {
                return new StockLedgerOutcome(false, shortfalls, untracked);
            }

            if (trackedClaims.Count == 0)
            {
                return new StockLedgerOutcome(true, [], untracked);
            }

            foreach (var (claim, rows) in trackedClaims)
            {
                var taken = Take(rows, claim.Quantity).Sum(row => row.Taken);
                Journal(StockMovementKinds.Commit, claim, rows, -taken, reference, documentKey);
            }

            try
            {
                await context.SaveChangesAsync(cancellationToken);

                logger.LogInformation(
                    "Stock ledger committed {LineCount} line(s) for {Reference} on {LedgerDay:yyyy-MM-dd}",
                    trackedClaims.Count, reference, CurrentLedgerDay);

                return new StockLedgerOutcome(true, [], untracked);
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxConcurrencyRetries)
            {
                // Somebody else took from the same rows between the read and the write. Drop what
                // was read and start again — the retry may well find there is no longer enough,
                // which is the right answer.
                logger.LogWarning(
                    "Stock ledger hit a concurrent write for {Reference}, attempt {Attempt} of {Max}",
                    reference, attempt, MaxConcurrencyRetries);
                Detach();
            }
        }

        return new StockLedgerOutcome(
            false,
            ["The stock ledger is being written by other sales faster than this one could take its share. Please retry."],
            []);
    }

    public async Task<IReadOnlyList<StockLedgerShortfall>> TakeSettledAsync(
        IReadOnlyList<StockLedgerLine> lines,
        string reference,
        string? documentKey,
        CancellationToken cancellationToken = default)
    {
        var shortfalls = new List<StockLedgerShortfall>();

        if (await AlreadyRecordedAsync(StockMovementKinds.Settle, documentKey, cancellationToken))
        {
            logger.LogInformation(
                "Stock ledger already holds a settlement for {Reference} ({DocumentKey}); took nothing",
                reference, documentKey);
            return shortfalls;
        }

        foreach (var claim in Aggregate(lines))
        {
            if (await TrackedDayAsync(claim.WarehouseCode, cancellationToken) is not { } snapshotDay)
            {
                continue;
            }

            var rows = await RowsFor(claim.ItemCode, claim.WarehouseCode, snapshotDay)
                .OrderBy(row => row.ExpiryDate)
                .ToListAsync(cancellationToken);

            var availableBefore = rows.Sum(row => row.AvailableQuantity);
            var taken = Take(rows, claim.Quantity).Sum(row => row.Taken);
            Journal(StockMovementKinds.Settle, claim, rows, -taken, reference, documentKey);

            // Take stops when the rows run out, so the shortfall goes unrecorded rather than driving
            // a row negative. Worth saying: a settled document for more than the ledger held means
            // the ledger and the shelf had already drifted apart before this document existed.
            if (availableBefore < claim.Quantity)
            {
                shortfalls.Add(new StockLedgerShortfall(
                    claim.ItemCode, claim.WarehouseCode, claim.Quantity, availableBefore));

                logger.LogWarning(
                    "Stock ledger recorded {Quantity} of {ItemCode} leaving {WarehouseCode} for {Reference}, "
                    + "but held only {Available}. The ledger had already drifted from the shelf.",
                    claim.Quantity, claim.ItemCode, claim.WarehouseCode, reference, availableBefore);
            }
        }

        await context.SaveChangesAsync(cancellationToken);
        return shortfalls;
    }

    public async Task ReleaseAsync(
        IReadOnlyList<StockLedgerLine> lines,
        string reference,
        string? documentKey,
        CancellationToken cancellationToken = default)
    {
        var claims = Aggregate(lines);

        if (await AlreadyRecordedAsync(StockMovementKinds.Release, documentKey, cancellationToken))
        {
            // Releasing twice is the direction that oversells: it puts units back that were never
            // taken a second time, and the ledger then promises stock that is not on the shelf.
            logger.LogInformation(
                "Stock ledger already holds a release for {Reference} ({DocumentKey}); returned nothing",
                reference, documentKey);
            return;
        }

        foreach (var claim in claims)
        {
            if (await TrackedDayAsync(claim.WarehouseCode, cancellationToken) is not { } snapshotDay)
            {
                continue;
            }

            var rows = await RowsFor(claim.ItemCode, claim.WarehouseCode, snapshotDay)
                .OrderBy(row => row.ExpiryDate)
                .ToListAsync(cancellationToken);

            if (rows.Count == 0)
            {
                // Nothing to put it back into. The morning fetch will restate the position; until
                // then the ledger is short by this much, which refuses sales rather than allowing
                // them.
                logger.LogWarning(
                    "Stock ledger could not return {Quantity} of {ItemCode} to {WarehouseCode} for {Reference}: "
                    + "the item has no row in today's snapshot",
                    claim.Quantity, claim.ItemCode, claim.WarehouseCode, reference);
                continue;
            }

            // Back into the row it would have come out of first.
            rows[0].Move(claim.Quantity);
            Journal(StockMovementKinds.Release, claim, rows, claim.Quantity, reference, documentKey);
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Renders a quantity the way a person would write it, so a shortfall reads "2" rather than
    /// "2.000000".
    /// </summary>
    /// <remarks>
    /// Arithmetic on decimals keeps the widest scale of its operands, so subtracting two snapshot
    /// quantities gives six decimal places whatever the numbers are. These strings are read by
    /// cashiers.
    /// </remarks>
    private static string Quantity(decimal value) =>
        value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Takes <paramref name="quantity"/> across the rows, soonest to expire first — the same order
    /// the till's own deduction used, and the order SAP's FEFO allocation picks.
    /// </summary>
    /// <returns>
    /// What came out of each row. Returned rather than applied silently because the journal has to
    /// record the movement at the grain it actually happened — per batch row — and only this method
    /// knows how the claim was spread.
    /// </returns>
    private static List<(DailyStockSnapshotItemEntity Row, decimal Taken)> Take(
        List<DailyStockSnapshotItemEntity> rows,
        decimal quantity)
    {
        var taken = new List<(DailyStockSnapshotItemEntity, decimal)>();
        var remaining = quantity;

        foreach (var row in rows)
        {
            if (remaining <= 0)
            {
                break;
            }

            if (row.AvailableQuantity <= 0)
            {
                continue;
            }

            var fromRow = Math.Min(row.AvailableQuantity, remaining);
            row.Move(-fromRow);
            remaining -= fromRow;
            taken.Add((row, fromRow));
        }

        return taken;
    }

    /// <summary>
    /// Writes the movement, in the same change tracker as the balances so that both reach the
    /// database in one save or neither does.
    /// </summary>
    /// <param name="kind">One of <see cref="StockMovementKinds"/>.</param>
    /// <param name="claim">The item and warehouse the movement is about.</param>
    /// <param name="rows">
    /// The snapshot rows behind that claim, already updated. Their total is what the movement
    /// records as the balance afterwards — the per-batch split stays on the rows themselves.
    /// </param>
    /// <param name="quantity">
    /// Signed as the balance moved: negative where units left. Taken from what the rows actually
    /// gave rather than from what was asked for, so a settlement that ran the rows out journals
    /// the units that really moved and the invariant still holds.
    /// </param>
    /// <param name="reference">How the movement reads to a person.</param>
    /// <param name="documentKey">The document's identity, or null when it has none.</param>
    private void Journal(
        string kind,
        StockLedgerLine claim,
        List<DailyStockSnapshotItemEntity> rows,
        decimal quantity,
        string reference,
        string? documentKey)
    {
        StockMovementJournal.Append(
            context,
            CurrentLedgerDay,
            kind,
            documentKey,
            claim.ItemCode,
            claim.WarehouseCode,
            quantity,
            rows.Sum(row => row.AvailableQuantity),
            reference);
    }

    /// <summary>
    /// Whether this document has already moved the ledger this way today.
    /// </summary>
    /// <param name="kind">Which of <see cref="StockMovementKinds"/> to look for.</param>
    /// <param name="documentKey">The document's identity. Null always answers false.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <remarks>
    /// The unique index is the guarantee; this read is what turns the guarantee into an answer
    /// rather than an exception, and what stops the balances being moved before the insert that
    /// would have rejected them. Both are needed: two concurrent replays can pass this check
    /// together, and the index is what settles which of them wins.
    /// </remarks>
    private async Task<bool> AlreadyRecordedAsync(
        string kind,
        string? documentKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(documentKey))
        {
            // No identity, nothing to recognise a repeat by. The movement is still journalled; it
            // simply cannot be deduplicated, which is the honest answer rather than a guessed one.
            return false;
        }

        var day = CurrentLedgerDay;
        return await context.StockMovements.AnyAsync(
            movement => movement.LedgerDay == day
                     && movement.Kind == kind
                     && movement.DocumentKey == documentKey,
            cancellationToken);
    }


    /// <summary>
    /// One claim per item and warehouse. Two lines of the same document naming the same item are one
    /// demand on the shelf, and checking them separately would let a document take twice what is
    /// there.
    /// </summary>
    private static List<StockLedgerLine> Aggregate(IReadOnlyList<StockLedgerLine> lines) =>
        lines
            .Where(line => !string.IsNullOrWhiteSpace(line.ItemCode)
                        && !string.IsNullOrWhiteSpace(line.WarehouseCode)
                        && line.Quantity > 0)
            .GroupBy(line => (
                ItemCode: line.ItemCode.Trim().ToUpperInvariant(),
                WarehouseCode: line.WarehouseCode.Trim().ToUpperInvariant()))
            .Select(group => new StockLedgerLine(
                group.Key.ItemCode,
                group.Key.WarehouseCode,
                group.Sum(line => line.Quantity)))
            .ToList();

    /// <summary>
    /// Units a live reservation is holding, which are on the shelf but already spoken for.
    /// </summary>
    /// <remarks>
    /// A reservation is a hold rather than a commitment, so it is read here rather than written into
    /// the snapshot. Its own status is the lifecycle: cancelling or expiring one drops it out of
    /// this sum with no bookkeeping to get wrong, where a reservation that decremented the snapshot
    /// would have to remember to put the units back and would lose them whenever it did not.
    ///
    /// <para>
    /// This is what closes the one-way hold. The SAP-side path already netted reservations off, via
    /// <c>GetReservedQuantityAsync</c>; the till read the raw snapshot and so would happily sell
    /// stock a rep had reserved minutes earlier for a customer standing in the shop.
    /// </para>
    ///
    /// <para>
    /// When a reservation is confirmed it stops being counted here, and the confirm posts an invoice
    /// that commits the units properly — so the hold becomes a decrement in one step rather than
    /// being counted twice or dropped between the two. A queued desktop invoice makes the same
    /// handover later and in one step: consolidation marks its queue entry Completed, which ends the
    /// hold, and takes the units off these rows in the same save. Which reservations are still
    /// holding is <see cref="ReservationHolds"/>'s to say, so this and the SAP-side reads cannot
    /// drift apart.
    /// </para>
    /// </remarks>
    private async Task<decimal> HeldByReservationsAsync(
        string itemCode,
        string warehouseCode,
        CancellationToken cancellationToken)
    {
        return await ReservationHolds.LiveLinesAsOf(context, DateTime.UtcNow)
            .Where(line => line.ItemCode == itemCode && line.WarehouseCode == warehouseCode)
            .SumAsync(line => line.ReservedQuantity, cancellationToken);
    }

    private IQueryable<DailyStockSnapshotItemEntity> RowsFor(string itemCode, string warehouseCode, DateTime snapshotDay)
    {
        return context.DailyStockSnapshotItems
            .Where(row => row.Snapshot.SnapshotDate == snapshotDay
                       && row.ItemCode == itemCode
                       && row.WarehouseCode == warehouseCode);
    }

    /// <summary>
    /// The day of the finished snapshot this warehouse is selling from, or null when it has none.
    /// </summary>
    /// <remarks>
    /// Usually <see cref="CurrentLedgerDay"/>. For a shop whose snapshot for today is still being
    /// fetched it is yesterday's — see <see cref="StockSnapshotInForce"/>. Resolved per warehouse and
    /// per call, never cached: a scope that straddles the moment today's snapshot finishes has to move
    /// today's rows from then on, not the ones it saw first.
    /// </remarks>
    private async Task<DateTime?> TrackedDayAsync(string warehouseCode, CancellationToken cancellationToken)
    {
        var inForce = await StockSnapshotInForce.ResolveAsync(
            context, warehouseCode, dailyStock.Value, cancellationToken);

        return inForce.IsComplete ? inForce.Day : null;
    }

    /// <summary>
    /// Drops what the failed attempt read and wrote, so the retry starts from the database rather
    /// than from its own stale copy.
    /// </summary>
    /// <remarks>
    /// The journal rows have to go with the snapshot rows. They were added for a save that did not
    /// happen, and leaving them tracked would have the retry insert a movement for each attempt —
    /// the journal claiming a document moved the ledger twice when it moved it once, which is
    /// exactly the reading this table exists to make trustworthy.
    /// </remarks>
    private void Detach()
    {
        foreach (var entry in context.ChangeTracker.Entries<DailyStockSnapshotItemEntity>())
        {
            entry.State = EntityState.Detached;
        }

        foreach (var entry in context.ChangeTracker.Entries<StockMovementEntity>())
        {
            entry.State = EntityState.Detached;
        }
    }
}
