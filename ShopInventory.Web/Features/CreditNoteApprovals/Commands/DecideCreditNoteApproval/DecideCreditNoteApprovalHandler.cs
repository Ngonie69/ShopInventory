using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Data;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.CreditNoteApprovals.Commands.DecideCreditNoteApproval;

public sealed class DecideCreditNoteApprovalHandler(
    ICreditNoteApprovalService approvalService,
    IAuditService auditService,
    ILogger<DecideCreditNoteApprovalHandler> logger)
    : IRequestHandler<DecideCreditNoteApprovalCommand, ErrorOr<CreditNoteApprovalDecisionResultDto>>
{
    public async Task<ErrorOr<CreditNoteApprovalDecisionResultDto>> Handle(
        DecideCreditNoteApprovalCommand request,
        CancellationToken cancellationToken)
    {
        var approving = string.Equals(request.Decision, "Approved", StringComparison.OrdinalIgnoreCase);
        var action = approving ? AuditActions.ApproveSapCreditNote : AuditActions.RejectSapCreditNote;

        try
        {
            var (success, message, value) = await approvalService.DecideAsync(
                request.Code, request.Decision, request.Remarks, request.ClientRequestId);

            await TryAuditAsync(action, request, success, message);

            if (!success || value is null)
            {
                return Errors.CreditNoteApproval.DecisionFailed(message);
            }

            return value;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deciding SAP credit note approval request {Code}", request.Code);
            await TryAuditAsync(action, request, false, ex.Message);
            return Errors.CreditNoteApproval.DecisionFailed("The decision could not be recorded in SAP.");
        }
    }

    private async Task TryAuditAsync(string action, DecideCreditNoteApprovalCommand request, bool success, string message)
    {
        try
        {
            await auditService.LogAsync(
                action,
                "SapApprovalRequest",
                request.Code.ToString(),
                $"{request.Decision} SAP approval request {request.Code}. {message} Remarks: {request.Remarks}",
                success,
                success ? null : message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to audit the decision on SAP approval request {Code}", request.Code);
        }
    }
}
