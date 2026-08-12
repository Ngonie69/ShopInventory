using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.CreditNotes.Queries.GetAllCreditNotes;

public sealed record GetAllCreditNotesQuery(
    int Page,
    int PageSize,
    CreditNoteStatus? Status,
    string? CardCode,
    DateTime? FromDate,
    DateTime? ToDate,

    // Off by default; see ICreditNoteService.GetAllAsync. Only item-level callers need it.
    bool IncludeLines = false
) : IRequest<ErrorOr<CreditNoteListResponseDto>>;
