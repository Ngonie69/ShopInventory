using Microsoft.EntityFrameworkCore;
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
    Task<StockLedgerOutcome> TryCommitAsync(
        IReadOnlyList<StockLedgerLine> lines,
        string reference,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts units back — a document that was refused after its claim was taken, or one that was
    /// reversed. Never fails on an untracked warehouse.
    /// </summary>
    Task ReleaseAsync(
        IReadOnlyList<StockLedgerLine> lines,
        string reference,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The day whose snapshot the ledger is reading. Exposed so callers that already resolve a date
    /// for themselves can be checked against it.
    /// </summary>
    DateTime CurrentLedgerDay { get; }
}

public sealed class StockLedger(
    ApplicationDbContext context,
    ILogger<StockLedger> logger) : IStockLedger
{
    private const int MaxConcurrencyRetries = 3;

    /// <summary>
    /// Deliberately identical to what every caller computed for itself before this existed:
    /// <c>DateTime.UtcNow.Date</c>.
    /// </summary>
    /// <remarks>
    /// It is wrong — the business runs in CAT, so a sale between midnight and 02:00 resolves to
    /// yesterday's snapshot — and it is left wrong here on purpose, so that introducing the ledger
    /// changes no dates. Correcting it is its own change, and this is the one place it now has to be
    /// corrected: the till handler, the snapshot job and the fetch handler all used to compute it
    /// separately.
    /// </remarks>
    public DateTime CurrentLedgerDay => DateTime.UtcNow.Date;

    public async Task<StockLedgerReading> ReadAsync(
        string itemCode,
        string warehouseCode,
        CancellationToken cancellationToken = default)
    {
        if (!await IsTrackedAsync(warehouseCode, cancellationToken))
        {
            return new StockLedgerReading(StockLedgerCoverage.NotTracked, 0m);
        }

        var available = await RowsFor(itemCode, warehouseCode)
            .SumAsync(row => row.AvailableQuantity, cancellationToken);

        return new StockLedgerReading(StockLedgerCoverage.Tracked, available);
    }

    public async Task<StockLedgerOutcome> TryCommitAsync(
        IReadOnlyList<StockLedgerLine> lines,
        string reference,
        CancellationToken cancellationToken = default)
    {
        var claims = Aggregate(lines);
        if (claims.Count == 0)
        {
            return StockLedgerOutcome.Success;
        }

        for (var attempt = 1; attempt <= MaxConcurrencyRetries; attempt++)
        {
            var trackedClaims = new List<(StockLedgerLine Claim, List<DailyStockSnapshotItemEntity> Rows)>();
            var shortfalls = new List<string>();
            var untracked = new List<string>();

            foreach (var claim in claims)
            {
                if (!await IsTrackedAsync(claim.WarehouseCode, cancellationToken))
                {
                    // Passed over, and reported. What it means is the caller's to decide — see
                    // StockLedgerOutcome.UntrackedWarehouses.
                    if (!untracked.Contains(claim.WarehouseCode, StringComparer.OrdinalIgnoreCase))
                    {
                        untracked.Add(claim.WarehouseCode);
                    }

                    continue;
                }

                var rows = await RowsFor(claim.ItemCode, claim.WarehouseCode)
                    .OrderBy(row => row.ExpiryDate)
                    .ToListAsync(cancellationToken);

                var available = rows.Sum(row => row.AvailableQuantity);
                if (available < claim.Quantity)
                {
                    shortfalls.Add(
                        $"{claim.ItemCode} in {claim.WarehouseCode}: {claim.Quantity} requested, {available} left to promise today");
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
                Take(rows, claim.Quantity);
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

    public async Task ReleaseAsync(
        IReadOnlyList<StockLedgerLine> lines,
        string reference,
        CancellationToken cancellationToken = default)
    {
        var claims = Aggregate(lines);

        foreach (var claim in claims)
        {
            if (!await IsTrackedAsync(claim.WarehouseCode, cancellationToken))
            {
                continue;
            }

            var rows = await RowsFor(claim.ItemCode, claim.WarehouseCode)
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
            rows[0].AvailableQuantity += claim.Quantity;
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Takes <paramref name="quantity"/> across the rows, soonest to expire first — the same order
    /// the till's own deduction used, and the order SAP's FEFO allocation picks.
    /// </summary>
    private static void Take(List<DailyStockSnapshotItemEntity> rows, decimal quantity)
    {
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

            var taken = Math.Min(row.AvailableQuantity, remaining);
            row.AvailableQuantity -= taken;
            remaining -= taken;
        }
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

    private IQueryable<DailyStockSnapshotItemEntity> RowsFor(string itemCode, string warehouseCode)
    {
        var day = CurrentLedgerDay;
        return context.DailyStockSnapshotItems
            .Where(row => row.Snapshot.SnapshotDate == day
                       && row.ItemCode == itemCode
                       && row.WarehouseCode == warehouseCode);
    }

    private async Task<bool> IsTrackedAsync(string warehouseCode, CancellationToken cancellationToken)
    {
        var day = CurrentLedgerDay;
        return await context.DailyStockSnapshots.AnyAsync(
            snapshot => snapshot.SnapshotDate == day
                     && snapshot.WarehouseCode == warehouseCode
                     && snapshot.Status == StockSnapshotStatus.Complete,
            cancellationToken);
    }

    private void Detach()
    {
        foreach (var entry in context.ChangeTracker.Entries<DailyStockSnapshotItemEntity>())
        {
            entry.State = EntityState.Detached;
        }
    }
}
