using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.CreditNotes.Commands.UpdateCreditNoteStatus;

public sealed class UpdateCreditNoteStatusHandler(
    ICreditNoteService creditNoteService
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
            return creditNote;
        }
        catch (InvalidOperationException ex)
        {
            return Errors.CreditNote.InvalidOperation(ex.Message);
        }
    }
}
