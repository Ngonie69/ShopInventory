using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.SapTransferApprovals.Commands.DecideSapTransferApproval;

/// <summary>
/// Records a decision on the SAP approval request <paramref name="Code"/> holding a transfer or transfer
/// request draft, as the service approver, naming the person who took it in the remarks.
/// <paramref name="Decision"/> is Approved or NotApproved. The role of the account <paramref name="UserId"/>
/// decides which SAP stages they may decide at all, and its warehouses which drafts.
/// </summary>
public sealed record DecideSapTransferApprovalCommand(
    int Code,
    string Decision,
    string? Remarks,
    Guid UserId,
    string Username,
    string? ClientRequestId) : IRequest<ErrorOr<SapTransferApprovalDecisionResultDto>>;
