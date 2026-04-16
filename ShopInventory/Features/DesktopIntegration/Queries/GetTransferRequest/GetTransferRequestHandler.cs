using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetTransferRequest;

public sealed class GetTransferRequestHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> sapSettings
) : IRequestHandler<GetTransferRequestQuery, ErrorOr<InventoryTransferRequestDto>>
{
    public async Task<ErrorOr<InventoryTransferRequestDto>> Handle(
        GetTransferRequestQuery query,
        CancellationToken cancellationToken)
    {
        if (!sapSettings.Value.Enabled)
            return Errors.DesktopIntegration.SapDisabled;

        var transferRequest = await sapClient.GetInventoryTransferRequestByDocEntryAsync(
            query.DocEntry, cancellationToken);

        if (transferRequest == null)
            return Errors.DesktopIntegration.TransferRequestNotFound(query.DocEntry);

        return transferRequest.ToDto();
    }
}
