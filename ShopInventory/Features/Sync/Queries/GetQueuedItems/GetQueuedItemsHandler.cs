using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Sync.Queries.GetQueuedItems;

public sealed class GetQueuedItemsHandler(
    IOfflineQueueService offlineQueueService
) : IRequestHandler<GetQueuedItemsQuery, ErrorOr<List<QueuedTransactionDto>>>
{
    public async Task<ErrorOr<List<QueuedTransactionDto>>> Handle(
        GetQueuedItemsQuery request,
        CancellationToken cancellationToken)
    {
        var status = await offlineQueueService.GetQueueStatusAsync(cancellationToken);
        return status.PendingTransactions ?? new List<QueuedTransactionDto>();
    }
}
