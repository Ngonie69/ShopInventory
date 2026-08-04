using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.CreditNotes.Commands.UpdateCreditNoteStatus;

public sealed class UpdateCreditNoteStatusHandler(
    ICreditNoteService creditNoteService,
    INotificationService notificationService,
    ILogger<UpdateCreditNoteStatusHandler> logger
) : IRequestHandler<UpdateCreditNoteStatusCommand, ErrorOr<CreditNoteDto>>
{
    public async Task<ErrorOr<CreditNoteDto>> Handle(
        UpdateCreditNoteStatusCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var creditNote = await creditNoteService.UpdateStatusAsync(
                command.Id, command.Status, command.UserId, cancellationToken);

            try
            {
                await notificationService.CreateNotificationAsync(
                    CreditNoteNotificationFactory.CreateStatusChangedNotification(creditNote),
                    cancellationToken);
            }
            catch (Exception notificationException)
            {
                logger.LogWarning(
                    notificationException,
                    "Failed to publish credit note status notification for {CreditNoteId}",
                    command.Id);
            }

            return creditNote;
        }
        catch (InvalidOperationException ex)
        {
            return Errors.CreditNote.InvalidOperation(ex.Message);
        }
    }
}
