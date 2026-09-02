using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.CreditNoteApprovals.Commands.DecideCreditNoteApproval;

/// <summary>
/// Approve or reject a SAP-held credit memo. <paramref name="ClientRequestId"/> is new per click, so a
/// retry of a call that timed out replays its answer instead of reaching SAP twice.
/// </summary>
public sealed record DecideCreditNoteApprovalCommand(int Code, string Decision, string? Remarks, string ClientRequestId)
    : IRequest<ErrorOr<CreditNoteApprovalDecisionResultDto>>;
