using ShopInventory.DTOs;
using ShopInventory.Models;

namespace ShopInventory.Common.Pods;

internal static class PodLocationScope
{
    private static readonly IReadOnlyDictionary<string, string[]> SectionFilterValuesByCanonicalSection =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Cheeseman"] = ["Cheeseman", "Cheeseman DC Harare", "Cheeseman DC Meyrick"],
            ["Factory"] = ["Factory", "Factory Dispatch", "Factory - Dispatch", "Factory-Dispatch"],
            ["Graniteside"] = ["Graniteside"],
            ["Machipisa"] = ["Machipisa"],
            ["Bulawayo"] = ["Bulawayo", "Cheeseman DC Byo"],
            ["Cheeseman DC Richwell"] = ["Cheeseman DC Richwell"],
            ["Cheeseman DC Vic Falls"] = ["Cheeseman DC Vic Falls"],
            ["Kefalos Byo"] = ["Kefalos Byo"]
        };

    private static readonly IReadOnlyDictionary<string, string> CanonicalSectionsByNormalizedValue =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CHEESEMAN"] = "Cheeseman",
            ["CHEESEMANDCHARARE"] = "Cheeseman",
            ["CHEESEMANDCMEYRICK"] = "Cheeseman",
            ["FACTORY"] = "Factory",
            ["FACTORYDISPATCH"] = "Factory",
            ["GRANITESIDE"] = "Graniteside",
            ["MACHIPISA"] = "Machipisa",
            ["BULAWAYO"] = "Bulawayo",
            ["CHEESEMANDCBYO"] = "Bulawayo",
            ["CHEESEMANDCRICHWELL"] = "Cheeseman DC Richwell",
            ["CHEESEMANDCVICFALLS"] = "Cheeseman DC Vic Falls",
            ["KEFALOSBYO"] = "Kefalos Byo"
        };

    public static IReadOnlyList<string> GetSectionFilterValues(string? assignedSection)
    {
        var canonicalSection = CanonicalizeSection(assignedSection);
        if (string.IsNullOrWhiteSpace(canonicalSection))
        {
            return [];
        }

        return SectionFilterValuesByCanonicalSection.TryGetValue(canonicalSection, out var values)
            ? values
            : [canonicalSection];
    }

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

    public static IReadOnlyList<string> GetWarehouseCodesForAssignedSection(
        IEnumerable<WarehouseDto> warehouses,
        string assignedSection)
    {
        var canonicalAssignedSection = CanonicalizeSection(assignedSection);
        if (string.IsNullOrWhiteSpace(canonicalAssignedSection))
        {
            return [];
        }

        return warehouses
            .Where(warehouse => !string.IsNullOrWhiteSpace(warehouse.WarehouseCode))
            .Where(warehouse =>
                string.Equals(CanonicalizeSection(warehouse.WarehouseCode), canonicalAssignedSection, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(CanonicalizeSection(warehouse.Location), canonicalAssignedSection, StringComparison.OrdinalIgnoreCase))
            .Select(warehouse => warehouse.WarehouseCode!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool InvoiceMatchesAssignedSection(
        Invoice invoice,
        string assignedSection,
        IReadOnlyDictionary<string, string?> warehouseLocations)
    {
        return WarehouseCodesMatchAssignedSection(
            invoice.DocumentLines?
                .Select(line => line.WarehouseCode),
            assignedSection,
            warehouseLocations);
    }

    public static bool WarehouseCodesMatchAssignedSection(
        IEnumerable<string?>? warehouseCodes,
        string assignedSection,
        IReadOnlyDictionary<string, string?> warehouseLocations)
    {
        if (warehouseCodes is null)
        {
            return false;
        }

        var canonicalAssignedSection = CanonicalizeSection(assignedSection);

        foreach (var warehouseCode in warehouseCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(CanonicalizeSection(warehouseCode), canonicalAssignedSection, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (warehouseLocations.TryGetValue(warehouseCode, out var location) &&
                string.Equals(CanonicalizeSection(location), canonicalAssignedSection, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static string CanonicalizeSection(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmedValue = value.Trim();
        var normalizedValue = NormalizeSectionToken(trimmedValue);
        return CanonicalSectionsByNormalizedValue.TryGetValue(normalizedValue, out var canonicalSection)
            ? canonicalSection
            : trimmedValue;
    }

    private static string NormalizeSectionToken(string value)
    {
        return string.Concat(value.Where(character => !char.IsWhiteSpace(character) && character != '-'))
            .ToUpperInvariant();
    }
}