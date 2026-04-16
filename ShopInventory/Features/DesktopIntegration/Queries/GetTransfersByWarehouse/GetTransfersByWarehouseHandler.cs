using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetTransfersByWarehouse;

public sealed class GetTransfersByWarehouseHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> sapSettings
) : IRequestHandler<GetTransfersByWarehouseQuery, ErrorOr<List<InventoryTransferDto>>>
{
    public async Task<ErrorOr<List<InventoryTransferDto>>> Handle(
        GetTransfersByWarehouseQuery query,
        CancellationToken cancellationToken)
    {
        if (!sapSettings.Value.Enabled)
            return Errors.DesktopIntegration.SapDisabled;

        var transfers = await sapClient.GetInventoryTransfersToWarehouseAsync(
            query.WarehouseCode, cancellationToken);

        return transfers.ToDto();
    }
}
