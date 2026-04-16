using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetTransfersByDateRange;

public sealed class GetTransfersByDateRangeHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> sapSettings
) : IRequestHandler<GetTransfersByDateRangeQuery, ErrorOr<List<InventoryTransferDto>>>
{
    public async Task<ErrorOr<List<InventoryTransferDto>>> Handle(
        GetTransfersByDateRangeQuery query,
        CancellationToken cancellationToken)
    {
        if (!sapSettings.Value.Enabled)
            return Errors.DesktopIntegration.SapDisabled;

        if (query.FromDate > query.ToDate)
            return Errors.DesktopIntegration.ValidationFailed("fromDate must be before or equal to toDate");

        var transfers = await sapClient.GetInventoryTransfersByDateRangeAsync(
            query.WarehouseCode, query.FromDate, query.ToDate, cancellationToken);

        return transfers.ToDto();
    }
}
