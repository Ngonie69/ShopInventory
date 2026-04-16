using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Services;

namespace ShopInventory.Features.Sync.Commands.CancelTransaction;

public sealed class CancelTransactionHandler(
    IOfflineQueueService offlineQueueService
) : IRequestHandler<CancelTransactionCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        CancelTransactionCommand command,
        CancellationToken cancellationToken)
    {
        var success = await offlineQueueService.CancelTransactionAsync(command.Id, cancellationToken);
        if (!success)
        {
            return Errors.Sync.TransactionNotFound(command.Id);
        }
        return Result.Success;
    }
}
