using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Stock.Queries.GetStockInWarehouse;

public sealed class GetStockInWarehouseHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetStockInWarehouseHandler> logger
) : IRequestHandler<GetStockInWarehouseQuery, ErrorOr<WarehouseStockResponseDto>>
{
    public async Task<ErrorOr<WarehouseStockResponseDto>> Handle(
        GetStockInWarehouseQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.Stock.SapDisabled;

        try
        {
            var stockItems = await sapClient.GetStockQuantitiesInWarehouseAsync(
                request.WarehouseCode, cancellationToken);

            if (request.IncludePackagingStock)
            {
                await PopulatePackagingMaterialStock(stockItems, request.WarehouseCode, cancellationToken);
            }

            var response = new WarehouseStockResponseDto
            {
                WarehouseCode = request.WarehouseCode,
                TotalItems = stockItems.Count,
                ItemsInStock = stockItems.Count(s => s.InStock > 0),
                QueryDate = DateTime.UtcNow,
                Items = stockItems
            };

            logger.LogInformation("Retrieved {Count} stock items in warehouse {Warehouse}, {InStock} with stock",
                response.TotalItems, request.WarehouseCode, response.ItemsInStock);

            return response;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            logger.LogError(ex, "Timeout connecting to SAP Service Layer");
            return Errors.Stock.SapTimeout;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return Errors.Stock.SapConnectionError(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving stock for warehouse {Warehouse}", request.WarehouseCode);
            return Errors.Stock.SapError(ex.Message);
        }
    }

    private async Task PopulatePackagingMaterialStock(
        List<StockQuantityDto> stockItems,
        string warehouseCode,
        CancellationToken cancellationToken)
    {
        var packagingCodes = new HashSet<string>();

        foreach (var item in stockItems)
        {
            if (!string.IsNullOrWhiteSpace(item.PackagingCode))
                packagingCodes.Add(item.PackagingCode);
            if (!string.IsNullOrWhiteSpace(item.PackagingCodeLabels))
                packagingCodes.Add(item.PackagingCodeLabels);
            if (!string.IsNullOrWhiteSpace(item.PackagingCodeLids))
                packagingCodes.Add(item.PackagingCodeLids);
        }

        if (packagingCodes.Count == 0)
            return;

        var packagingStock = await sapClient.GetPackagingMaterialStockAsync(
            packagingCodes, warehouseCode, cancellationToken);

        foreach (var item in stockItems)
        {
            if (!string.IsNullOrWhiteSpace(item.PackagingCode) &&
                packagingStock.TryGetValue(item.PackagingCode, out var pkgStock))
            {
                item.PackagingMaterialStock = pkgStock;
            }
            if (!string.IsNullOrWhiteSpace(item.PackagingCodeLabels) &&
                packagingStock.TryGetValue(item.PackagingCodeLabels, out var lblStock))
            {
                item.PackagingLabelStock = lblStock;
            }
            if (!string.IsNullOrWhiteSpace(item.PackagingCodeLids) &&
                packagingStock.TryGetValue(item.PackagingCodeLids, out var lidStock))
            {
                item.PackagingLidStock = lidStock;
            }
        }

        logger.LogInformation("Populated packaging stock for {Count} unique packaging codes", packagingCodes.Count);
    }
}
