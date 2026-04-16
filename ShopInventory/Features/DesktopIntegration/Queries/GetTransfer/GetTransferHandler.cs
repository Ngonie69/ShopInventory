using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetTransfer;

public sealed class GetTransferHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> sapSettings
) : IRequestHandler<GetTransferQuery, ErrorOr<InventoryTransferDto>>
{
    public async Task<ErrorOr<InventoryTransferDto>> Handle(
        GetTransferQuery query,
        CancellationToken cancellationToken)
    {
        if (!sapSettings.Value.Enabled)
            return Errors.DesktopIntegration.SapDisabled;

        var transfer = await sapClient.GetInventoryTransferByDocEntryAsync(query.DocEntry, cancellationToken);

        if (transfer == null)
            return Errors.DesktopIntegration.TransferNotFound(query.DocEntry);

        return transfer.ToDto();
    }
}
