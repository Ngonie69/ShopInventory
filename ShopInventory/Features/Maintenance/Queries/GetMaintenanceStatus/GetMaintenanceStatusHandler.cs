using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

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
        // Off the snapshot, not the database. Every handset polls this, and so does every web
        // portal circuit, and it has to keep answering while the database is the thing being
        // worked on.
        ErrorOr<MaintenanceStatusDto> result = MaintenanceMapper.ToStatus(
            store.Current, request.Caller, timeProvider.GetUtcNow().UtcDateTime);

        return Task.FromResult(result);
    }
}
