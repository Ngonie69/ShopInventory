using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.CreditNoteApprovals.Commands.DecideCreditNoteApproval;

/// <summary>
/// Records a decision on a SAP approval request as the service approver, naming the person who took
/// it in the remarks. <paramref name="Decision"/> is Approved or NotApproved.
/// </summary>
public sealed record DecideCreditNoteApprovalCommand(
    int Code,
    string Decision,
    string? Remarks,
    Guid UserId,
    string Username,
    string? ClientRequestId) : IRequest<ErrorOr<CreditNoteApprovalDecisionResultDto>>;
