using Microsoft.EntityFrameworkCore;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

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

        return finished.Contains(previous)
            ? new StockSnapshotInForce(previous, scheduled, true)
            : new StockSnapshotInForce(scheduled, scheduled, false);
    }
}
