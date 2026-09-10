using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Commands.RetryQueuedInvoice;

/// <summary>
/// Puts a failed queue entry back in line for SAP.
/// </summary>
/// <remarks>
/// Audited because a retry is a person deciding to send a document to SAP again, and the queue entry
/// afterwards looks the same whether it was retried once or six times. The row carries the status it
/// was retried from, which is the only place the count survives.
/// </remarks>
public sealed class RetryQueuedInvoiceHandler(
    IInvoiceQueueService queueService,
    IAuditService auditService,
    ILogger<RetryQueuedInvoiceHandler> logger
) : IRequestHandler<RetryQueuedInvoiceCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        RetryQueuedInvoiceCommand command,
        CancellationToken cancellationToken)
    {
        var (outcome, details) = await RetryAsync(command, cancellationToken);

        await auditService.LogAsync(
            AuditActions.RetryQueuedDesktopInvoice,
            "InvoiceQueue",
            command.ExternalReference,
            details,
            !outcome.IsError,
            outcome.IsError ? outcome.FirstError.Description : null);

        return outcome;
    }

    private async Task<(ErrorOr<Success> Outcome, string Details)> RetryAsync(
        RetryQueuedInvoiceCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var status = await queueService.GetQueueStatusAsync(command.ExternalReference, cancellationToken);

            if (status == null)
                return (Errors.DesktopIntegration.QueueNotFound(command.ExternalReference),
                    $"No queue entry for {command.ExternalReference}.");

            if (!status.CanRetry && !status.IsFailed)
                return (Errors.DesktopIntegration.ValidationFailed(
                        $"Invoice cannot be retried - current status: {status.Status}"),
                    $"Queue entry {command.ExternalReference} was not retryable from status {status.Status}.");

            var success = await queueService.RetryInvoiceAsync(command.ExternalReference, cancellationToken);

            if (!success)
                return (Errors.DesktopIntegration.ReservationFailed("Failed to retry invoice"),
                    $"Queue entry {command.ExternalReference} could not be requeued from status {status.Status}.");

            return (Result.Success,
                $"Queue entry {command.ExternalReference} requeued from status {status.Status}.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrying queued invoice {ExternalReference}", command.ExternalReference);
            return (Errors.DesktopIntegration.ReservationFailed(ex.Message),
                $"Retrying queue entry {command.ExternalReference} threw.");
        }
    }
}
