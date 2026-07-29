using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Data;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.CreditNotes.Commands.CreateCreditNote;

public sealed class CreateCreditNoteHandler(
    ICreditNoteService creditNoteService,
    IAuditService auditService,
    ILogger<CreateCreditNoteHandler> logger
) : IRequestHandler<CreateCreditNoteCommand, ErrorOr<CreditNoteDto>>
{
    public async Task<ErrorOr<CreditNoteDto>> Handle(
        CreateCreditNoteCommand request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await creditNoteService.CreateCreditNoteAsync(request.Request);
            if (result is null)
            {
                await TryAuditFailureAsync(request.Request.CardCode, "Failed to create credit note.");
                return Errors.CreditNote.CreateFailed("Failed to create credit note.");
            }

            try
            {
                await auditService.LogAsync(AuditActions.CreateCreditNote, "CreditNote", result.CreditNoteNumber);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to audit credit note creation {CreditNoteNumber}", result.CreditNoteNumber);
            }

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating credit note");
            await TryAuditFailureAsync(request.Request.CardCode, "Failed to create credit note.");
            return Errors.CreditNote.CreateFailed("Failed to create credit note.");
        }
    }

    private async Task TryAuditFailureAsync(string? cardCode, string message)
    {
        try
        {
            await auditService.LogAsync(AuditActions.CreateCreditNote, "CreditNote", cardCode, null, false, message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to audit failed credit note creation for {CardCode}", cardCode);
        }
    }
}