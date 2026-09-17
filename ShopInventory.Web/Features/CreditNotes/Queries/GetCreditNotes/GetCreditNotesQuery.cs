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
    DateTime? ToDate,

    // True: only credit notes against van invoices (/van-sales-credit-notes). False: none of them
    // (/credit-notes). Null: no van filter. The API decides which is which.
    bool? VanSalesOnly = null) : IRequest<ErrorOr<CreditNoteListResponse>>;