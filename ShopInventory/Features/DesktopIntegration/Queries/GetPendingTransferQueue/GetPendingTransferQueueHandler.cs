using ErrorOr;
using MediatR;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetPendingTransferQueue;

public sealed class GetPendingTransferQueueHandler(
    IInventoryTransferQueueService transferQueueService
) : IRequestHandler<GetPendingTransferQueueQuery, ErrorOr<List<InventoryTransferQueueStatusDto>>>
{
    public async Task<ErrorOr<List<InventoryTransferQueueStatusDto>>> Handle(
        GetPendingTransferQueueQuery query,
        CancellationToken cancellationToken)
    {
        var pending = await transferQueueService.GetPendingTransfersAsync(
            query.SourceSystem, query.Limit, cancellationToken);

        return pending;
    }
}
