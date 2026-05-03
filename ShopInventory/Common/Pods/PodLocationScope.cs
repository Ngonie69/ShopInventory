using ShopInventory.DTOs;
using ShopInventory.Models;

namespace ShopInventory.Common.Pods;

internal static class PodLocationScope
{
    public static Dictionary<string, string?> BuildWarehouseLocationLookup(IEnumerable<WarehouseDto> warehouses)
    {
        return warehouses
            .Where(warehouse => !string.IsNullOrWhiteSpace(warehouse.WarehouseCode))
            .GroupBy(warehouse => warehouse.WarehouseCode!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().Location?.Trim(),
                StringComparer.OrdinalIgnoreCase);
    }

    public static bool InvoiceMatchesAssignedSection(
        Invoice invoice,
        string assignedSection,
        IReadOnlyDictionary<string, string?> warehouseLocations)
    {
        var warehouseCodes = invoice.DocumentLines?
            .Select(line => line.WarehouseCode)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase);

        if (warehouseCodes is null)
        {
            return false;
        }

        foreach (var warehouseCode in warehouseCodes)
        {
            if (string.Equals(warehouseCode, assignedSection, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (warehouseLocations.TryGetValue(warehouseCode, out var location) &&
                string.Equals(location, assignedSection, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}