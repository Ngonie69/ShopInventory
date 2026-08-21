using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Products.Queries.GetPagedProductsInWarehouse;

public sealed class GetPagedProductsInWarehouseHandler(
    ISAPServiceLayerClient sapClient,
    ILocalPriceCatalogService localPriceCatalogService,
    IOptions<SAPSettings> settings,
    ILogger<GetPagedProductsInWarehouseHandler> logger
) : IRequestHandler<GetPagedProductsInWarehouseQuery, ErrorOr<WarehouseProductsPagedResponseDto>>
{
    public async Task<ErrorOr<WarehouseProductsPagedResponseDto>> Handle(
        GetPagedProductsInWarehouseQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.Product.SapDisabled;

        try
        {
            var (items, hasMore, nextCursor) = request.IsCursorPaged
                ? await ReadCursorPageAsync(request, cancellationToken)
                : await ReadOffsetPageAsync(request, cancellationToken);

            var itemCodes = items
                .Select(i => i.ItemCode)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code!)
                .ToList();

            // A snapshot read: this is a catalogue page, and every extra round trip here is spent
            // with a rep watching a spinner. See the parameter's remarks for why the movement paths
            // must not do the same.
            var allBatches = itemCodes.Count == 0
                ? []
                : await sapClient.GetBatchNumbersForItemsInWarehouseAsync(
                    itemCodes, request.WarehouseCode, allowCachedSnapshot: true, cancellationToken);

            var priceMap = await BuildPriceMapAsync(request, itemCodes, cancellationToken);

            var itemCodeSet = itemCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var relevantBatches = allBatches
                .Where(batch => batch.ItemCode is not null && itemCodeSet.Contains(batch.ItemCode))
                .ToList();
            var batchesByItem = relevantBatches
                .GroupBy(b => b.ItemCode ?? string.Empty)
                .ToDictionary(g => g.Key, g => g.ToList());

            var products = items.Select(item => MapToProductDto(item, batchesByItem, priceMap)).ToList();

            var response = new WarehouseProductsPagedResponseDto
            {
                WarehouseCode = request.WarehouseCode,
                Page = request.Page,
                PageSize = request.PageSize,
                Count = products.Count,
                HasMore = hasMore,
                NextCursor = nextCursor,
                Products = products
            };

            logger.LogInformation(
                "Retrieved {Position} of {Scope} products in warehouse {Warehouse} ({Count} records)",
                request.IsCursorPaged ? $"cursor page after {request.After ?? "(start)"}" : $"page {request.Page}",
                request.VanSaleOnly ? "van sales approved" : "all",
                request.WarehouseCode,
                products.Count);

            return response;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            logger.LogError(ex, "Timeout connecting to SAP Service Layer");
            return Errors.Product.SapTimeout;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return Errors.Product.SapConnectionError(ex.Message);
        }
    }

    /// <summary>
    /// Cursor paging: safe to walk across pages while the warehouse's stock is moving.
    /// </summary>
    private async Task<(List<Item> Items, bool HasMore, string? NextCursor)> ReadCursorPageAsync(
        GetPagedProductsInWarehouseQuery request,
        CancellationToken cancellationToken)
    {
        var page = await sapClient.GetItemPageInWarehouseAsync(
            request.WarehouseCode, request.After, request.PageSize, request.VanSaleOnly, cancellationToken);

        return (page.Items, page.HasMore, page.NextCursor);
    }

    /// <summary>
    /// Offset paging, kept for callers built against it before cursors existed.
    /// </summary>
    /// <remarks>
    /// Unchanged on purpose. Handsets in the field cannot all be updated at once, and they page by
    /// offset; making pages dense here — the fix that stops an empty page reading as the end of the
    /// catalogue — would consume a variable number of codes per page and move the boundaries those
    /// callers compute. They get both fixes by opting in to cursors, not by having this shift.
    /// </remarks>
    private async Task<(List<Item> Items, bool HasMore, string? NextCursor)> ReadOffsetPageAsync(
        GetPagedProductsInWarehouseQuery request,
        CancellationToken cancellationToken)
    {
        var (items, hasMore) = await sapClient.GetPagedItemsInWarehouseAsync(
            request.WarehouseCode, request.Page, request.PageSize, request.VanSaleOnly, cancellationToken);

        return (items, hasMore, null);
    }

    private async Task<Dictionary<string, decimal>> BuildPriceMapAsync(
        GetPagedProductsInWarehouseQuery request,
        IReadOnlyCollection<string> itemCodes,
        CancellationToken cancellationToken)
    {
        if (itemCodes.Count == 0)
        {
            return new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(request.BusinessPartnerCode))
        {
            var pricing = await localPriceCatalogService.GetBusinessPartnerPricingAsync(
                request.BusinessPartnerCode,
                itemCodes,
                cancellationToken);

            if (pricing?.Prices.Prices is { Count: > 0 })
            {
                return pricing.Prices.Prices
                    .Where(price => !string.IsNullOrWhiteSpace(price.ItemCode))
                    .ToDictionary(price => price.ItemCode!, price => price.Price, StringComparer.OrdinalIgnoreCase);
            }

            logger.LogWarning(
                "No locally stored BP pricing found for business partner {BusinessPartnerCode} in warehouse product page {Page}.",
                request.BusinessPartnerCode,
                request.Page);
        }

        if (request.PriceListNum is > 0)
        {
            var pricing = await localPriceCatalogService.GetPricesByPriceListAsync(
                request.PriceListNum.Value,
                itemCodes,
                cancellationToken);

            if (pricing.Prices is { Count: > 0 })
            {
                return pricing.Prices
                    .Where(price => !string.IsNullOrWhiteSpace(price.ItemCode))
                    .ToDictionary(price => price.ItemCode!, price => price.Price, StringComparer.OrdinalIgnoreCase);
            }
        }

        return new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
    }

    private static ProductDto MapToProductDto(
        Item item,
        Dictionary<string, List<BatchNumber>> batchesByItem,
        IReadOnlyDictionary<string, decimal> priceMap)
    {
        var itemBatches = batchesByItem.TryGetValue(item.ItemCode ?? string.Empty, out var batches)
            ? batches
            : [];
        var price = item.ItemCode is not null && priceMap.TryGetValue(item.ItemCode, out var matchedPrice)
            ? matchedPrice
            : 0m;

        return new ProductDto
        {
            ItemCode = item.ItemCode,
            ItemName = item.ItemName,
            BarCode = item.BarCode,
            ItemType = item.ItemType,
            ManagesBatches = item.ManageBatchNumbers == "tYES",
            QuantityInStock = item.QuantityOnStock,
            QuantityAvailable = item.QuantityOnStock - item.QuantityOrderedByCustomers,
            QuantityCommitted = item.QuantityOrderedByCustomers,
            QuantityOnStock = item.QuantityOnStock,
            Price = price,
            DefaultWarehouse = item.DefaultWarehouse,
            Category = item.U_ItemGroup,
            ItemsGroupCode = item.ItemsGroupCode,
            UoM = item.InventoryUOM,
            Batches = itemBatches.Select(MapToBatchDto).ToList()
        };
    }

    private static BatchDto MapToBatchDto(BatchNumber batch) => new()
    {
        BatchNumber = batch.BatchNum,
        Quantity = batch.Quantity,
        Status = batch.Status,
        ExpiryDate = batch.ExpiryDate,
        ManufacturingDate = batch.ManufacturingDate,
        AdmissionDate = batch.AdmissionDate,
        Location = batch.Location,
        Notes = batch.Notes
    };
}
