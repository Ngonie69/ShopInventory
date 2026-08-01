using System.Security.Claims;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

/// <summary>
/// Works out which warehouse a new document should open on. The invoice and sales-order screens
/// both ask this, so an operator sees the same warehouse pre-selected either side instead of each
/// screen carrying its own rule and its own hardcoded "01".
/// </summary>
public static class DefaultWarehouseResolver
{
    /// <summary>
    /// Resolution order: the warehouse configured in Settings when the operator is allowed to use
    /// it, then the operator's own first assignment, then nothing.
    /// <para>
    /// A code the warehouse cache does not hold is never returned. A dropdown can only pre-select
    /// an option it actually has, so handing one back anyway leaves the control reading
    /// "Select…" while the lines underneath quietly carry the code.
    /// </para>
    /// </summary>
    /// <param name="user">
    /// The signed-in operator. Carries one "warehouse" claim per assigned warehouse — an operator
    /// with no such claim is unrestricted, not unassigned.
    /// </param>
    /// <param name="configuredDefault">DefaultWarehouse from Settings.</param>
    /// <param name="cachedWarehouses">The warehouses master data currently holds.</param>
    /// <returns>A warehouse code the caller can pre-select, or null when none is usable.</returns>
    public static string? Resolve(
        ClaimsPrincipal? user,
        string? configuredDefault,
        IReadOnlyCollection<WarehouseDto> cachedWarehouses)
    {
        var assigned = user?.FindAll("warehouse")
            .Select(claim => claim.Value)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .ToList() ?? new List<string>();

        bool IsCached(string code) => cachedWarehouses.Any(
            warehouse => string.Equals(warehouse.WarehouseCode, code, StringComparison.OrdinalIgnoreCase));

        // An operator restricted to a set of warehouses may not be entitled to the configured
        // default; one with no assignment at all is entitled to it.
        var mayUseConfigured = !string.IsNullOrWhiteSpace(configuredDefault)
            && (assigned.Count == 0
                || assigned.Contains(configuredDefault, StringComparer.OrdinalIgnoreCase));

        if (mayUseConfigured && IsCached(configuredDefault!))
        {
            return configuredDefault;
        }

        return assigned.FirstOrDefault(IsCached);
    }
}
