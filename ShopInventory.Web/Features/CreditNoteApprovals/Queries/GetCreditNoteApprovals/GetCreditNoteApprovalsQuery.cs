using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.CreditNoteApprovals.Queries.GetCreditNoteApprovals;

/// <summary><paramref name="Status"/> is open, pending, approved or all; null means open.</summary>
public sealed record GetCreditNoteApprovalsQuery(string? Status, int Page, int PageSize)
    : IRequest<ErrorOr<CreditNoteApprovalListResponseDto>>;
