using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.CreditNoteApprovals.Queries.GetCreditNoteApprovalCounts;

/// <summary>How many requests each queue holds — the numbers on the page's queue tiles.</summary>
public sealed record GetCreditNoteApprovalCountsQuery : IRequest<ErrorOr<CreditNoteApprovalCountsDto>>;
