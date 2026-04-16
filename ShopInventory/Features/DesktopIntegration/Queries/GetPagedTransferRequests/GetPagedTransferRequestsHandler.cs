using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetPagedTransferRequests;

public sealed class GetPagedTransferRequestsHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> sapSettings
) : IRequestHandler<GetPagedTransferRequestsQuery, ErrorOr<List<InventoryTransferRequestDto>>>
{
    public async Task<ErrorOr<List<InventoryTransferRequestDto>>> Handle(
        GetPagedTransferRequestsQuery query,
        CancellationToken cancellationToken)
    {
        if (!sapSettings.Value.Enabled)
            return Errors.DesktopIntegration.SapDisabled;

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        var requests = await sapClient.GetPagedInventoryTransferRequestsAsync(page, pageSize, cancellationToken);

        return requests.Select(r => r.ToDto()).ToList();
    }
}
