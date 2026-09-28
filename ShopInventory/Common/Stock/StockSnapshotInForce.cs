using Microsoft.EntityFrameworkCore;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Common.Stock;

/// <summary>
/// The snapshot a warehouse is actually selling from, which is not always the one scheduled for today.
/// </summary>
/// <param name="Day">The snapshot day whose rows to read and move.</param>
/// <param name="Scheduled">
/// The day <see cref="StockLedgerDay"/> says is in force. The journal and its duplicate check stay on
/// this day even while <paramref name="Day"/> is the one before — see <c>StockLedger</c>.
/// </param>
/// <param name="IsComplete">
/// Whether <paramref name="Day"/> has a finished snapshot. False means there is nothing to sell from.
/// </param>
/// <remarks>
/// <para><b>Why.</b> <see cref="StockLedgerDay"/> rolls at the fetch time, 07:00 CAT, whether or not the
/// fetch has finished. Until it does, today's snapshot is Pending and every reader of it refused: the
/// till's catalogue answered "still being loaded" and every sale was refused as untracked. On a normal
/// morning that is a minute. On 2026-09-17 the API restarted at 07:01, part-way through the fetch, and
/// left KEFSHOP's snapshot Pending with nothing to finish it. The till was refused from 07:04, and a
/// fetch started by hand at 08:06 had still not finished at 08:13.</para>
///
/// <para><b>The rule.</b> A shop warehouse whose snapshot for today is not finished keeps selling from
/// yesterday's finished one — the rows the tills were already moving, which are still a live figure.
/// Only one day back: a warehouse that has not had a finished snapshot for two mornings has a problem a
/// person needs to see, and serving figures that old would hide it.</para>
///
/// <para><b>Unless SAP was down.</b> A recorded SAP outage is the one explanation for missed mornings
/// that needs no person: nothing could have fetched them. So a shop may reach further back, to its
/// last finished snapshot within <see cref="DailyStockSettings.OutageCarryOverDays"/>, when one outage
/// was already under way at the first missed morning's fetch and was still going on at this morning's.
/// The first condition matters: a shop's failed fetch is retried all day, so a day that missed its
/// snapshot while SAP was up for most of it has some other cause. The rows it sells from are the same rows the tills have been moving all along, so the figure
/// stays live; and the fetch that finally succeeds takes off every till sale SAP has not had, however
/// many days old, so nothing sold during the outage goes back on the shelf.</para>
///
/// <para><b>Shops only.</b> The warehouses in <see cref="DailyStockSettings.ReconcileWarehouses"/>. A
/// van's row is its morning load, which the van reconciliation is computed from, and carrying one van
/// day into the next would change that number. Vans behave as they did.</para>
///
/// <para><b>What makes it safe.</b> Whatever moves yesterday's rows after 07:00 is journalled under
/// today's ledger day, and the fetch that finishes today's snapshot takes those movements off the new
/// rows — see <c>FetchDailyStockHandler</c>. Without that, every unit sold while the fetch ran would be
/// back on the shelf the moment it finished.</para>
/// </remarks>
public sealed record StockSnapshotInForce(DateTime Day, DateTime Scheduled, bool IsComplete)
{
    /// <summary>True while yesterday's snapshot stands in for today's.</summary>
    public bool IsCarriedOver => Day != Scheduled;

    public static async Task<StockSnapshotInForce> ResolveAsync(
        ApplicationDbContext db,
        string warehouseCode,
        DailyStockSettings settings,
        CancellationToken cancellationToken)
    {
        var scheduled = StockLedgerDay.Today(settings.StockFetchTimeCAT);
        var previous = scheduled.AddDays(-1);
        var mayCarry = settings.ReconcileWarehouses.Contains(warehouseCode, StringComparer.OrdinalIgnoreCase);

        var finished = await db.DailyStockSnapshots
            .AsNoTracking()
            .Where(snapshot => snapshot.WarehouseCode == warehouseCode
                            && snapshot.Status == StockSnapshotStatus.Complete
                            && (snapshot.SnapshotDate == scheduled
                                || (mayCarry && snapshot.SnapshotDate == previous)))
            .Select(snapshot => snapshot.SnapshotDate)
            .ToListAsync(cancellationToken);

        if (finished.Contains(scheduled))
        {
            return new StockSnapshotInForce(scheduled, scheduled, true);
        }

        if (finished.Contains(previous))
        {
            return new StockSnapshotInForce(previous, scheduled, true);
        }

        var outageDay = mayCarry
            ? await CarriedThroughOutageAsync(db, warehouseCode, scheduled, settings, cancellationToken)
            : null;

        return outageDay is { } day
            ? new StockSnapshotInForce(day, scheduled, true)
            : new StockSnapshotInForce(scheduled, scheduled, false);
    }

    /// <summary>
    /// The last finished snapshot older than yesterday that a SAP outage lets this shop sell from, or
    /// null when there is none or no outage explains the gap.
    /// </summary>
    private static async Task<DateTime?> CarriedThroughOutageAsync(
        ApplicationDbContext db,
        string warehouseCode,
        DateTime scheduled,
        DailyStockSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.OutageCarryOverDays <= 1)
        {
            return null;
        }

        var oldest = scheduled.AddDays(-settings.OutageCarryOverDays);
        var previous = scheduled.AddDays(-1);

        var last = await db.DailyStockSnapshots
            .AsNoTracking()
            .Where(snapshot => snapshot.WarehouseCode == warehouseCode
                            && snapshot.Status == StockSnapshotStatus.Complete
                            && snapshot.SnapshotDate >= oldest
                            && snapshot.SnapshotDate < previous)
            .OrderByDescending(snapshot => snapshot.SnapshotDate)
            .Select(snapshot => (DateTime?)snapshot.SnapshotDate)
            .FirstOrDefaultAsync(cancellationToken);

        if (last is not { } lastDay)
        {
            return null;
        }

        // The first missed morning is the day after the last finished snapshot. The outage has to have
        // been under way by that morning's fetch and to have still been going at this morning's. The
        // slack is for how an outage is dated: from the probe that first failed, which can land a little
        // after the fetch SAP had already refused.
        var fetchTime = StockLedgerDay.ParseFetchTime(settings.StockFetchTimeCAT);
        var firstMissedFetchUtc = FetchInstantUtc(lastDay.AddDays(1), fetchTime);
        var thisMorningUtc = FetchInstantUtc(scheduled, fetchTime);
        var startedByUtc = firstMissedFetchUtc.Add(OutageStartSlack);

        var explained = await db.SapOutages
            .AsNoTracking()
            .AnyAsync(outage => outage.StartedAtUtc <= startedByUtc
                             && (outage.EndedAtUtc == null || outage.EndedAtUtc >= thisMorningUtc),
                cancellationToken);

        return explained ? lastDay : null;
    }

    /// <summary>
    /// How long after a missed fetch an outage may be dated and still count as its cause.
    /// </summary>
    private static readonly TimeSpan OutageStartSlack = TimeSpan.FromHours(1);

    private static DateTime FetchInstantUtc(DateTime day, TimeSpan fetchTimeCat) =>
        DateTime.SpecifyKind(AuditService.FromCAT(day.Date.Add(fetchTimeCat)), DateTimeKind.Utc);
}
