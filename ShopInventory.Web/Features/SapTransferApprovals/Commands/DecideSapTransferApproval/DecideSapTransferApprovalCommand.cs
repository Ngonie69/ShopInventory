using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.SapTransferApprovals.Commands.DecideSapTransferApproval;

/// <summary>
/// Approve or reject a SAP approval request holding a transfer draft: <see cref="SapTransferDecisions.Approved"/>
/// or <see cref="SapTransferDecisions.NotApproved"/>, with an optional remark and a key that makes a repeat
/// replay the first answer.
/// </summary>
public sealed record DecideSapTransferApprovalCommand(int Code, string Decision, string? Remarks, string ClientRequestId)
    : IRequest<ErrorOr<SapTransferApprovalDecisionResultDto>>;
