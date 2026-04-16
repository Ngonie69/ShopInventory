using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetPagedTransfers;

public sealed class GetPagedTransfersHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> sapSettings
) : IRequestHandler<GetPagedTransfersQuery, ErrorOr<List<InventoryTransferDto>>>
{
    public async Task<ErrorOr<List<InventoryTransferDto>>> Handle(
        GetPagedTransfersQuery query,
        CancellationToken cancellationToken)
    {
        if (!sapSettings.Value.Enabled)
            return Errors.DesktopIntegration.SapDisabled;

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        var transfers = await sapClient.GetPagedInventoryTransfersToWarehouseAsync(
            query.WarehouseCode, page, pageSize, cancellationToken);

        return transfers.ToDto();
    }
}
