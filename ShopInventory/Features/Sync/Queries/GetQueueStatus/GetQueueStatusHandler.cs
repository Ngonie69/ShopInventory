using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Sync.Queries.GetQueueStatus;

public sealed class GetQueueStatusHandler(
    IOfflineQueueService offlineQueueService
) : IRequestHandler<GetQueueStatusQuery, ErrorOr<OfflineQueueStatusDto>>
{
    public async Task<ErrorOr<OfflineQueueStatusDto>> Handle(
        GetQueueStatusQuery request,
        CancellationToken cancellationToken)
    {
        var status = await offlineQueueService.GetQueueStatusAsync(cancellationToken);
        return status;
    }
}
