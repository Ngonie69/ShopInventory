using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.CreditNotes.Commands.DeleteCreditNote;

public sealed class DeleteCreditNoteHandler(
    ICreditNoteService creditNoteService,
    IAuditService auditService
) : IRequestHandler<DeleteCreditNoteCommand, ErrorOr<Deleted>>
{
    public async Task<ErrorOr<Deleted>> Handle(
        DeleteCreditNoteCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var deleted = await creditNoteService.DeleteAsync(command.Id, cancellationToken);
            if (!deleted)
                return Errors.CreditNote.NotFound(command.Id);

            try { await auditService.LogAsync(AuditActions.DeleteCreditNote, "CreditNote", command.Id.ToString(), $"Credit note {command.Id} deleted", true); } catch { }
            return Result.Deleted;
        }
        catch (InvalidOperationException ex)
        {
            return Errors.CreditNote.InvalidOperation(ex.Message);
        }
    }
}
