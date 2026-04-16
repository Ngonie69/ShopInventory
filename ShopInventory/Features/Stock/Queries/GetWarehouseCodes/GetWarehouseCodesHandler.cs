using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Services;

namespace ShopInventory.Features.Stock.Queries.GetWarehouseCodes;

public sealed class GetWarehouseCodesHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetWarehouseCodesHandler> logger
) : IRequestHandler<GetWarehouseCodesQuery, ErrorOr<List<string>>>
{
    public async Task<ErrorOr<List<string>>> Handle(
        GetWarehouseCodesQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.Stock.SapDisabled;

        try
        {
            var warehouses = await sapClient.GetWarehousesAsync(cancellationToken);
            var warehouseCodes = warehouses
                .Where(w => request.IncludeInactive || w.IsActive)
                .Select(w => w.WarehouseCode)
                .ToList();

            logger.LogInformation("Retrieved {Count} warehouse codes (includeInactive: {IncludeInactive})",
                warehouseCodes.Count, request.IncludeInactive);

            return warehouseCodes;
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
            logger.LogError(ex, "Error retrieving warehouse codes");
            return Errors.Stock.SapError(ex.Message);
        }
    }
}
