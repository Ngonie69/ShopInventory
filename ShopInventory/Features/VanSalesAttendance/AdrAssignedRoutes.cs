using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models;

namespace ShopInventory.Features.VanSalesAttendance;

/// <summary>
/// The route an ADR is assigned to, for the rep-days that have no round of their own.
///
/// A round's route is normally the snapshot the handset's Start Day writes onto
/// <see cref="Models.Entities.VanRouteDayEntity"/>. Start Day is the van's departure — truck, opening
/// odometer, RTI out — and an ADR walking a route taking orders has none of that to declare, so their
/// days carry no round and the attendance pages said "Route not recorded" for a rep who plainly has a
/// route. For an ADR the assignment is the best record there is.
///
/// ADRs only. A sales rep who checks into customers without starting the day has skipped the
/// departure check, and the missing route is how the page shows it; borrowing their assignment would
/// hide that. The truck is left out for the same kind of reason: the route's registered vehicle is
/// the van's, and an ADR on that route was not necessarily in it.
///
/// This is the assignment as it stands now, not as it stood on the day — there is no snapshot to read
/// for a day nobody started.
/// </summary>
internal static class AdrAssignedRoutes
{
    public sealed record AssignedRoute(string Code, string Name);

    public static async Task<Dictionary<Guid, AssignedRoute>> ForAsync(
        ApplicationDbContext db,
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken)
    {
        if (userIds.Count == 0) return new();

        var assigned = await db.Users
            .AsNoTracking()
            .Where(user => userIds.Contains(user.Id) && user.Route != null)
            .Select(user => new { user.Id, user.Role, user.Route!.Code, user.Route.Name })
            .ToListAsync(cancellationToken);

        // The role is compared here rather than in SQL: it is free text, and the handset already
        // learned the hard way that "ADR " and "Adr" are the same account.
        return assigned
            .Where(user => string.Equals(user.Role?.Trim(), ApplicationRoles.Adr, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(user => user.Id, user => new AssignedRoute(user.Code, user.Name));
    }
}
