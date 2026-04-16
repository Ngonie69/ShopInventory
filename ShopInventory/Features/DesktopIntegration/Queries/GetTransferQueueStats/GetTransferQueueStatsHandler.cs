using ErrorOr;
using MediatR;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetTransferQueueStats;

public sealed class GetTransferQueueStatsHandler(
    IInventoryTransferQueueService transferQueueService
) : IRequestHandler<GetTransferQueueStatsQuery, ErrorOr<InventoryTransferQueueStatsDto>>
{
    public async Task<ErrorOr<InventoryTransferQueueStatsDto>> Handle(
        GetTransferQueueStatsQuery query,
        CancellationToken cancellationToken)
    {
        var stats = await transferQueueService.GetQueueStatsAsync(cancellationToken);
        return stats;
    }
}
