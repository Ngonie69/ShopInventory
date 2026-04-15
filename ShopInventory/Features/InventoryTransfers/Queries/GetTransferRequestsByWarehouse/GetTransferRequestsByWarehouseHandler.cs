using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.InventoryTransfers.Queries.GetTransferRequestsByWarehouse;

public sealed class GetTransferRequestsByWarehouseHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetTransferRequestsByWarehouseHandler> logger
) : IRequestHandler<GetTransferRequestsByWarehouseQuery, ErrorOr<TransferRequestListResponseDto>>
{
    public async Task<ErrorOr<TransferRequestListResponseDto>> Handle(
        GetTransferRequestsByWarehouseQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.InventoryTransfer.SapDisabled;

        if (string.IsNullOrWhiteSpace(request.WarehouseCode))
            return Errors.InventoryTransfer.WarehouseCodeRequired;

        try
        {
            var transferRequests = await sapClient.GetInventoryTransferRequestsByWarehouseAsync(request.WarehouseCode, cancellationToken);

            logger.LogInformation("Retrieved {Count} transfer requests to warehouse {Warehouse}", transferRequests.Count, request.WarehouseCode);

            return new TransferRequestListResponseDto
            {
                Warehouse = request.WarehouseCode,
                Count = transferRequests.Count,
                TransferRequests = transferRequests.ToDto()
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Errors.InventoryTransfer.CreationFailed("Request was canceled by the client");
        }
        catch (OperationCanceledException ex)
        {
            logger.LogError(ex, "Timeout retrieving transfer requests by warehouse");
            return Errors.InventoryTransfer.SapTimeout;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return Errors.InventoryTransfer.SapConnectionError(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving transfer requests for warehouse {Warehouse}", request.WarehouseCode);
            return Errors.InventoryTransfer.CreationFailed(ex.Message);
        }
    }
}
