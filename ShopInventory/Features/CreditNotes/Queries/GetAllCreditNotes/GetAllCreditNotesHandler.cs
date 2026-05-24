using ErrorOr;
using MediatR;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.CreditNotes.Queries.GetAllCreditNotes;

public sealed class GetAllCreditNotesHandler(
    ApplicationDbContext dbContext,
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

        await FiscalDocumentStatusProjector.EnrichCreditNotesAsync(dbContext, result.CreditNotes, cancellationToken);

        return result;
    }
}
