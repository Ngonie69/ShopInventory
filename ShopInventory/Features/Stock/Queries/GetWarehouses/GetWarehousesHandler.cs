using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Stock.Queries.GetWarehouses;

public sealed class GetWarehousesHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetWarehousesHandler> logger
) : IRequestHandler<GetWarehousesQuery, ErrorOr<WarehouseListResponseDto>>
{
    public async Task<ErrorOr<WarehouseListResponseDto>> Handle(
        GetWarehousesQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.Stock.SapDisabled;

        try
        {
            var warehouses = await sapClient.GetWarehousesAsync(cancellationToken);

            var response = new WarehouseListResponseDto
            {
                TotalWarehouses = warehouses.Count,
                Warehouses = warehouses
            };

            logger.LogInformation("Retrieved {Count} warehouses", response.TotalWarehouses);

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
            logger.LogError(ex, "Error retrieving warehouses");
            return Errors.Stock.SapError(ex.Message);
        }
    }
}
