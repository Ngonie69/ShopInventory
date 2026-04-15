using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.InventoryTransfers.Queries.GetTransferByDocEntry;

public sealed class GetTransferByDocEntryHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetTransferByDocEntryHandler> logger
) : IRequestHandler<GetTransferByDocEntryQuery, ErrorOr<InventoryTransferDto>>
{
    public async Task<ErrorOr<InventoryTransferDto>> Handle(
        GetTransferByDocEntryQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.InventoryTransfer.SapDisabled;

        try
        {
            var transfer = await sapClient.GetInventoryTransferByDocEntryAsync(request.DocEntry, cancellationToken);
            if (transfer is null)
                return Errors.InventoryTransfer.NotFound(request.DocEntry);

            return transfer.ToDto();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving inventory transfer {DocEntry}", request.DocEntry);
            return Errors.InventoryTransfer.CreationFailed(ex.Message);
        }
    }
}
