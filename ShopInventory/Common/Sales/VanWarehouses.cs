using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;

namespace ShopInventory.Common.Sales;

/// <summary>
/// One van warehouse: who drives it and which business partners its reps' sales invoice to.
/// </summary>
/// <param name="Code">The warehouse code, trimmed, spelt as the first rep's assignment spells it.</param>
/// <param name="Rep">
/// The active rep's name, falling back to the account they sign in as. Two active reps on one van are
/// both named, in user-id order, rather than letting the order of the rows decide.
/// </param>
/// <param name="BusinessPartnerCodes">
/// Every <c>AssignedBusinessPartnerCode</c> on the van's active reps. Not the warehouse code: VAN001's sales
/// invoice to VAN010. Empty when no rep on the van has one.
/// </param>
public sealed record VanWarehouse(string Code, string Rep, IReadOnlySet<string> BusinessPartnerCodes);

/// <summary>
/// The warehouses that are vans, and whose they are.
/// </summary>
/// <remarks>
/// <para>
/// A van is a warehouse assigned to a rep (<c>User.AssignedWarehouseCodes</c>) whom a depot supplies
/// (<c>User.SupplyingWarehouseCode</c>) — that pairing is what makes it a van rather than a store. The
/// Van Stock, Van Replenishment and Count Variance reports all read vans here, so no two of them can
/// disagree about which warehouses are vans.
/// </para>
/// <para>
/// Production van warehouses are in fact coded <c>VAN0nn</c> (VAN001, VAN004 and up; the higher
/// VAN008–VAN020 codes are also the vans' business partners — see <see cref="VanSalesAccounts"/>), so
/// a prefix match would work today. It is still not what this does, because the prefix is a
/// convention and the assignment is the definition: a van ever coded differently, or a store ever
/// coded <c>VAN…</c>, is classified correctly without anybody remembering to update a filter.
/// </para>
/// <para>
/// Only active reps count. A deactivated rep neither makes a warehouse a van nor is named on one, so
/// a van whose only rep has been deactivated drops out of every van report — even while it still
/// holds stock — until an active rep is assigned to it. Decided on PR #579.
/// </para>
/// </remarks>
public static class VanWarehouses
{
    /// <summary>Van warehouse code (case-insensitive) → the van.</summary>
    public static async Task<Dictionary<string, VanWarehouse>> LoadAsync(
        ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        // Materialised as entities so the codes can be read by the entity's own helper. They are a
        // JSON list in one column, and re-implementing that parse here is how the two would drift.
        var users = await db.Users
            .AsNoTracking()
            .Where(user => user.IsActive
                           && user.SupplyingWarehouseCode != null
                           && user.AssignedWarehouseCodes != null)
            .OrderBy(user => user.Id)
            .ToListAsync(cancellationToken);

        var reps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var accounts = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

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

                var key = code.Trim();
                reps[key] = reps.TryGetValue(key, out var existing) && !string.IsNullOrWhiteSpace(existing)
                    ? $"{existing}, {rep}"
                    : rep;

                if (!accounts.TryGetValue(key, out var codes))
                {
                    codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    accounts[key] = codes;
                }

                if (!string.IsNullOrWhiteSpace(user.AssignedBusinessPartnerCode))
                {
                    codes.Add(user.AssignedBusinessPartnerCode.Trim());
                }
            }
        }

        // Keyed by the dictionary's own spelling of the code, which is the first one seen.
        return reps.ToDictionary(
            pair => pair.Key,
            pair => new VanWarehouse(pair.Key, pair.Value, accounts[pair.Key]),
            StringComparer.OrdinalIgnoreCase);
    }
}
