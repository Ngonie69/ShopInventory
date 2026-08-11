using ShopInventory.Models;

namespace ShopInventory.Services;

/// <summary>
/// Cursor paging over a warehouse's stocked items. See <see cref="WarehouseItemCursor"/> for why
/// this exists alongside the offset overload rather than replacing it.
/// </summary>
public partial class SAPServiceLayerClient
{
    // A page is filled from consecutive windows of codes because a code can resolve to nothing:
    // GetStockItemsByCodesAsync keeps only live inventory items, so a code still carrying batch
    // stock for an item since marked invalid in SAP drops out of the result. Left unfilled, that
    // page comes back short — and a window of them comes back empty while HasMore is still true,
    // which reads to a caller like the end of the catalogue.
    //
    // This bounds how hard one page tries. Five windows is far past anything a healthy warehouse
    // needs, and the cursor still advances over everything consumed, so the worst case is a short
    // page the caller simply asks past. A short page is recoverable where an unbounded scan of a
    // warehouse full of dead codes is not.
    private const int MaxPageFillWindows = 5;

    public async Task<WarehouseItemPage> GetItemPageInWarehouseAsync(
        string warehouseCode,
        string? after,
        int pageSize,
        bool vanSaleOnly = false,
        CancellationToken cancellationToken = default)
    {
        var normalizedPageSize = Math.Max(pageSize, 1);

        var itemCodes = await GetAllItemCodesInWarehouseAsync(warehouseCode, cancellationToken);

        if (vanSaleOnly)
        {
            // Narrowed before paging, so the page stays dense and the cursor walks approved items only.
            var approved = await GetVanSalesApprovedItemCodesAsync(cancellationToken);
            itemCodes = itemCodes.Where(approved.Contains).ToList();
        }

        // Never mutated: without vanSaleOnly this is the cached list instance itself.
        var start = WarehouseItemCursor.FindStart(itemCodes, after);
        var consumed = start;

        if (consumed >= itemCodes.Count)
        {
            return new WarehouseItemPage([], false, null);
        }

        var items = new List<Item>();

        for (var window = 0;
             window < MaxPageFillWindows && consumed < itemCodes.Count && items.Count < normalizedPageSize;
             window++)
        {
            var take = Math.Min(normalizedPageSize - items.Count, itemCodes.Count - consumed);
            var windowCodes = itemCodes.GetRange(consumed, take);

            items.AddRange(await GetStockItemsByCodesAsync(windowCodes, cancellationToken));
            consumed += take;
        }

        var hasMore = consumed < itemCodes.Count;

        _logger.LogInformation(
            "Cursor page of {Scope} items in warehouse {Warehouse} after {Cursor}: {Count} items from {Consumed} codes",
            vanSaleOnly ? "van sales approved" : "all",
            warehouseCode,
            after ?? "(start)",
            items.Count,
            consumed - start);

        return new WarehouseItemPage(items, hasMore, hasMore ? itemCodes[consumed - 1] : null);
    }
}
