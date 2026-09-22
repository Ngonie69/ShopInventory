using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Features.AppVersion;

namespace ShopInventory.Features.Maintenance.Queries.GetMobileMaintenanceStatus;

public sealed class GetMobileMaintenanceStatusHandler(
    IMobileMaintenanceStore store,
    TimeProvider timeProvider
) : IRequestHandler<GetMobileMaintenanceStatusQuery, ErrorOr<MobileMaintenanceStatusDto>>
{
    public Task<ErrorOr<MobileMaintenanceStatusDto>> Handle(
        GetMobileMaintenanceStatusQuery request,
        CancellationToken cancellationToken)
    {
        // Off the snapshot, not the database. Every handset polls this, and it has to keep
        // answering while the database is the thing being worked on.
        var policyKey = MobileVersionPolicyAppCatalog.TryResolvePolicyKey(request.AppId, out var resolved)
            ? resolved
            : null;

        ErrorOr<MobileMaintenanceStatusDto> result = MobileMaintenanceMapper.ToStatus(
            store.Current, policyKey, timeProvider.GetUtcNow().UtcDateTime);

        return Task.FromResult(result);
    }
}
