using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.CreditNotes.Queries.GetAllCreditNotes;

public sealed class GetAllCreditNotesHandler(
    ICreditNoteService creditNoteService
) : IRequestHandler<GetAllCreditNotesQuery, ErrorOr<CreditNoteListResponseDto>>
{
    public async Task<ErrorOr<CreditNoteListResponseDto>> Handle(
        GetAllCreditNotesQuery request,
        CancellationToken cancellationToken)
    {
        var result = await creditNoteService.GetAllAsync(
            request.Page, request.PageSize, request.Status, request.CardCode,
            request.FromDate, request.ToDate, cancellationToken);
        return result;
    }
}
