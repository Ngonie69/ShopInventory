using ShopInventory.Web.Models;

namespace ShopInventory.Web.Components;

/// <summary>
/// Turns warehouses into <see cref="NocturnePicker"/> rows.
/// </summary>
/// <remarks>
/// The sibling of <see cref="BusinessPartnerOptions"/>, and here for the same
/// reason: three call sites now — /shops, and the default and supplying
/// warehouse fields on /user-management — and the judgements below are the kind
/// that drift the moment they are made twice.
/// </remarks>
public static class WarehouseOptions
{
    /// <summary>
    /// The rows, <b>in the caller's own order</b>. Deliberately not sorted here:
    /// two of the three callers lead their list with a synthetic "Currently
    /// assigned" row for a code the warehouse master no longer offers, and
    /// sorting would bury the one row the administrator most needs to see. A
    /// caller that wants them by code sorts its own input.
    /// </summary>
    /// <remarks>
    /// Active-only filtering is the caller's too, for the same reason: the
    /// /user-management lists are filtered once when the cache lands and then
    /// have that synthetic row added, so a filter applied here would strip it.
    /// </remarks>
    public static List<NocturnePickerOption> From(IEnumerable<WarehouseDto>? warehouses) =>
        (warehouses ?? [])
            .Where(warehouse => !string.IsNullOrWhiteSpace(warehouse.WarehouseCode))
            .Select(warehouse => new NocturnePickerOption(
                warehouse.WarehouseCode!,
                string.IsNullOrWhiteSpace(warehouse.WarehouseName)
                    ? warehouse.WarehouseCode!
                    : warehouse.WarehouseName))
            .ToList();

    /// <summary>The same rows, ordered by code — what a plain master list wants.</summary>
    public static List<NocturnePickerOption> ByCode(IEnumerable<WarehouseDto>? warehouses) =>
        From(warehouses).OrderBy(option => option.Value, StringComparer.OrdinalIgnoreCase).ToList();
}
