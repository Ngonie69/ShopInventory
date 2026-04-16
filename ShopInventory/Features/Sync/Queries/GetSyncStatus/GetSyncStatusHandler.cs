using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Sync.Queries.GetSyncStatus;

public sealed class GetSyncStatusHandler(
    ISyncStatusService syncStatusService
) : IRequestHandler<GetSyncStatusQuery, ErrorOr<SyncStatusDashboardDto>>
{
    public async Task<ErrorOr<SyncStatusDashboardDto>> Handle(
        GetSyncStatusQuery request,
        CancellationToken cancellationToken)
    {
        var status = await syncStatusService.GetSyncStatusDashboardAsync(cancellationToken);
        return status;
    }
}
