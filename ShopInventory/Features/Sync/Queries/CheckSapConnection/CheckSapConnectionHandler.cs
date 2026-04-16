using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Sync.Queries.CheckSapConnection;

public sealed class CheckSapConnectionHandler(
    ISyncStatusService syncStatusService
) : IRequestHandler<CheckSapConnectionQuery, ErrorOr<SapConnectionStatusDto>>
{
    public async Task<ErrorOr<SapConnectionStatusDto>> Handle(
        CheckSapConnectionQuery request,
        CancellationToken cancellationToken)
    {
        var status = await syncStatusService.CheckSapConnectionAsync(cancellationToken);
        return status;
    }
}
