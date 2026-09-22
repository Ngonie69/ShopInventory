using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Features.AppVersion;

namespace ShopInventory.Features.Maintenance.Queries.GetMaintenanceStatus;

public sealed class GetMaintenanceStatusHandler(
    IMaintenanceStore store,
    TimeProvider timeProvider
) : IRequestHandler<GetMaintenanceStatusQuery, ErrorOr<MaintenanceStatusDto>>
{
    public Task<ErrorOr<MaintenanceStatusDto>> Handle(
        GetMaintenanceStatusQuery request,
        CancellationToken cancellationToken)
    {
        // Off the snapshot, not the database. Every handset polls this, and it has to keep
        // answering while the database is the thing being worked on.
        var policyKey = MobileVersionPolicyAppCatalog.TryResolvePolicyKey(request.AppId, out var resolved)
            ? resolved
            : null;

        ErrorOr<MaintenanceStatusDto> result = MaintenanceMapper.ToStatus(
            store.Current, policyKey, timeProvider.GetUtcNow().UtcDateTime);

        return Task.FromResult(result);
    }
}
