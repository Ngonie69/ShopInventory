using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.CreditNoteApprovals;

/// <summary>
/// Where a credit memo draft returns its stock. Returned crates and product go to the wash bay, so a
/// draft whose lines name any other warehouse (a depot's, a van's, a shop's) is unusual and the
/// approver is told before approving or adding it. The draft is not changed here: a wrong warehouse is
/// corrected in SAP.
/// </summary>
public static class CreditNoteReturnWarehouse
{
    /// <summary>The warehouse credit notes normally credit stock into.</summary>
    public const string Expected = "WASHBAY";

    /// <summary>The distinct warehouses the lines credit into, in line order; blanks left out.</summary>
    public static IReadOnlyList<string> Used(IEnumerable<CreditNoteDraftLineDto> lines) =>
        lines
            .Select(line => line.WarehouseCode?.Trim())
            .Where(code => !string.IsNullOrEmpty(code))
            .Select(code => code!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>The warehouses the lines credit into other than <see cref="Expected"/>.</summary>
    public static IReadOnlyList<string> Unusual(IEnumerable<CreditNoteDraftLineDto> lines) =>
        Used(lines).Where(IsUnusual).ToList();

    /// <summary>True for a named warehouse other than <see cref="Expected"/>; a blank is not judged.</summary>
    public static bool IsUnusual(string? warehouseCode) =>
        !string.IsNullOrWhiteSpace(warehouseCode)
        && !string.Equals(warehouseCode.Trim(), Expected, StringComparison.OrdinalIgnoreCase);
}
