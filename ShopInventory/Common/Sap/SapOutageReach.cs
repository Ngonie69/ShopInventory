using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Services;

namespace ShopInventory.Common.Sap;

/// <summary>
/// How far back a posting pass has to look once SAP outages are taken into account.
/// </summary>
/// <remarks>
/// <para>
/// Each pass that posts to SAP looks back a fixed number of days — three for till sales, seven for van
/// sales — and anything older is never offered to SAP again. The windows are sized for ordinary delays
/// and cost nothing on a normal day, because everything older has already posted. An outage breaks
/// that: a sale made on the first day of a four-day outage is five days old when SAP comes back, and
/// the three-day till pass would never see it. It stays fiscalised, unposted and netted off the shelf
/// until the ledger's own thirty-day window hands its units back.
/// </para>
/// <para>
/// So a pass's cutoff moves back to the day before any outage that was still going on at the cutoff,
/// or has not ended. Once an outage has been over for longer than a pass's own window, it no longer
/// reaches: by then the backlog has had the whole window to drain. The day before, rather than the day
/// it started, because a sale made late that evening could not post either.
/// </para>
/// <para>
/// Every place that decides a sale is out of reach has to ask this too, or it reports as stranded a
/// sale the pass is about to post.
/// </para>
/// </remarks>
public static class SapOutageReach
{
    /// <param name="db">Where the outages are recorded.</param>
    /// <param name="cutoffDate">The pass's own first day, as it compares it: a trading day for sales.</param>
    /// <param name="maxExtensionDays">The furthest back, from today, an outage may take the cutoff.</param>
    /// <param name="nowUtc">The present, which <paramref name="maxExtensionDays"/> counts back from.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<DateTime> ExtendAsync(
        ApplicationDbContext db,
        DateTime cutoffDate,
        int maxExtensionDays,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var cutoff = cutoffDate.Date;
        var cutoffUtc = DateTime.SpecifyKind(cutoff, DateTimeKind.Utc);

        var earliestStart = await db.SapOutages
            .AsNoTracking()
            .Where(outage => outage.EndedAtUtc == null || outage.EndedAtUtc >= cutoffUtc)
            .OrderBy(outage => outage.StartedAtUtc)
            .Select(outage => (DateTime?)outage.StartedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        return Extend(cutoff, earliestStart, maxExtensionDays, nowUtc);
    }

    internal static DateTime Extend(
        DateTime cutoffDate,
        DateTime? earliestOutageStartUtc,
        int maxExtensionDays,
        DateTime nowUtc)
    {
        var cutoff = cutoffDate.Date;
        if (earliestOutageStartUtc is not { } startedUtc)
        {
            return cutoff;
        }

        var reach = AuditService.ToCAT(startedUtc).Date.AddDays(-1);
        var floor = AuditService.ToCAT(nowUtc).Date.AddDays(-Math.Max(0, maxExtensionDays));

        if (reach < floor)
        {
            reach = floor;
        }

        return reach < cutoff ? DateTime.SpecifyKind(reach, cutoffDate.Kind) : cutoff;
    }
}
