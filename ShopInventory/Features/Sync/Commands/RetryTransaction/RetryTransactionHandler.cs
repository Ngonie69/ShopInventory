using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Services;

namespace ShopInventory.Features.Sync.Commands.RetryTransaction;

public sealed class RetryTransactionHandler(
    IOfflineQueueService offlineQueueService
) : IRequestHandler<RetryTransactionCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        RetryTransactionCommand command,
        CancellationToken cancellationToken)
    {
        var success = await offlineQueueService.RetryTransactionAsync(command.Id, cancellationToken);
        if (!success)
        {
            return Errors.Sync.TransactionNotFound(command.Id);
        }
        return Result.Success;
    }
}
