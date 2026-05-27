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
            var (items, hasMore) = await sapClient.GetPagedItemsInWarehouseAsync(
                request.WarehouseCode, request.Page, request.PageSize, cancellationToken);

            var itemCodes = items
                .Select(i => i.ItemCode)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code!)
                .ToList();

            var allBatches = itemCodes.Count == 0
                ? []
                : await sapClient.GetBatchNumbersForItemsInWarehouseAsync(itemCodes, request.WarehouseCode, cancellationToken);

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
                Products = products
            };

            logger.LogInformation("Retrieved page {Page} of products in warehouse {Warehouse} ({Count} records)",
                request.Page, request.WarehouseCode, products.Count);

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
            Category = item.ItemsGroupCode?.ToString(),
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
