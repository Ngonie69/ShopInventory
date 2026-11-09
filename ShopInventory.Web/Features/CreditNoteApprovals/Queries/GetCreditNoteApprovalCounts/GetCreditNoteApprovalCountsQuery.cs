using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.CreditNoteApprovals.Queries.GetCreditNoteApprovalCounts;

/// <summary>How many requests each queue holds — the numbers on the page's queue tiles.</summary>
/// <param name="Filter">The list's raised dates and originator, so a tile counts what it opens.</param>
public sealed record GetCreditNoteApprovalCountsQuery(CreditNoteApprovalFilter? Filter = null)
    : IRequest<ErrorOr<CreditNoteApprovalCountsDto>>;
