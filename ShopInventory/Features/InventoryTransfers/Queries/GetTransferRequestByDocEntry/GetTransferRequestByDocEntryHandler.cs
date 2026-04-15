using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.InventoryTransfers.Queries.GetTransferRequestByDocEntry;

public sealed class GetTransferRequestByDocEntryHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetTransferRequestByDocEntryHandler> logger
) : IRequestHandler<GetTransferRequestByDocEntryQuery, ErrorOr<InventoryTransferRequestDto>>
{
    public async Task<ErrorOr<InventoryTransferRequestDto>> Handle(
        GetTransferRequestByDocEntryQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.InventoryTransfer.SapDisabled;

        try
        {
            var transferRequest = await sapClient.GetInventoryTransferRequestByDocEntryAsync(request.DocEntry, cancellationToken);
            if (transferRequest is null)
                return Errors.InventoryTransfer.TransferRequestNotFound(request.DocEntry);

            return transferRequest.ToDto();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving transfer request {DocEntry}", request.DocEntry);
            return Errors.InventoryTransfer.CreationFailed(ex.Message);
        }
    }
}
