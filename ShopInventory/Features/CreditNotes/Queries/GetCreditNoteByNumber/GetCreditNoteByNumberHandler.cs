using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.CreditNotes.Queries.GetCreditNoteByNumber;

public sealed class GetCreditNoteByNumberHandler(
    ICreditNoteService creditNoteService
) : IRequestHandler<GetCreditNoteByNumberQuery, ErrorOr<CreditNoteDto>>
{
    public async Task<ErrorOr<CreditNoteDto>> Handle(
        GetCreditNoteByNumberQuery request,
        CancellationToken cancellationToken)
    {
        var creditNote = await creditNoteService.GetByCreditNoteNumberAsync(request.CreditNoteNumber, cancellationToken);
        if (creditNote is null)
            return Errors.CreditNote.NotFoundByNumber(request.CreditNoteNumber);

        return creditNote;
    }
}
