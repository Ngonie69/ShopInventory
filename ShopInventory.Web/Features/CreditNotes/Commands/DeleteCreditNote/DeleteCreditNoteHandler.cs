using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Data;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.CreditNotes.Commands.DeleteCreditNote;

public sealed class DeleteCreditNoteHandler(
    ICreditNoteService creditNoteService,
    IAuditService auditService,
    ILogger<DeleteCreditNoteHandler> logger
) : IRequestHandler<DeleteCreditNoteCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        DeleteCreditNoteCommand request,
        CancellationToken cancellationToken)
    {
        try
        {
            var success = await creditNoteService.DeleteCreditNoteAsync(request.Id);
            if (!success)
            {
                return Errors.CreditNote.UpdateFailed("Failed to delete credit note.");
            }

            try
            {
                await auditService.LogAsync(AuditActions.DeleteCreditNote, "CreditNote", request.Id.ToString());
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to audit credit note deletion {Id}", request.Id);
            }

            return Result.Success;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting credit note {Id}", request.Id);
            return Errors.CreditNote.UpdateFailed("Failed to delete credit note.");
        }
    }
}