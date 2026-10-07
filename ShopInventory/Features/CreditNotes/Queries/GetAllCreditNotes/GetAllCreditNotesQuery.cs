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
    bool IncludeLines = false,

    // True: only credit notes against van invoices. False: none of them. Null: no van filter.
    // The rule is VanSaleCreditNotes'.
    bool? VanSalesOnly = null,

    // The Credit Notes page's other filters and its sort. Set, the whole date range is read, filtered,
    // sorted and then paged here, so the page can ask for one page at a time.
    CreditNoteListOptions? ListOptions = null
): IRequest<ErrorOr<CreditNoteListResponseDto>>;

/// <summary>
/// The Credit Notes page's filters beyond status and dates, matched as the page matched them in memory.
/// </summary>
/// <param name="CreditNoteNumber">Part of the credit note number, ignoring case.</param>
/// <param name="Customer">Part of the customer code or name, ignoring case.</param>
/// <param name="FiscalStatus">"Fiscalised", "Not Fiscalised" or "Unknown"; anything else reads as Unknown.</param>
/// <param name="SortBy">The column to sort by.</param>
/// <param name="SortDescending">Sort direction. The SAP document entry, descending, breaks ties.</param>
public sealed record CreditNoteListOptions(
    string? CreditNoteNumber = null,
    string? Customer = null,
    string? FiscalStatus = null,
    CreditNoteListSort SortBy = CreditNoteListSort.Number,
    bool SortDescending = true);

/// <summary>The Credit Notes sortable columns.</summary>
public enum CreditNoteListSort
{
    Number,
    Date,
    Total
}
