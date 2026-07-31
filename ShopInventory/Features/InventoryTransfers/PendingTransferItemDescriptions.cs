using ShopInventory.Common.Validation;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.InventoryTransfers;

/// <summary>
/// Fills in the item names on held transfer lines. The stored payload carries item codes only,
/// so an approver reading a draft would otherwise see a bare code with nothing to check it
/// against.
/// </summary>
public static class PendingTransferItemDescriptions
{
    /// <summary>
    /// Resolves descriptions from SAP for every draft being returned, in chunked reads.
    /// </summary>
    /// <remarks>
    /// This used to read the API's own <c>Products</c> table, but nothing in the codebase ever
    /// writes that table, so every description came back null and any consumer other than the
    /// Blazor app — which falls back to its own product cache — showed a bare code. SAP is the only
    /// source here that actually holds the names.
    ///
    /// Codes SAP cannot answer for keep a blank description, as does every code when SAP is
    /// switched off or unreachable: the code alone is what the drawer showed before, and it is not
    /// worth failing an approver's read over.
    /// </remarks>
    public static async Task AttachAsync(
        ISAPServiceLayerClient sapClient,
        SAPSettings sapSettings,
        IReadOnlyList<PendingInventoryTransferDto> transfers,
        CancellationToken cancellationToken)
    {
        if (!sapSettings.Enabled)
            return;

        var lines = transfers.SelectMany(transfer => transfer.Lines).ToList();
        var itemCodes = lines
            .Select(line => UomQuantityValidation.NormalizeItemCode(line.ItemCode))
            .Where(code => code is not null)
            .Select(code => code!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (itemCodes.Count == 0)
            return;

        var names = await sapClient.GetItemNamesAsync(itemCodes, cancellationToken);

        foreach (var line in lines)
        {
            var itemCode = UomQuantityValidation.NormalizeItemCode(line.ItemCode);
            if (itemCode is null)
                continue;

            if (names.TryGetValue(itemCode, out var itemName) && !string.IsNullOrWhiteSpace(itemName))
                line.ItemDescription = itemName;
        }
    }
}
