using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Commands.RetryQueuedInvoice;

public sealed class RetryQueuedInvoiceHandler(
    IInvoiceQueueService queueService,
    ILogger<RetryQueuedInvoiceHandler> logger
) : IRequestHandler<RetryQueuedInvoiceCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        RetryQueuedInvoiceCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var status = await queueService.GetQueueStatusAsync(command.ExternalReference, cancellationToken);

            if (status == null)
                return Errors.DesktopIntegration.QueueNotFound(command.ExternalReference);

            if (!status.CanRetry && !status.IsFailed)
                return Errors.DesktopIntegration.ValidationFailed(
                    $"Invoice cannot be retried - current status: {status.Status}");

            var success = await queueService.RetryInvoiceAsync(command.ExternalReference, cancellationToken);

            if (!success)
                return Errors.DesktopIntegration.ReservationFailed("Failed to retry invoice");

            return Result.Success;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrying queued invoice {ExternalReference}", command.ExternalReference);
            return Errors.DesktopIntegration.ReservationFailed(ex.Message);
        }
    }
}
