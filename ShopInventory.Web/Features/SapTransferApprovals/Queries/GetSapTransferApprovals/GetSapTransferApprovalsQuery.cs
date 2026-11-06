using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.SapTransferApprovals.Queries.GetSapTransferApprovals;

/// <summary>One page of the approved transfer drafts waiting to be added.</summary>
/// <param name="Page">Which page — for the range label, and for the first read of a run.</param>
/// <param name="PageSize">How many rows the page holds.</param>
/// <param name="BeforeDocEntry">
/// The previous page's <c>NextCursor</c>. Given, this page continues below that draft rather than
/// counting in from a top that moves whenever a transfer is approved or added.
/// </param>
/// <param name="Warehouse">Only drafts leaving or entering this warehouse.</param>
public sealed record GetSapTransferApprovalsQuery(int Page, int PageSize, int? BeforeDocEntry = null, string? Warehouse = null)
    : IRequest<ErrorOr<SapTransferApprovalListResponseDto>>;
