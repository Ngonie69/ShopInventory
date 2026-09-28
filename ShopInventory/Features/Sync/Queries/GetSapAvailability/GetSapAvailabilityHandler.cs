using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.Sync.Queries.GetSapAvailability;

/// <summary>
/// What the Web banner shows: SAP is down and sales keep being recorded, or SAP is back and the sales
/// from the outage are still posting.
/// </summary>
/// <remarks>
/// <para>
/// Every staff page polls this, so the ordinary answer is cheap: the open outage comes from
/// <see cref="SapAvailability"/>'s in-memory copy, and with none open one indexed read looks for an
/// outage that ended within <see cref="RecoveryNotice"/>. Sales are counted only when there is an
/// outage to report on.
/// </para>
/// <para>
/// "Not in SAP yet" is a sale with no invoice of its own (<c>SapDocEntry</c>) and no end-of-day
/// invoice (<c>ConsolidationId</c>). Not the consolidation status: an online van sale is marked
/// Consolidated the moment it is written, before SAP has anything.
/// </para>
/// </remarks>
public sealed class GetSapAvailabilityHandler(
    ApplicationDbContext db,
    SapAvailability availability)
    : IRequestHandler<GetSapAvailabilityQuery, ErrorOr<SapAvailabilityResult>>
{
    /// <summary>How long after an outage ends the banner still says SAP is back and catching up.</summary>
    public static readonly TimeSpan RecoveryNotice = TimeSpan.FromMinutes(30);

    public async Task<ErrorOr<SapAvailabilityResult>> Handle(
        GetSapAvailabilityQuery query,
        CancellationToken cancellationToken)
    {
        var current = availability.Current;

        if (current is { InOutage: true, SinceUtc: { } since })
        {
            return new SapAvailabilityResult(
                true, current.Cause, since, null, await SalesAwaitingSapAsync(since, cancellationToken));
        }

        var endedAfter = DateTime.UtcNow - RecoveryNotice;
        var recent = await db.SapOutages
            .AsNoTracking()
            .Where(outage => outage.EndedAtUtc != null && outage.EndedAtUtc >= endedAfter)
            .OrderByDescending(outage => outage.EndedAtUtc)
            .Select(outage => new { outage.Cause, outage.StartedAtUtc, outage.EndedAtUtc })
            .FirstOrDefaultAsync(cancellationToken);

        if (recent is null)
        {
            return SapAvailabilityResult.Available;
        }

        return new SapAvailabilityResult(
            false,
            recent.Cause,
            recent.StartedAtUtc,
            recent.EndedAtUtc,
            await SalesAwaitingSapAsync(recent.StartedAtUtc, cancellationToken));
    }

    private Task<int> SalesAwaitingSapAsync(DateTime sinceUtc, CancellationToken cancellationToken) =>
        db.DesktopSales
            .AsNoTracking()
            .CountAsync(sale => sale.CreatedAt >= sinceUtc
                             && sale.SapDocEntry == null
                             && sale.ConsolidationId == null
                             && sale.ConsolidationStatus != DesktopSaleConsolidationStatus.Excluded,
                cancellationToken);
}
