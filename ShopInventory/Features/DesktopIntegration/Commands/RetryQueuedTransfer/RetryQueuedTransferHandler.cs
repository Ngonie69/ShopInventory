using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Commands.RetryQueuedTransfer;

public sealed class RetryQueuedTransferHandler(
    IInventoryTransferQueueService transferQueueService,
    ILogger<RetryQueuedTransferHandler> logger
) : IRequestHandler<RetryQueuedTransferCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        RetryQueuedTransferCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var status = await transferQueueService.GetQueueStatusAsync(
                command.ExternalReference, cancellationToken);

            if (status == null)
                return Errors.DesktopIntegration.QueueNotFound(command.ExternalReference);

            if (!status.CanRetry && !status.IsFailed)
                return Errors.DesktopIntegration.ValidationFailed(
                    $"Transfer cannot be retried - current status: {status.Status}");

            var success = await transferQueueService.RetryTransferAsync(
                command.ExternalReference, cancellationToken);

            if (!success)
                return Errors.DesktopIntegration.TransferFailed("Failed to retry transfer");

            return Result.Success;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrying queued transfer {ExternalReference}", command.ExternalReference);
            return Errors.DesktopIntegration.TransferFailed(ex.Message);
        }
    }
}
