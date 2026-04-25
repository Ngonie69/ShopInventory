using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Stock.Queries.GetStockForItemsInWarehouse;

public sealed class GetStockForItemsInWarehouseHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetStockForItemsInWarehouseHandler> logger
) : IRequestHandler<GetStockForItemsInWarehouseQuery, ErrorOr<WarehouseStockResponseDto>>
{
    public async Task<ErrorOr<WarehouseStockResponseDto>> Handle(
        GetStockForItemsInWarehouseQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.Stock.SapDisabled;

        if (string.IsNullOrWhiteSpace(request.WarehouseCode))
            return Errors.Stock.InvalidRequest("Warehouse code is required.");

        var itemCodes = request.ItemCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToList();

        if (itemCodes.Count == 0)
            return Errors.Stock.InvalidRequest("At least one item code is required.");

        try
        {
            var stockItems = await sapClient.GetStockQuantitiesForItemsInWarehouseAsync(
                request.WarehouseCode,
                itemCodes,
                cancellationToken);

            var response = new WarehouseStockResponseDto
            {
                WarehouseCode = request.WarehouseCode,
                TotalItems = stockItems.Count,
                ItemsInStock = stockItems.Count(s => s.InStock > 0),
                QueryDate = DateTime.UtcNow,
                Items = stockItems
            };

            logger.LogInformation(
                "Retrieved stock for {RequestedCount} requested items in warehouse {Warehouse}, matched {MatchedCount}",
                itemCodes.Count,
                request.WarehouseCode,
                stockItems.Count);

            return response;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            logger.LogError(ex, "Timeout connecting to SAP Service Layer");
            return Errors.Stock.SapTimeout;
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "SAP connection aborted for warehouse {Warehouse} item stock lookup", request.WarehouseCode);
            return Errors.Stock.SapConnectionError(ex.InnerException?.Message ?? ex.Message);
        }
        catch (TaskCanceledException)
        {
            return Errors.Stock.SapError("Request was cancelled by client.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return Errors.Stock.SapConnectionError(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving item stock for warehouse {Warehouse}", request.WarehouseCode);
            return Errors.Stock.SapError(ex.Message);
        }
    }
}