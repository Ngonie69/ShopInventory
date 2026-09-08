using Microsoft.Extensions.Options;

namespace ShopInventory.Configuration;

/// <summary>
/// Refuses to start when <c>DailyStock:MonitoredWarehouses</c> is empty.
/// </summary>
/// <remarks>
/// This exists because of what the fix to the double-binding gave up. The list used to be declared
/// with all 21 warehouses as a collection initializer, which meant configuration could never leave it
/// empty — at the cost of binding to 42, since the binder appends rather than replaces. Taking the
/// initializer out fixes the doubling and opens a new hole: if the key is ever missing or misspelled,
/// the list binds to nothing and every consumer treats that as a legitimate answer.
///
/// Nothing downstream would report it. <c>FetchDailyStockHandler</c> loops over the list, so an empty
/// one snapshots no warehouses and returns success having done nothing; the snapshot rows are simply
/// absent, so every till validates its sales against nothing and refuses every line, silently, until
/// someone tries to sell. That is a worse failure than not starting, and it is invisible for hours.
///
/// Registered with <c>ValidateOnStart</c>, so it fails during startup where the deployment script's
/// readiness probe catches it before the slot is swapped in, rather than at 07:00 the next morning.
/// </remarks>
public sealed class DailyStockSettingsValidation : IValidateOptions<DailyStockSettings>
{
    public ValidateOptionsResult Validate(string? name, DailyStockSettings options)
    {
        if (options.MonitoredWarehouses.Count == 0)
        {
            return ValidateOptionsResult.Fail(
                "DailyStock:MonitoredWarehouses is empty. It has no code-level default by design — a "
                + "default here would be appended to the configured list rather than replacing it, "
                + "which is what made the snapshot job read every warehouse twice. Supply the list in "
                + "appsettings.json. Left empty, the daily stock snapshot would silently cover no "
                + "warehouses and every till would refuse every sale.");
        }

        // Not fatal: the readers deduplicate, so a repeat costs a wasted pass rather than a wrong
        // answer. It does mean the list is being assembled from two places again, which is worth
        // seeing before it becomes the same bug.
        var duplicates = options.MonitoredWarehouses
            .GroupBy(warehouse => warehouse.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        return duplicates.Count > 0
            ? ValidateOptionsResult.Fail(
                $"DailyStock:MonitoredWarehouses lists {string.Join(", ", duplicates)} more than once. "
                + "The daily stock snapshot walks this list, so each repeat is a second pass over that "
                + "warehouse and a second set of SAP reads.")
            : ValidateOptionsResult.Success;
    }
}
