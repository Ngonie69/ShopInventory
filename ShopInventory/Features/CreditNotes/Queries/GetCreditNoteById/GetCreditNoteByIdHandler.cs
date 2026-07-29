using ErrorOr;
using MediatR;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.CreditNotes.Queries.GetCreditNoteById;

public sealed class GetCreditNoteByIdHandler(
    ApplicationDbContext dbContext,
    ICreditNoteService creditNoteService
) : IRequestHandler<GetCreditNoteByIdQuery, ErrorOr<CreditNoteDto>>
{
    public async Task<ErrorOr<CreditNoteDto>> Handle(
        GetCreditNoteByIdQuery request,
        CancellationToken cancellationToken)
    {
        var creditNote = await creditNoteService.GetByIdAsync(request.Id, cancellationToken);
        if (creditNote is null)
            return Errors.CreditNote.NotFound(request.Id);

        await FiscalDocumentStatusProjector.EnrichCreditNoteAsync(dbContext, creditNote, cancellationToken);

        return creditNote;
    }
}
