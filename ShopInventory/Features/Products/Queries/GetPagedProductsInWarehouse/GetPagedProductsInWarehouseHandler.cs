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

            var itemCodes = items.Select(i => i.ItemCode).Where(c => c is not null).ToList();
            var allBatches = itemCodes.Count == 0
                ? []
                : await sapClient.GetBatchNumbersForItemsInWarehouseAsync(itemCodes!, request.WarehouseCode, cancellationToken);

            var relevantBatches = allBatches.Where(b => itemCodes.Contains(b.ItemCode)).ToList();
            var batchesByItem = relevantBatches
                .GroupBy(b => b.ItemCode ?? string.Empty)
                .ToDictionary(g => g.Key, g => g.ToList());

            var products = items.Select(item => MapToProductDto(item, batchesByItem)).ToList();

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
