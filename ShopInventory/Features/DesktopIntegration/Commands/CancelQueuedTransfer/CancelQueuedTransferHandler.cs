using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Commands.CancelQueuedTransfer;

public sealed class CancelQueuedTransferHandler(
    IInventoryTransferQueueService transferQueueService,
    ILogger<CancelQueuedTransferHandler> logger
) : IRequestHandler<CancelQueuedTransferCommand, ErrorOr<Deleted>>
{
    public async Task<ErrorOr<Deleted>> Handle(
        CancelQueuedTransferCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var status = await transferQueueService.GetQueueStatusAsync(
                command.ExternalReference, cancellationToken);

            if (status == null)
                return Errors.DesktopIntegration.QueueNotFound(command.ExternalReference);

            if (!status.CanCancel)
                return Errors.DesktopIntegration.CancellationFailed(
                    $"Transfer cannot be cancelled - current status: {status.Status}");

            var success = await transferQueueService.CancelQueuedTransferAsync(
                command.ExternalReference, command.CancelledBy, cancellationToken);

            if (!success)
                return Errors.DesktopIntegration.CancellationFailed("Failed to cancel queued transfer");

            return Result.Deleted;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error cancelling queued transfer {ExternalReference}", command.ExternalReference);
            return Errors.DesktopIntegration.CancellationFailed(ex.Message);
        }
    }
}
