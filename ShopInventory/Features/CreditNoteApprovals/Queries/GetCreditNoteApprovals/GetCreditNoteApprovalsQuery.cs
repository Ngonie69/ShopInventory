using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.CreditNoteApprovals.Queries.GetCreditNoteApprovals;

/// <summary>One page of the A/R credit memo approval requests SAP holds.</summary>
/// <param name="Status">
/// One of <see cref="CreditNoteApprovalStatusFilters"/>; empty means open — pending, or approved and
/// not yet added.
/// </param>
/// <param name="Page">Which page, when reading by offset. Advisory when a cursor is given.</param>
/// <param name="PageSize">How many rows the page holds.</param>
/// <param name="BeforeCode">
/// The cursor from the previous page's <c>NextCursor</c>. Given, the page continues below that
/// <c>Code</c>; null reads from the top and <paramref name="Page"/> offsets into the queue.
/// </param>
public sealed record GetCreditNoteApprovalsQuery(string? Status, int Page, int PageSize, int? BeforeCode = null)
    : IRequest<ErrorOr<CreditNoteApprovalListResponseDto>>;
