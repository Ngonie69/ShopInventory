using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Data;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.CreditNotes.Commands.ApproveCreditNote;

public sealed class ApproveCreditNoteHandler(
    ICreditNoteService creditNoteService,
    IAuditService auditService,
    ILogger<ApproveCreditNoteHandler> logger
) : IRequestHandler<ApproveCreditNoteCommand, ErrorOr<CreditNoteDto>>
{
    public async Task<ErrorOr<CreditNoteDto>> Handle(
        ApproveCreditNoteCommand request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await creditNoteService.ApproveAsync(request.Id);
            if (result is null)
            {
                return Errors.CreditNote.UpdateFailed("Failed to approve credit note.");
            }

            try
            {
                await auditService.LogAsync(AuditActions.ApproveCreditNote, "CreditNote", request.Id.ToString());
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to audit credit note approval {Id}", request.Id);
            }

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error approving credit note {Id}", request.Id);
            return Errors.CreditNote.UpdateFailed("Failed to approve credit note.");
        }
    }
}