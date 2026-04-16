using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Sync.Queries.GetHealthSummary;

public sealed class GetHealthSummaryHandler(
    ISyncStatusService syncStatusService
) : IRequestHandler<GetHealthSummaryQuery, ErrorOr<SyncHealthSummaryDto>>
{
    public async Task<ErrorOr<SyncHealthSummaryDto>> Handle(
        GetHealthSummaryQuery request,
        CancellationToken cancellationToken)
    {
        var health = await syncStatusService.GetHealthSummaryAsync(cancellationToken);
        return health;
    }
}
