using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.CreditNotes.Commands.CreateCreditNoteFromInvoice;

public sealed class CreateCreditNoteFromInvoiceHandler(
    ICreditNoteService creditNoteService,
    IAuditService auditService,
    ILogger<CreateCreditNoteFromInvoiceHandler> logger
) : IRequestHandler<CreateCreditNoteFromInvoiceCommand, ErrorOr<CreditNoteDto>>
{
    public async Task<ErrorOr<CreditNoteDto>> Handle(
        CreateCreditNoteFromInvoiceCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var lines = command.Request.Lines?.Select(l => new CreateCreditNoteLineRequest
            {
                ItemCode = l.ItemCode ?? "",
                ItemDescription = l.ItemDescription,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice,
                DiscountPercent = l.DiscountPercent,
                TaxPercent = l.TaxPercent,
                WarehouseCode = l.WarehouseCode,
                ReturnReason = l.ReturnReason,
                OriginalInvoiceLineId = l.OriginalInvoiceLineId
            }).ToList() ?? new List<CreateCreditNoteLineRequest>();

            var creditNote = await creditNoteService.CreateFromInvoiceAsync(
                command.InvoiceId, lines, command.Request.Reason ?? "", command.UserId, cancellationToken);
            try { await auditService.LogAsync(AuditActions.CreateCreditNote, "CreditNote", creditNote.Id.ToString(), $"Credit note created from invoice {command.InvoiceId}", true); } catch { }
            return creditNote;
        }
        catch (InvalidOperationException ex)
        {
            return Errors.CreditNote.InvalidOperation(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating credit note from invoice {InvoiceId}", command.InvoiceId);
            return Errors.CreditNote.CreationFailed(ex.Message);
        }
    }
}
