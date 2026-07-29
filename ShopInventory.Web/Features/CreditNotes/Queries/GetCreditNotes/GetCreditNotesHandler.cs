using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.CreditNotes.Queries.GetCreditNotes;

public sealed class GetCreditNotesHandler(
    ICreditNoteService creditNoteService,
    ILogger<GetCreditNotesHandler> logger
) : IRequestHandler<GetCreditNotesQuery, ErrorOr<CreditNoteListResponse>>
{
    public async Task<ErrorOr<CreditNoteListResponse>> Handle(
        GetCreditNotesQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await creditNoteService.GetCreditNotesAsync(
                request.Page,
                request.PageSize,
                request.Status,
                request.CardCode,
                request.FromDate,
                request.ToDate);

            if (response is null)
            {
                return Errors.CreditNote.LoadFailed("Failed to load credit notes.");
            }

            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading credit notes in web CQRS handler");
            return Errors.CreditNote.LoadFailed("Failed to load credit notes.");
        }
    }
}