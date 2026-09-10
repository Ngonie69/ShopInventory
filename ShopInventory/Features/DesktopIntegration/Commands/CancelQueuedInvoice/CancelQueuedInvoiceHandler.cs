using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Commands.CancelQueuedInvoice;

/// <summary>
/// Drops a queued invoice and releases the stock it was holding.
/// </summary>
/// <remarks>
/// Audited whatever the outcome. Cancelling is the one thing on the queue that destroys work — the
/// entry goes, the reservation goes, and what is left behind says only that an invoice which was
/// once expected never arrived. The row records the status it was cancelled from, because "cancelled
/// a pending one" and "cancelled one that had already failed against SAP" are different events and
/// the queue keeps neither afterwards.
/// </remarks>
public sealed class CancelQueuedInvoiceHandler(
    IInvoiceQueueService queueService,
    IStockReservationService reservationService,
    IAuditService auditService,
    ILogger<CancelQueuedInvoiceHandler> logger
) : IRequestHandler<CancelQueuedInvoiceCommand, ErrorOr<Deleted>>
{
    public async Task<ErrorOr<Deleted>> Handle(
        CancelQueuedInvoiceCommand command,
        CancellationToken cancellationToken)
    {
        var (outcome, details) = await CancelAsync(command, cancellationToken);

        await auditService.LogAsync(
            AuditActions.CancelQueuedDesktopInvoice,
            "InvoiceQueue",
            command.ExternalReference,
            details,
            !outcome.IsError,
            outcome.IsError ? outcome.FirstError.Description : null);

        return outcome;
    }

    private async Task<(ErrorOr<Deleted> Outcome, string Details)> CancelAsync(
        CancelQueuedInvoiceCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var status = await queueService.GetQueueStatusAsync(command.ExternalReference, cancellationToken);

            if (status == null)
                return (Errors.DesktopIntegration.QueueNotFound(command.ExternalReference),
                    $"No queue entry for {command.ExternalReference}.");

            if (!status.CanCancel)
                return (Errors.DesktopIntegration.CancellationFailed(
                        $"Invoice cannot be cancelled - current status: {status.Status}"),
                    $"Queue entry {command.ExternalReference} was not cancellable from status {status.Status}.");

            var success = await queueService.CancelQueuedInvoiceAsync(
                command.ExternalReference, command.CancelledBy, cancellationToken);

            if (!success)
                return (Errors.DesktopIntegration.CancellationFailed("Failed to cancel queued invoice"),
                    $"Queue entry {command.ExternalReference} could not be cancelled from status {status.Status}.");

            // Also cancel the reservation
            await reservationService.CancelReservationAsync(new CancelReservationRequest
            {
                ReservationId = status.ReservationId,
                Reason = "Queued invoice cancelled"
            }, cancellationToken);

            return (Result.Deleted,
                $"Queue entry {command.ExternalReference} cancelled from status {status.Status}, "
                + $"releasing reservation {status.ReservationId}.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error cancelling queued invoice {ExternalReference}", command.ExternalReference);
            return (Errors.DesktopIntegration.CancellationFailed(ex.Message),
                $"Cancelling queue entry {command.ExternalReference} threw.");
        }
    }
}
