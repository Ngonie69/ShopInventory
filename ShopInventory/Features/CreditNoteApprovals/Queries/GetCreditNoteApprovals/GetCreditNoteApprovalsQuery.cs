using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.CreditNoteApprovals.Queries.GetCreditNoteApprovals;

/// <summary>
/// One page of the A/R credit memo approval requests SAP holds. <paramref name="Status"/> is one of
/// <see cref="CreditNoteApprovalStatusFilters"/>; empty means open (pending or approved-not-added).
/// </summary>
public sealed record GetCreditNoteApprovalsQuery(string? Status, int Page, int PageSize)
    : IRequest<ErrorOr<CreditNoteApprovalListResponseDto>>;
