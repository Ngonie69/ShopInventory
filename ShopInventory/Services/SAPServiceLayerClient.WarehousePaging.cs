using ShopInventory.Models;

namespace ShopInventory.Services;

/// <summary>
/// Cursor paging over a warehouse's stocked items. See <see cref="WarehouseItemCursor"/> for why
/// this exists alongside the offset overload rather than replacing it, and
/// <see cref="WarehouseItemPageBuilder"/> for how a page is assembled.
/// </summary>
public partial class SAPServiceLayerClient
{
    public async Task<WarehouseItemPage> GetItemPageInWarehouseAsync(
        string warehouseCode,
        string? after,
        int pageSize,
        bool vanSaleOnly = false,
        CancellationToken cancellationToken = default)
    {
        // Never mutated: without vanSaleOnly this is the cached list instance itself.
        var itemCodes = await GetAllItemCodesInWarehouseAsync(warehouseCode, cancellationToken);

        if (vanSaleOnly)
        {
            // Narrowed before paging, so the page stays dense and the cursor walks approved items only.
            var approved = await GetVanSalesApprovedItemCodesAsync(cancellationToken);
            itemCodes = itemCodes.Where(approved.Contains).ToList();
        }

        var page = await WarehouseItemPageBuilder.BuildAsync(
            itemCodes,
            after,
            pageSize,
            (codes, token) => GetStockItemsByCodesAsync(codes, token),
            cancellationToken);

        _logger.LogInformation(
            "Cursor page of {Scope} items in warehouse {Warehouse} after {Cursor}: {Count} items",
            vanSaleOnly ? "van sales approved" : "all",
            warehouseCode,
            after ?? "(start)",
            page.Items.Count);

        return page;
    }
}
