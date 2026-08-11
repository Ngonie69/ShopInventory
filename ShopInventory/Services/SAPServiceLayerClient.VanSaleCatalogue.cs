using ShopInventory.Models;

namespace ShopInventory.Services;

/// <summary>
/// The van sales approved catalogue as whole item master rows rather than as codes.
/// </summary>
/// <remarks>
/// Everything else that reads the approval list intersects it with a warehouse's stock, which answers
/// "what can this van sell today". A stock transfer request asks the opposite question — what the van
/// has run out of, or has never carried — so it needs the approval list standing on its own.
/// </remarks>
public partial class SAPServiceLayerClient
{
    public async Task<List<Item>> GetVanSalesApprovedItemsAsync(CancellationToken cancellationToken = default)
    {
        var approved = await GetVanSalesApprovedItemCodesAsync(cancellationToken);

        if (approved.Count == 0)
        {
            return [];
        }

        // Resolves through the stock reader, so an approved code since made invalid in SAP drops out
        // rather than reaching a handset as a line nobody can transfer.
        var items = await GetStockItemsByCodesAsync(approved, cancellationToken);

        _logger.LogInformation(
            "Van sales approved catalogue: {Resolved} live items from {Approved} approved codes",
            items.Count,
            approved.Count);

        return items
            .OrderBy(item => item.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
