using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.InventoryTransfers.Queries.GetTransfersByDate;

public sealed class GetTransfersByDateHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetTransfersByDateHandler> logger
) : IRequestHandler<GetTransfersByDateQuery, ErrorOr<InventoryTransferDateResponseDto>>
{
    public async Task<ErrorOr<InventoryTransferDateResponseDto>> Handle(
        GetTransfersByDateQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.InventoryTransfer.SapDisabled;

        if (string.IsNullOrWhiteSpace(request.WarehouseCode))
            return Errors.InventoryTransfer.WarehouseCodeRequired;

        if (!DateTime.TryParse(request.Date, out DateTime parsedDate))
            return Errors.InventoryTransfer.InvalidDateFormat("Date");

        try
        {
            var transfers = await sapClient.GetInventoryTransfersByDateAsync(request.WarehouseCode, parsedDate, cancellationToken);

            logger.LogInformation("Retrieved {Count} inventory transfers to warehouse {Warehouse} for date {Date}",
                transfers.Count, request.WarehouseCode, request.Date);

            return new InventoryTransferDateResponseDto
            {
                Warehouse = request.WarehouseCode,
                Date = parsedDate.ToString("yyyy-MM-dd"),
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
            logger.LogError(ex, "Timeout retrieving transfers by date");
            return Errors.InventoryTransfer.SapTimeout;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return Errors.InventoryTransfer.SapConnectionError(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving inventory transfers for warehouse {Warehouse} on date {Date}", request.WarehouseCode, request.Date);
            return Errors.InventoryTransfer.CreationFailed(ex.Message);
        }
    }
}
