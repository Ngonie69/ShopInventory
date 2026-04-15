using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.InventoryTransfers.Queries.GetTransfersByWarehouse;

public sealed class GetTransfersByWarehouseHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetTransfersByWarehouseHandler> logger
) : IRequestHandler<GetTransfersByWarehouseQuery, ErrorOr<InventoryTransferListResponseDto>>
{
    public async Task<ErrorOr<InventoryTransferListResponseDto>> Handle(
        GetTransfersByWarehouseQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.InventoryTransfer.SapDisabled;

        if (string.IsNullOrWhiteSpace(request.WarehouseCode))
            return Errors.InventoryTransfer.WarehouseCodeRequired;

        try
        {
            var transfers = await sapClient.GetInventoryTransfersToWarehouseAsync(request.WarehouseCode, cancellationToken);

            logger.LogInformation("Retrieved {Count} inventory transfers to warehouse {Warehouse}", transfers.Count, request.WarehouseCode);

            return new InventoryTransferListResponseDto
            {
                Warehouse = request.WarehouseCode,
                Count = transfers.Count,
                Transfers = transfers.ToDto()
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Errors.InventoryTransfer.CreationFailed("Request was canceled by the client");
        }
        catch (OperationCanceledException ex)
        {
            logger.LogError(ex, "Timeout retrieving inventory transfers");
            return Errors.InventoryTransfer.SapTimeout;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return Errors.InventoryTransfer.SapConnectionError(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving inventory transfers for warehouse {Warehouse}", request.WarehouseCode);
            return Errors.InventoryTransfer.CreationFailed(ex.Message);
        }
    }
}
