using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Sync.Queries.GetCacheStatus;

public sealed class GetCacheStatusHandler(
    ISyncStatusService syncStatusService
) : IRequestHandler<GetCacheStatusQuery, ErrorOr<List<CacheSyncStatusDto>>>
{
    public async Task<ErrorOr<List<CacheSyncStatusDto>>> Handle(
        GetCacheStatusQuery request,
        CancellationToken cancellationToken)
    {
        var dashboard = await syncStatusService.GetSyncStatusDashboardAsync(cancellationToken);
        return dashboard.CacheStatuses ?? new List<CacheSyncStatusDto>();
    }
}
