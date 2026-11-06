using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.SapTransferApprovals.Queries.GetSapTransferApproval;

/// <summary>One held transfer draft with its lines, batch picks, approvals and the request it was copied from.</summary>
public sealed record GetSapTransferApprovalQuery(int DraftEntry, Guid? CallerUserId = null)
    : IRequest<ErrorOr<SapTransferApprovalDetailDto>>;
