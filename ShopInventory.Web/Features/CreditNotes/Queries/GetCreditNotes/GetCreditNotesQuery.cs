using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.CreditNotes.Queries.GetCreditNotes;

public sealed record GetCreditNotesQuery(
    int Page,
    int PageSize,
    CreditNoteStatus? Status,
    string? CardCode,
    DateTime? FromDate,
    DateTime? ToDate) : IRequest<ErrorOr<CreditNoteListResponse>>;