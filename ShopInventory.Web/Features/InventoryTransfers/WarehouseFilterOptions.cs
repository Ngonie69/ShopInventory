using ShopInventory.Web.Components;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.InventoryTransfers;

/// <summary>
/// The rows of a From or To warehouse filter on /inventory-transfers.
/// </summary>
/// <remarks>
/// The list used to be the codes found on the loaded page and nothing else, so a warehouse whose
/// requests were not among the newest hundred could not be chosen at all. A user who acts on any
/// warehouse — an administrator, the wash bay — now gets every warehouse: those on the page first,
/// each with how many of its rows it holds, then the rest A to Z, each with its name. Everyone else
/// keeps the codes on the page, which is also everything a warehouse-scoped user can act on there.
/// </remarks>
public static class WarehouseFilterOptions
{
    public const string OnThisPageGroup = "On this page";
    public const string OtherWarehousesGroup = "Every other warehouse";

    /// <param name="anyLabel">The "no filter" row — "Any source".</param>
    /// <param name="codesOnPage">The warehouse at this end of each loaded row, one entry per row.</param>
    /// <param name="warehouses">Every warehouse the web app knows, for names and the rest of the list.</param>
    /// <param name="everyWarehouse">Whether the user is offered every warehouse.</param>
    /// <param name="selected">The current filter, kept choosable even when nothing else names it.</param>
    public static IReadOnlyList<NocturneSelectOption<string>> Build(
        string anyLabel,
        IEnumerable<string?> codesOnPage,
        IEnumerable<WarehouseDto>? warehouses,
        bool everyWarehouse,
        string? selected = null)
    {
        var counts = codesOnPage
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code!.Trim())
            .GroupBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.First(), group => group.Count(), StringComparer.OrdinalIgnoreCase);

        var rows = new List<NocturneSelectOption<string>> { NocturneSelectOption.All(anyLabel) };

        if (!everyWarehouse)
        {
            rows.AddRange(counts.Keys
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(code => new NocturneSelectOption<string>(code, code, "accent")));
            return rows;
        }

        var names = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var warehouse in warehouses ?? [])
        {
            if (!string.IsNullOrWhiteSpace(warehouse.WarehouseCode))
                names.TryAdd(warehouse.WarehouseCode.Trim(), NameOrNull(warehouse));
        }

        // A filter left set on a warehouse that has since gone from both lists would otherwise
        // leave the trigger reading "Any source" while the page is still filtered by it.
        var selectedCode = string.IsNullOrWhiteSpace(selected) ? null : selected.Trim();
        if (selectedCode is not null && !counts.ContainsKey(selectedCode) && !names.ContainsKey(selectedCode))
            counts[selectedCode] = 0;

        rows.AddRange(counts
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new NocturneSelectOption<string>(pair.Key, pair.Key, "accent")
            {
                Detail = names.GetValueOrDefault(pair.Key),
                Hint = pair.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Group = OnThisPageGroup
            }));

        rows.AddRange(names
            .Where(pair => !counts.ContainsKey(pair.Key))
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new NocturneSelectOption<string>(pair.Key, pair.Key, "neutral")
            {
                Detail = pair.Value,
                Group = OtherWarehousesGroup
            }));

        return rows;
    }

    private static string? NameOrNull(WarehouseDto warehouse) =>
        string.IsNullOrWhiteSpace(warehouse.WarehouseName) ||
        string.Equals(warehouse.WarehouseName.Trim(), warehouse.WarehouseCode?.Trim(), StringComparison.OrdinalIgnoreCase)
            ? null
            : warehouse.WarehouseName.Trim();
}
