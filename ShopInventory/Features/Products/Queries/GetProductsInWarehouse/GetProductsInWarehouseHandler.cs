using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Products.Queries.GetProductsInWarehouse;

public sealed class GetProductsInWarehouseHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetProductsInWarehouseHandler> logger
) : IRequestHandler<GetProductsInWarehouseQuery, ErrorOr<WarehouseProductsResponseDto>>
{
    public async Task<ErrorOr<WarehouseProductsResponseDto>> Handle(
        GetProductsInWarehouseQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.Product.SapDisabled;

        try
        {
            const int sapPageSize = 100;
            var page = 1;
            var hasMore = false;
            var products = new List<ProductDto>();

            do
            {
                var (items, pageHasMore) = await sapClient.GetPagedItemsInWarehouseAsync(
                    request.WarehouseCode, page, sapPageSize, cancellationToken);

                var itemCodes = items
                    .Select(i => i.ItemCode)
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Select(c => c!)
                    .ToList();

                var itemCodeSet = itemCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var pageBatches = itemCodes.Count == 0
                    ? []
                    : await sapClient.GetBatchNumbersForItemsInWarehouseAsync(
                        itemCodes, request.WarehouseCode, cancellationToken);

                var batchesByItem = pageBatches
                    .Where(b => b.ItemCode is not null && itemCodeSet.Contains(b.ItemCode))
                    .GroupBy(b => b.ItemCode ?? string.Empty)
                    .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

                products.AddRange(items.Select(item => MapToProductDto(item, batchesByItem)));

                hasMore = pageHasMore;
                page++;
            }
            while (hasMore);

            var response = new WarehouseProductsResponseDto
            {
                WarehouseCode = request.WarehouseCode,
                TotalProducts = products.Count,
                ProductsWithBatches = products.Count(p => p.Batches?.Count > 0),
                Products = products
            };

            logger.LogInformation("Retrieved {Count} products in warehouse {Warehouse}, {BatchCount} with batches",
                response.TotalProducts, request.WarehouseCode, response.ProductsWithBatches);

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

    private static ProductDto MapToProductDto(Item item, Dictionary<string, List<BatchNumber>> batchesByItem)
    {
        var itemBatches = batchesByItem.TryGetValue(item.ItemCode ?? string.Empty, out var batches)
            ? batches
            : [];

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
