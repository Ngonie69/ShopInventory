using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.CreditNotes.Commands.ApproveCreditNote;

public sealed class ApproveCreditNoteHandler(
    ICreditNoteService creditNoteService,
    IAuditService auditService
) : IRequestHandler<ApproveCreditNoteCommand, ErrorOr<CreditNoteDto>>
{
    public async Task<ErrorOr<CreditNoteDto>> Handle(
        ApproveCreditNoteCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var creditNote = await creditNoteService.ApproveAsync(command.Id, command.UserId, cancellationToken);
            try { await auditService.LogAsync(AuditActions.ApproveCreditNote, "CreditNote", command.Id.ToString(), $"Credit note {command.Id} approved", true); } catch { }
            return creditNote;
        }
        catch (InvalidOperationException ex)
        {
            return Errors.CreditNote.InvalidOperation(ex.Message);
        }
    }
}
