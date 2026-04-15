using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.CreditNotes.Commands.CreateCreditNote;

public sealed class CreateCreditNoteHandler(
    ICreditNoteService creditNoteService,
    IAuditService auditService,
    ILogger<CreateCreditNoteHandler> logger
) : IRequestHandler<CreateCreditNoteCommand, ErrorOr<CreditNoteDto>>
{
    public async Task<ErrorOr<CreditNoteDto>> Handle(
        CreateCreditNoteCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var creditNote = await creditNoteService.CreateAsync(command.Request, command.UserId, cancellationToken);
            try { await auditService.LogAsync(AuditActions.CreateCreditNote, "CreditNote", creditNote.Id.ToString(), $"Credit note created for {command.Request.CardCode}", true); } catch { }
            return creditNote;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating credit note");
            return Errors.CreditNote.CreationFailed(ex.Message);
        }
    }
}
