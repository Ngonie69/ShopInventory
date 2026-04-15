using ErrorOr;
using MediatR;
using ShopInventory.Controllers;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.CreditNotes.Queries.GetCreditNotesByInvoice;

public sealed class GetCreditNotesByInvoiceHandler(
    ICreditNoteService creditNoteService
) : IRequestHandler<GetCreditNotesByInvoiceQuery, ErrorOr<CreditNotesByInvoiceResponse>>
{
    public async Task<ErrorOr<CreditNotesByInvoiceResponse>> Handle(
        GetCreditNotesByInvoiceQuery request,
        CancellationToken cancellationToken)
    {
        var creditNotes = await creditNoteService.GetByInvoiceIdAsync(request.InvoiceId, cancellationToken);
        return new CreditNotesByInvoiceResponse
        {
            InvoiceId = request.InvoiceId,
            HasExistingCreditNotes = creditNotes.Any(),
            TotalCreditedAmount = creditNotes.Sum(cn => cn.DocTotal),
            CreditNotes = creditNotes
        };
    }
}
