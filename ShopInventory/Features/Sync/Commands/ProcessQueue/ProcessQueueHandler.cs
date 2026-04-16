using ErrorOr;
using MediatR;
using ShopInventory.Services;

namespace ShopInventory.Features.Sync.Commands.ProcessQueue;

public sealed class ProcessQueueHandler(
    IOfflineQueueService offlineQueueService
) : IRequestHandler<ProcessQueueCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        ProcessQueueCommand command,
        CancellationToken cancellationToken)
    {
        await offlineQueueService.ProcessQueueAsync(cancellationToken);
        return Result.Success;
    }
}
