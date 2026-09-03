using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.CreditNoteApprovals.Queries.GetCreditNoteApprovals;

/// <summary>One page of the queue.</summary>
/// <param name="Status">Open, pending, approved or all; null means open.</param>
/// <param name="Page">Which page — for the range label, and for the first read of a run.</param>
/// <param name="PageSize">How many rows the page holds.</param>
/// <param name="BeforeCode">
/// The previous page's <c>NextCursor</c>. Given, this page continues below that request code rather
/// than counting in from a top that moves whenever a credit memo is raised.
/// </param>
public sealed record GetCreditNoteApprovalsQuery(string? Status, int Page, int PageSize, int? BeforeCode = null)
    : IRequest<ErrorOr<CreditNoteApprovalListResponseDto>>;
