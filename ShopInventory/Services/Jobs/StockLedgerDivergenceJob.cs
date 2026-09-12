using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Quartz;
using ShopInventory.Common.Stock;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

/// <summary>
/// Asks, every hour, whether the stock ledger still agrees with SAP — and, where it is allowed to,
/// puts it back in step.
/// </summary>
/// <remarks>
/// The ledger is the morning snapshot less everything this system has promised since, so it is only
/// right about stock this system moved. A goods issue done in the SAP client, a manual adjustment, an
/// invoice raised outside this application — none of it reaches the ledger, and the gap grows
/// silently until the next morning's fetch papers over it. Nothing else would ever notice.
///
/// <para><b>It used to only report, and reporting was not enough.</b> The argument for reporting
/// alone was that writing SAP's figure back would hide the one thing worth knowing: that something is
/// moving stock this system cannot see. That holds for the record and no longer holds for the ledger.
/// On 2026-09-11 the job compared 93 items that had moved and recorded 89 divergences — not a signal
/// with a trend to watch, a ledger that was wrong about nearly everything it had touched. Both
/// directions were live at shop tills that day: <c>ICS025/026/027</c> in KEFGRS sat at zero against
/// 17–20 in SAP, so the till refused sales the stock was there for, while <c>VHU002</c> in KEFBYC
/// stood 154 units above SAP, which is the same fault pointed at the customer. The divergence rows
/// are still written either way; what changes is that the ledger does not stay wrong for the rest of
/// the trading day.</para>
///
/// <para><b>SAP is not simply copied over.</b> A till sale commits to the ledger when the goods leave
/// the counter and does not reach SAP until it is posted, so for that window SAP is the figure that
/// is behind. Overwriting with it would hand back stock that has already left the shop — a
/// same-day oversell, manufactured by the thing meant to prevent one. The target is therefore SAP's
/// issuable quantity <i>less</i> what this system has committed that SAP has not seen yet, which is
/// the unposted till sales and nothing else: web invoices post within seconds of committing and
/// release their claim if they do not, and every other writer takes from the ledger only once the
/// document already exists in SAP.</para>
///
/// <para><b>Corrections are bounded and never destructive.</b> Stock is only ever removed by drawing
/// existing rows down in expiry order, and only ever added against batch rows SAP itself reports, up
/// to the quantity SAP itself gives. No row is deleted and no batch is invented, so the worst a bad
/// SAP read can do is leave the ledger where it already was. Vans are excluded — see
/// <see cref="DailyStockSettings.ReconcileWarehouses"/> — because a van's row is deliberately its
/// morning load rather than a live figure.</para>
///
/// <para><b>Only what moved.</b> Comparing every item in every monitored warehouse would be the
/// whole-warehouse scan this codebase has repeatedly optimised away: there are six process-wide SAP
/// slots and a stock read is what fills them, so an hourly full sweep would starve the interactive
/// users it exists to protect. Divergence can only appear where a quantity changed, so the job asks
/// SAP about the rows the day has touched and no others. On a quiet warehouse that is nothing at
/// all. The cost of that restraint is real and worth naming: an item that had no row this morning and
/// arrived during the day is invisible here, because there is nothing of it to have moved. Transfers
/// bring their own rows in through the listener; a goods receipt booked straight into SAP waits for
/// the next morning.</para>
/// </remarks>
[DisallowConcurrentExecution]
public sealed class StockLedgerDivergenceJob(
    IServiceProvider serviceProvider,
    IOptions<DailyStockSettings> dailyStock,
    ILogger<StockLedgerDivergenceJob> logger) : IJob
{
    /// <summary>
    /// Below this, a difference is rounding rather than a movement. Quantities are stored to six
    /// decimal places and weighed goods divide unevenly.
    /// </summary>
    private const decimal Tolerance = 0.0001m;

    /// <summary>
    /// Most rows checked in one pass, so a bad day cannot turn this into the sweep it avoids.
    /// </summary>
    private const int MaxRowsPerRun = 500;

    public async Task Execute(IJobExecutionContext context)
    {
        using var scope = serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var sapClient = scope.ServiceProvider.GetRequiredService<ISAPServiceLayerClient>();
        var ledger = scope.ServiceProvider.GetRequiredService<IStockLedger>();

        var settings = dailyStock.Value;
        var ledgerDay = ledger.CurrentLedgerDay;
        var recorded = 0;
        var compared = 0;
        var corrected = 0;

        var reconcilable = settings.ReconcileLedgerAgainstSap
            ? new HashSet<string>(settings.ReconcileWarehouses, StringComparer.OrdinalIgnoreCase)
            : [];

        if (settings.ReconcileLedgerAgainstSap && reconcilable.Count == 0)
        {
            // Enabled and pointed at nothing is not a safe default, it is a setting that looks on and
            // does nothing. Said once per run rather than left to be inferred from a zero.
            logger.LogWarning(
                "Stock ledger reconciliation is enabled but DailyStock:ReconcileWarehouses is empty, "
                + "so divergences will be recorded and none of them corrected");
        }

        foreach (var warehouseCode in settings.MonitoredWarehouses)
        {
            if (context.CancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                var moved = await MovedTodayAsync(db, ledgerDay, warehouseCode, context.CancellationToken);
                if (moved.Count == 0)
                {
                    continue;
                }

                var sapStock = await sapClient.GetStockQuantitiesForItemsInWarehouseAsync(
                    warehouseCode,
                    moved.Keys,
                    context.CancellationToken);

                var sapByItem = sapStock
                    .Where(stock => !string.IsNullOrWhiteSpace(stock.ItemCode))
                    .ToDictionary(stock => stock.ItemCode!, stock => stock, StringComparer.OrdinalIgnoreCase);

                var mayCorrect = reconcilable.Contains(warehouseCode);

                // What this system has promised that SAP has not seen. Read once per warehouse rather
                // than per item: it is one grouped query either way, and it must be the same figure
                // for every item in the pass or two items could be reconciled against different
                // moments.
                var outstanding = mayCorrect
                    ? await OutstandingTillSalesAsync(
                        db, ledgerDay, warehouseCode, settings.StockFetchTimeCAT, context.CancellationToken)
                    : [];

                var targets = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

                foreach (var (itemCode, ledgerQuantity) in moved)
                {
                    if (!sapByItem.TryGetValue(itemCode, out var stock))
                    {
                        // SAP does not carry the item in this warehouse at all. That is a divergence
                        // in its own right, but it is also what a decommissioned item looks like, so
                        // it is left to the morning fetch rather than reported every hour.
                        continue;
                    }

                    compared++;

                    var difference = ledgerQuantity - stock.Issuable;
                    if (Math.Abs(difference) <= Tolerance)
                    {
                        continue;
                    }

                    db.StockLedgerDivergences.Add(new StockLedgerDivergenceEntity
                    {
                        LedgerDay = ledgerDay,
                        WarehouseCode = warehouseCode,
                        ItemCode = itemCode,
                        LedgerQuantity = ledgerQuantity,
                        SapIssuableQuantity = stock.Issuable,
                        Difference = difference
                    });

                    recorded++;

                    logger.LogWarning(
                        "Stock ledger and SAP disagree on {ItemCode} in {WarehouseCode}: ledger {Ledger}, "
                        + "SAP {Sap}, difference {Difference}. {Direction}",
                        itemCode, warehouseCode, ledgerQuantity, stock.Issuable, difference,
                        difference > 0
                            ? "The ledger is promising stock SAP does not have."
                            : "The ledger is refusing sales SAP could cover.");

                    if (!mayCorrect)
                    {
                        continue;
                    }

                    // SAP's figure, less what SAP has not been told about yet. Floored at zero: a
                    // warehouse already negative in SAP has nothing left to promise, and a negative
                    // target would read as stock owed rather than stock absent.
                    var target = Math.Max(
                        0m,
                        stock.Issuable - outstanding.GetValueOrDefault(itemCode));

                    if (Math.Abs(target - ledgerQuantity) > Tolerance)
                    {
                        targets[itemCode] = target;
                    }
                }

                if (targets.Count > 0)
                {
                    corrected += await ApplyTargetsAsync(
                        db, sapClient, ledgerDay, warehouseCode, targets, context.CancellationToken);
                }
            }
            catch (Exception ex)
            {
                // One warehouse failing must not stop the rest. This job reads SAP, and SAP being
                // unavailable is exactly the condition under which the other guards are already
                // degrading — it is not worth escalating twice.
                logger.LogWarning(ex,
                    "Could not compare the stock ledger against SAP for warehouse {WarehouseCode}",
                    warehouseCode);

                // Whatever failed, this warehouse may have left half-applied corrections tracked.
                // They must not ride along on the next warehouse's save.
                DetachSnapshotRows(db);
            }
        }

        if (recorded > 0)
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }

        var discovered = 0;

        if (settings.DiscoverNewStockArrivals && reconcilable.Count > 0)
        {
            foreach (var warehouseCode in DiscoverySlots(
                         settings.ReconcileWarehouses,
                         settings.WarehousesPerDiscoveryPass,
                         DateTime.UtcNow))
            {
                if (context.CancellationToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    discovered += await DiscoverArrivalsAsync(
                        db, sapClient, ledgerDay, warehouseCode, settings, context.CancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Could not search {WarehouseCode} for stock that arrived without a snapshot row",
                        warehouseCode);
                    DetachSnapshotRows(db);
                }
            }
        }

        logger.LogInformation(
            "Stock ledger comparison for {LedgerDay:yyyy-MM-dd}: {Compared} item(s) checked, "
            + "{Recorded} divergence(s) recorded, {Corrected} corrected, {Discovered} arrival(s) added",
            ledgerDay, compared, recorded, corrected, discovered);
    }

    /// <summary>
    /// The warehouses this pass searches for arrivals, rotating so every warehouse comes round.
    /// </summary>
    /// <remarks>
    /// Derived from the clock rather than held anywhere. A stored cursor is one more thing to get
    /// stuck — a restart, a failed write or a cleared job data map and the rotation sits on the same
    /// warehouse for ever, which is indistinguishable from working. The hour is already a shared,
    /// monotonic counter that every node agrees on, and this job runs hourly, so consecutive passes
    /// take consecutive slices with no state at all.
    /// </remarks>
    internal static IEnumerable<string> DiscoverySlots(
        IReadOnlyList<string> warehouses,
        int perPass,
        DateTime utcNow)
    {
        if (warehouses.Count == 0 || perPass <= 0)
        {
            yield break;
        }

        var take = Math.Min(perPass, warehouses.Count);
        var cycle = utcNow.Ticks / TimeSpan.TicksPerHour;
        var start = (int)(cycle * take % warehouses.Count);

        for (var offset = 0; offset < take; offset++)
        {
            yield return warehouses[(start + offset) % warehouses.Count];
        }
    }

    /// <summary>
    /// Adds rows for stock the warehouse is holding that today's snapshot has no row for at all.
    /// </summary>
    /// <remarks>
    /// <para><b>The gap this closes.</b> Everything else in this job reasons about rows that moved, so
    /// an item the warehouse held none of at 07:00 cannot be seen by any of it. Until the next morning
    /// the till has no row to sell from, which a shop experiences as the item simply not being in the
    /// product list while it sits on the shelf.</para>
    ///
    /// <para><b>Composed the way the morning fetch composes it.</b> The same two reads and the same
    /// <see cref="SnapshotComposer"/>, so a row added here is indistinguishable from one the 07:00 job
    /// would have written — batch rows for a batch-managed item, a single batchless row for one SAP
    /// does not batch, and commitments already netted off. Deriving the shape again here is how the
    /// two would drift, and a row of the wrong shape is worse than no row: SAP refuses a batch-managed
    /// line that names no batch, and takes the whole document with it.</para>
    ///
    /// <para><b>Only adds.</b> An item that already has a row, at whatever quantity, belongs to the
    /// comparison above and is left alone here — otherwise one pass could correct the same item twice
    /// by two different routes.</para>
    /// </remarks>
    private async Task<int> DiscoverArrivalsAsync(
        ApplicationDbContext db,
        ISAPServiceLayerClient sapClient,
        DateTime ledgerDay,
        string warehouseCode,
        DailyStockSettings settings,
        CancellationToken cancellationToken)
    {
        var snapshot = await db.DailyStockSnapshots
            .Where(header => header.SnapshotDate == ledgerDay
                          && header.WarehouseCode == warehouseCode
                          && header.Status == StockSnapshotStatus.Complete)
            .Select(header => new { header.Id })
            .FirstOrDefaultAsync(cancellationToken);

        if (snapshot is null)
        {
            // No snapshot to add to. The morning fetch has not run or did not finish, and inventing
            // rows under a header that does not exist would put stock on a till the ledger reports as
            // untracked.
            return 0;
        }

        var known = (await db.DailyStockSnapshotItems
                .Where(row => row.Snapshot.SnapshotDate == ledgerDay && row.WarehouseCode == warehouseCode)
                .Select(row => row.ItemCode)
                .Distinct()
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var batches = await sapClient.GetAllBatchNumbersInWarehouseAsync(warehouseCode, cancellationToken);
        var warehouseStock = await sapClient.GetStockQuantitiesInWarehouseAsync(warehouseCode, cancellationToken);

        var arrivals = SnapshotComposer.Compose(batches, warehouseStock)
            .Rows
            .Where(row => !known.Contains(row.ItemCode) && row.AvailableQuantity > 0)
            .GroupBy(row => row.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (arrivals.Count == 0)
        {
            return 0;
        }

        var outstanding = await OutstandingTillSalesAsync(
            db, ledgerDay, warehouseCode, settings.StockFetchTimeCAT, cancellationToken);

        var added = 0;

        foreach (var arrival in arrivals)
        {
            // A newly arrived item can already have been sold, if it reached the shelf before it
            // reached here. Netting the unposted sales off is the same arithmetic the comparison
            // does, and for the same reason: SAP has not been told about them yet.
            var sold = outstanding.GetValueOrDefault(arrival.Key);
            var rows = new List<DailyStockSnapshotItemEntity>();

            foreach (var composed in arrival.OrderBy(row => row.ExpiryDate ?? DateTime.MaxValue))
            {
                var row = NewRow(
                    db, snapshot.Id, warehouseCode, arrival.Key, composed.BatchNumber, composed.ExpiryDate);

                row.ItemDescription = composed.ItemDescription;

                // OriginalQuantity stays at zero: the warehouse held none of this at the morning
                // fetch, which is what that field means and what keeps the row visible to the
                // comparison from now on.
                row.AvailableQuantity = composed.AvailableQuantity;
                rows.Add(row);
            }

            if (sold > 0)
            {
                DrawDown(rows, sold);
            }

            if (rows.Sum(row => row.AvailableQuantity) <= 0)
            {
                continue;
            }

            added++;

            logger.LogInformation(
                "Stock ledger added {ItemCode} to {WarehouseCode} with {Quantity}: the warehouse is "
                + "holding it and today's snapshot had no row for it",
                arrival.Key, warehouseCode, rows.Sum(row => row.AvailableQuantity));
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        else
        {
            // Rows were built and then netted away to nothing. They must not be left tracked, or the
            // next warehouse's save writes them.
            DetachSnapshotRows(db);
        }

        return added;
    }

    /// <summary>
    /// Moves each named item's rows to its target, and reports how many items were moved.
    /// </summary>
    /// <remarks>
    /// Saved per warehouse rather than per item so that a warehouse's corrections land together, and
    /// separately from the divergence rows so that a correction that cannot be written still leaves
    /// the record of why it was wanted.
    /// </remarks>
    private async Task<int> ApplyTargetsAsync(
        ApplicationDbContext db,
        ISAPServiceLayerClient sapClient,
        DateTime ledgerDay,
        string warehouseCode,
        Dictionary<string, decimal> targets,
        CancellationToken cancellationToken)
    {
        var rowsByItem = (await db.DailyStockSnapshotItems
                .Where(row => row.Snapshot.SnapshotDate == ledgerDay
                           && row.WarehouseCode == warehouseCode
                           && targets.Keys.Contains(row.ItemCode))
                .ToListAsync(cancellationToken))
            .GroupBy(row => row.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        // Which items need stock put back, and so need real batch numbers from SAP. An item that only
        // needs drawing down is answered from rows already held, and asks SAP nothing further.
        var needGrowth = targets
            .Where(target => target.Value
                           - (rowsByItem.GetValueOrDefault(target.Key)?.Sum(row => row.AvailableQuantity) ?? 0m)
                           > Tolerance)
            .Select(target => target.Key)
            .ToList();

        var batchesByItem = needGrowth.Count > 0
            ? await BatchesAsync(sapClient, warehouseCode, needGrowth, cancellationToken)
            : [];

        var snapshotId = await db.DailyStockSnapshots
            .Where(snapshot => snapshot.SnapshotDate == ledgerDay && snapshot.WarehouseCode == warehouseCode)
            .Select(snapshot => (int?)snapshot.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (snapshotId is null)
        {
            // Nothing to correct into. Reported rather than passed over: the comparison found rows
            // for this warehouse a moment ago, so a missing header means something removed it
            // underneath us.
            logger.LogWarning(
                "Stock ledger has no snapshot header for {WarehouseCode} on {LedgerDay:yyyy-MM-dd}, "
                + "so {Count} correction(s) were not applied",
                warehouseCode, ledgerDay, targets.Count);
            return 0;
        }

        var moved = 0;

        foreach (var (itemCode, target) in targets)
        {
            var rows = rowsByItem.GetValueOrDefault(itemCode) ?? [];
            var current = rows.Sum(row => row.AvailableQuantity);
            var delta = target - current;

            if (Math.Abs(delta) <= Tolerance)
            {
                continue;
            }

            var applied = delta < 0
                ? DrawDown(rows, -delta)
                : PutBack(db, snapshotId.Value, warehouseCode, itemCode, rows,
                    delta, batchesByItem.GetValueOrDefault(itemCode) ?? []);

            if (Math.Abs(applied) <= Tolerance)
            {
                continue;
            }

            moved++;

            logger.LogInformation(
                "Stock ledger corrected {ItemCode} in {WarehouseCode} from {From} to {To} "
                + "(SAP less unposted till sales)",
                itemCode, warehouseCode, current, current + applied);
        }

        if (moved > 0)
        {
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                // A till sold from one of these rows between the read and the write. Its figure is
                // the newer one and the correction is an hour from being offered again, so this
                // stands down rather than retrying over the top of a real sale.
                logger.LogWarning(ex,
                    "Stock ledger corrections for {WarehouseCode} were overtaken by a concurrent sale "
                    + "and were not applied; the next pass will re-offer them",
                    warehouseCode);

                // The refused rows are still sitting in the tracker, and every later save on this
                // context — the divergence rows at the end of the run, and every warehouse after this
                // one — would carry them along and fail on them again. Standing down means letting
                // go of them, or the report is lost with the correction.
                DetachSnapshotRows(db);
                return 0;
            }
        }

        return moved;
    }

    /// <summary>
    /// Takes <paramref name="quantity"/> off the item's rows, soonest to expire first — the order the
    /// till deducts in and the order SAP's FEFO allocation picks.
    /// </summary>
    /// <returns>The signed change actually made, which is bounded by what the rows held.</returns>
    private static decimal DrawDown(List<DailyStockSnapshotItemEntity> rows, decimal quantity)
    {
        var remaining = quantity;

        foreach (var row in rows.OrderBy(row => row.ExpiryDate ?? DateTime.MaxValue))
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

        return -(quantity - remaining);
    }

    /// <summary>
    /// Puts <paramref name="quantity"/> back, against batches SAP actually reports.
    /// </summary>
    /// <remarks>
    /// Never above the quantity SAP gives for a batch, and never into a batch SAP did not name. That
    /// bound is what makes a bad or partial SAP read harmless: the worst it can do is put back less
    /// than it should, which leaves the ledger where it already was rather than promising stock on a
    /// batch that does not exist.
    ///
    /// <para>
    /// An item SAP reports no batches for is not an error — most of the item master is not batch
    /// managed, and the morning fetch writes those as a single row with no batch number. The same
    /// shape is written here. What must not happen is the reverse: inventing a batchless row for an
    /// item SAP <i>does</i> batch, because SAP refuses a batch-managed line that names no batch and
    /// the refusal takes the whole document with it.
    /// </para>
    /// </remarks>
    private static decimal PutBack(
        ApplicationDbContext db,
        int snapshotId,
        string warehouseCode,
        string itemCode,
        List<DailyStockSnapshotItemEntity> rows,
        decimal quantity,
        List<BatchNumber> batches)
    {
        var remaining = quantity;

        if (batches.Count == 0)
        {
            // Unbatched, so there is one place it can go.
            var row = rows.FirstOrDefault(row => row.BatchNumber == null);

            if (row is null)
            {
                if (rows.Count > 0 && rows.TrueForAll(existing => existing.BatchNumber != null))
                {
                    // Every row this item has carries a batch, so SAP batches it and simply answered
                    // with none — a degraded read, not an unbatched item. Adding a batchless row here
                    // would put stock on the till that no document could ever take off it.
                    return 0m;
                }

                row = NewRow(db, snapshotId, warehouseCode, itemCode, batch: null, expiry: null);
            }

            row.AvailableQuantity += remaining;
            return remaining;
        }

        foreach (var batch in batches.OrderBy(batch => ParseExpiry(batch.ExpiryDate) ?? DateTime.MaxValue))
        {
            if (remaining <= 0)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(batch.BatchNum) || batch.Quantity <= 0)
            {
                continue;
            }

            var row = rows.FirstOrDefault(row =>
                string.Equals(row.BatchNumber, batch.BatchNum, StringComparison.OrdinalIgnoreCase));

            if (row is null)
            {
                row = NewRow(
                    db, snapshotId, warehouseCode, itemCode,
                    batch.BatchNum, ParseExpiry(batch.ExpiryDate));
                rows.Add(row);
            }

            // Never above what SAP says this batch holds.
            var headroom = batch.Quantity - row.AvailableQuantity;
            if (headroom <= 0)
            {
                continue;
            }

            var added = Math.Min(headroom, remaining);
            row.AvailableQuantity += added;
            remaining -= added;
        }

        return quantity - remaining;
    }

    /// <summary>
    /// A row for stock that was not in the warehouse this morning.
    /// </summary>
    /// <remarks>
    /// <see cref="DailyStockSnapshotItemEntity.OriginalQuantity"/> stays at zero, and that is not an
    /// oversight. It means "what this warehouse held at the morning fetch", it is what the van
    /// reconciliation reads as the day's load, and it is what <c>MovedTodayAsync</c> compares against
    /// to find rows worth asking SAP about. Writing today's figure into it would make the row look
    /// untouched and hide it from the next pass.
    /// </remarks>
    private static DailyStockSnapshotItemEntity NewRow(
        ApplicationDbContext db,
        int snapshotId,
        string warehouseCode,
        string itemCode,
        string? batch,
        DateTime? expiry)
    {
        var row = new DailyStockSnapshotItemEntity
        {
            SnapshotId = snapshotId,
            ItemCode = itemCode,
            WarehouseCode = warehouseCode,
            BatchNumber = batch,
            OriginalQuantity = 0m,
            AvailableQuantity = 0m,
            ExpiryDate = expiry
        };

        db.DailyStockSnapshotItems.Add(row);
        return row;
    }

    /// <summary>
    /// SAP's batches for the named items, indexed by item. Failure is answered with nothing rather
    /// than thrown, because an item that cannot be grown is worth less than a warehouse that stops
    /// being reconciled.
    /// </summary>
    private async Task<Dictionary<string, List<BatchNumber>>> BatchesAsync(
        ISAPServiceLayerClient sapClient,
        string warehouseCode,
        List<string> itemCodes,
        CancellationToken cancellationToken)
    {
        try
        {
            var batches = await sapClient.GetBatchNumbersForItemsInWarehouseAsync(
                itemCodes, warehouseCode, allowCachedSnapshot: false, cancellationToken);

            return batches
                .Where(batch => !string.IsNullOrWhiteSpace(batch.ItemCode))
                .GroupBy(batch => batch.ItemCode!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not read batches for {Count} item(s) in {WarehouseCode}; those items will not "
                + "have stock put back on this pass",
                itemCodes.Count, warehouseCode);
            return [];
        }
    }

    private static DateTime? ParseExpiry(string? value) =>
        DateTime.TryParse(value, out var parsed) ? parsed : null;

    /// <summary>
    /// Lets go of every snapshot row this pass had changed, leaving the divergence rows tracked.
    /// </summary>
    /// <remarks>
    /// The two things this job writes have to be able to fail apart. The divergence record is the
    /// report, and it is worth keeping even — especially — on a pass where the correction could not
    /// be applied. Sharing one context means a row EF cannot save is retried on every later
    /// SaveChanges, so without this a single refused correction would take the whole run's reporting
    /// down with it, one warehouse at a time.
    /// </remarks>
    private static void DetachSnapshotRows(ApplicationDbContext db)
    {
        foreach (var entry in db.ChangeTracker.Entries<DailyStockSnapshotItemEntity>().ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    /// <summary>
    /// Till sales captured on this ledger day that SAP has not been given yet, summed per item.
    /// </summary>
    /// <remarks>
    /// This is the whole of the correction that SAP cannot supply. A till sale commits to the ledger
    /// the moment the goods leave the counter and reaches SAP later — within the minute if the posting
    /// service is keeping up, at end of day if it is not — so between those two moments SAP's figure
    /// is higher than the truth by exactly this.
    ///
    /// <para>
    /// <c>Consolidated</c> is the one status that means SAP has the sale, for both routes: the
    /// desktop app's sales arrive inside a consolidated invoice and van sales post one-to-one.
    /// Everything else counts as outstanding, deliberately including a sale whose post went out and
    /// whose reply was lost. Counting that one twice makes the ledger a unit short, which refuses a
    /// sale; not counting it makes the ledger a unit long, which sells stock that is gone. Only one of
    /// those two mistakes reaches a customer.
    /// </para>
    ///
    /// <para>
    /// Bounded by when the sale was captured rather than by its <c>DocDate</c>, which is an accounting
    /// date taken from the UTC day and so names the wrong ledger day for anything sold between
    /// midnight and the morning fetch. The ledger day runs from the fetch time in CAT, which is two
    /// hours ahead of UTC — hence the window below.
    /// </para>
    /// </remarks>
    private static async Task<Dictionary<string, decimal>> OutstandingTillSalesAsync(
        ApplicationDbContext db,
        DateTime ledgerDay,
        string warehouseCode,
        string? fetchTimeCat,
        CancellationToken cancellationToken)
    {
        var fetchTime = StockLedgerDay.ParseFetchTime(fetchTimeCat);
        var from = ledgerDay.Add(fetchTime).AddHours(-2);
        var to = from.AddDays(1);

        var totals = await db.DesktopSaleLines
            .Where(line => line.WarehouseCode == warehouseCode
                        && line.Sale.ConsolidationStatus != DesktopSaleConsolidationStatus.Consolidated
                        && line.Sale.CreatedAt >= from
                        && line.Sale.CreatedAt < to)
            .GroupBy(line => line.ItemCode)
            .Select(group => new { ItemCode = group.Key, Quantity = group.Sum(line => line.Quantity) })
            .ToListAsync(cancellationToken);

        return totals.ToDictionary(
            total => total.ItemCode,
            total => total.Quantity,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Today's ledger rows whose quantity has changed since the morning fetch, summed per item.
    /// </summary>
    /// <remarks>
    /// A row still at its original quantity has had nothing happen to it that this system knows
    /// about, and asking SAP about it would only find divergence caused elsewhere — which is real,
    /// but is what the morning fetch is for. Restricting the question to what moved is what keeps
    /// this off the SAP slot pool.
    /// </remarks>
    private static async Task<Dictionary<string, decimal>> MovedTodayAsync(
        ApplicationDbContext db,
        DateTime ledgerDay,
        string warehouseCode,
        CancellationToken cancellationToken)
    {
        var touchedItems = await db.DailyStockSnapshotItems
            .Where(row => row.Snapshot.SnapshotDate == ledgerDay
                       && row.WarehouseCode == warehouseCode
                       && row.AvailableQuantity != row.OriginalQuantity)
            .Select(row => row.ItemCode)
            .Distinct()
            .Take(MaxRowsPerRun)
            .ToListAsync(cancellationToken);

        if (touchedItems.Count == 0)
        {
            return [];
        }

        // The comparison is per item, not per batch: SAP's issuable figure is an item total, and the
        // ledger's batch rows have to be added up to face it.
        var totals = await db.DailyStockSnapshotItems
            .Where(row => row.Snapshot.SnapshotDate == ledgerDay
                       && row.WarehouseCode == warehouseCode
                       && touchedItems.Contains(row.ItemCode))
            .GroupBy(row => row.ItemCode)
            .Select(group => new { ItemCode = group.Key, Available = group.Sum(row => row.AvailableQuantity) })
            .ToListAsync(cancellationToken);

        return totals.ToDictionary(
            total => total.ItemCode,
            total => total.Available,
            StringComparer.OrdinalIgnoreCase);
    }
}
