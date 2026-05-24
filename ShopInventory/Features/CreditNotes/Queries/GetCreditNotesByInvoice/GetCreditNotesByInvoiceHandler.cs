using ErrorOr;
using MediatR;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Controllers;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.CreditNotes.Queries.GetCreditNotesByInvoice;

public sealed class GetCreditNotesByInvoiceHandler(
    ApplicationDbContext dbContext,
    ICreditNoteService creditNoteService
) : IRequestHandler<GetCreditNotesByInvoiceQuery, ErrorOr<CreditNotesByInvoiceResponse>>
{
    public async Task<ErrorOr<CreditNotesByInvoiceResponse>> Handle(
        GetCreditNotesByInvoiceQuery request,
        CancellationToken cancellationToken)
    {
        var creditNotes = await creditNoteService.GetByInvoiceIdAsync(request.InvoiceId, cancellationToken);

        await FiscalDocumentStatusProjector.EnrichCreditNotesAsync(dbContext, creditNotes, cancellationToken);

        return new CreditNotesByInvoiceResponse
        {
            InvoiceId = request.InvoiceId,
            HasExistingCreditNotes = creditNotes.Any(),
            TotalCreditedAmount = creditNotes.Sum(cn => cn.DocTotal),
            CreditNotes = creditNotes
        };
    }
}
