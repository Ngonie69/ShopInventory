using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;

namespace ShopInventory.Features.InventoryTransfers;

/// <summary>
/// Fills in the item names on held transfer lines. The stored payload carries item codes only,
/// so an approver reading a draft would otherwise see a bare code with nothing to check it
/// against.
/// </summary>
public static class PendingTransferItemDescriptions
{
    /// <summary>
    /// Resolves descriptions from the local item cache in one query across every draft being
    /// returned. Items the cache has not seen keep a blank description: the code alone is what
    /// the drawer showed before, and it is not worth blocking the read on SAP for.
    /// </summary>
    public static async Task AttachAsync(
        ApplicationDbContext context,
        IReadOnlyList<PendingInventoryTransferDto> transfers,
        CancellationToken cancellationToken)
    {
        var lines = transfers.SelectMany(transfer => transfer.Lines).ToList();
        var itemCodes = lines
            .Select(line => line.ItemCode)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (itemCodes.Count == 0)
            return;

        var products = await context.Products.AsNoTracking()
            .Where(product => itemCodes.Contains(product.ItemCode))
            .Select(product => new { product.ItemCode, product.ItemName })
            .ToListAsync(cancellationToken);

        if (products.Count == 0)
            return;

        var names = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var product in products)
            names[product.ItemCode] = product.ItemName;

        foreach (var line in lines)
        {
            if (line.ItemCode is not null && names.TryGetValue(line.ItemCode, out var itemName))
                line.ItemDescription = itemName;
        }
    }
}
