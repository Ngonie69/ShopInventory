using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;

namespace ShopInventory.Features.CountVariance;

/// <summary>
/// The warehouses that are vans, and whose they are.
/// </summary>
/// <remarks>
/// The same rule as the Van Stock and Replenishment reports: a van is a warehouse assigned to a rep
/// whom a depot supplies. Read from the assignment rather than a code prefix, because production
/// warehouse codes do not reliably carry one. Like those reports, a deactivated rep's van still counts
/// until its assignment is cleared.
/// </remarks>
internal static class VanWarehouses
{
    /// <summary>Van warehouse code → the rep's name (null when the user has no name on file).</summary>
    public static async Task<Dictionary<string, string?>> LoadAsync(
        ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        var users = await db.Users
            .AsNoTracking()
            .Where(user => user.SupplyingWarehouseCode != null && user.AssignedWarehouseCodes != null)
            .OrderBy(user => user.Id)
            .ToListAsync(cancellationToken);

        var vans = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var user in users)
        {
            var name = $"{user.FirstName} {user.LastName}".Trim();
            var rep = string.IsNullOrWhiteSpace(name) ? user.Username : name;

            foreach (var code in user.GetWarehouseCodes())
            {
                if (string.IsNullOrWhiteSpace(code))
                {
                    continue;
                }

                // Two reps on one van: name both rather than let the order of the rows decide.
                var key = code.Trim();
                vans[key] = vans.TryGetValue(key, out var existing) && !string.IsNullOrWhiteSpace(existing)
                    ? $"{existing}, {rep}"
                    : rep;
            }
        }

        return vans;
    }
}
