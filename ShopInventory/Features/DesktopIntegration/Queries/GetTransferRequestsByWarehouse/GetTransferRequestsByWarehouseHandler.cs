using ShopInventory.Common;
using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetTransferRequestsByWarehouse;

public sealed class GetTransferRequestsByWarehouseHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> sapSettings
) : IRequestHandler<GetTransferRequestsByWarehouseQuery, ErrorOr<List<InventoryTransferRequestDto>>>
{
    public async Task<ErrorOr<List<InventoryTransferRequestDto>>> Handle(
        GetTransferRequestsByWarehouseQuery query,
        CancellationToken cancellationToken)
    {
        if (!sapSettings.Value.Enabled)
            return Errors.DesktopIntegration.SapDisabled;

        var (from, _) = TransferReadWindow.Resolve(query.FromDate, null, DateTime.Today);
        var requests = await sapClient.GetInventoryTransferRequestsByWarehouseAsync(
            query.WarehouseCode, from, cancellationToken);

        return requests.Select(r => r.ToDto()).ToList();
    }
}
