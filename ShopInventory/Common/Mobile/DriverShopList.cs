using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models;

namespace ShopInventory.Common.Mobile;

/// <summary>The drivers' shop list, and which account it was read off.</summary>
public sealed record DriverShopListSource(Guid UserId, string Username, string Role, List<string> CustomerCodes);

/// <summary>
/// Reads the one list of shops the office has given every driver to deliver to.
/// </summary>
/// <remarks>
/// <para>There is no table for it. The portal's Driver Shop Access page saves one allowlist and writes
/// the same codes onto every <c>Driver</c> and <c>PodOperator</c> account's
/// <c>AssignedCustomerCodes</c> (<c>UpdateGlobalDriverAssignedCustomersHandler</c>), so the list is
/// whatever those accounts carry. They normally all agree; when they do not — an account created after
/// the last save, say — a driver's copy is preferred over an operator's, then the first by username,
/// so every reader lands on the same account.</para>
///
/// <para>This is the list <see cref="MobileAssignedCustomerScope"/> backfills a new driver from, and the
/// one a van rep's proof-of-delivery screen is scoped to. Both read it here so they cannot disagree
/// about which shops are on it.</para>
/// </remarks>
public static class DriverShopList
{
    /// <summary>
    /// The list, or null when no driver or POD operator account carries any shops.
    /// </summary>
    /// <param name="excludingUserId">An account not to read it off — the one being backfilled.</param>
    public static async Task<DriverShopListSource?> FindAsync(
        ApplicationDbContext db,
        Guid? excludingUserId,
        CancellationToken cancellationToken)
    {
        var candidates = await db.Users
            .AsNoTracking()
            .Where(candidate => (excludingUserId == null || candidate.Id != excludingUserId) &&
                (candidate.Role == ApplicationRoles.Driver || candidate.Role == ApplicationRoles.PodOperator) &&
                candidate.AssignedCustomerCodes != null)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.Username,
                candidate.Role,
                candidate.AssignedCustomerCodes
            })
            .ToListAsync(cancellationToken);

        return candidates
            .Select(candidate => new DriverShopListSource(
                candidate.Id,
                candidate.Username,
                candidate.Role,
                MobileAssignedCustomerScope.Normalize(Deserialize(candidate.AssignedCustomerCodes))))
            .Where(candidate => candidate.CustomerCodes.Count > 0)
            .OrderBy(candidate => string.Equals(candidate.Role, ApplicationRoles.Driver, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(candidate => candidate.Username, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static List<string> Deserialize(string? serializedCodes)
    {
        if (string.IsNullOrWhiteSpace(serializedCodes))
        {
            return new List<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(serializedCodes) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }
}
