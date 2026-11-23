using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Data;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.SapTransferApprovals.Commands.DecideSapTransferApproval;

public sealed class DecideSapTransferApprovalHandler(
    ISapTransferApprovalService approvalService,
    IAuditService auditService,
    ILogger<DecideSapTransferApprovalHandler> logger)
    : IRequestHandler<DecideSapTransferApprovalCommand, ErrorOr<SapTransferApprovalDecisionResultDto>>
{
    public async Task<ErrorOr<SapTransferApprovalDecisionResultDto>> Handle(
        DecideSapTransferApprovalCommand request,
        CancellationToken cancellationToken)
    {
        try
        {
            var (success, message, value) = await approvalService.DecideAsync(
                request.Code, request.Decision, request.Remarks, request.ClientRequestId);

            await TryAuditAsync(request, success, message);

            if (!success || value is null)
            {
                return Errors.SapTransferApproval.DecideFailed(message);
            }

            return value;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deciding SAP transfer approval request {Code}", request.Code);
            await TryAuditAsync(request, false, ex.Message);
            return Errors.SapTransferApproval.DecideFailed("The decision could not be recorded in SAP.");
        }
    }

    private async Task TryAuditAsync(DecideSapTransferApprovalCommand request, bool success, string message)
    {
        try
        {
            var approving = request.Decision == SapTransferDecisions.Approved;
            await auditService.LogAsync(
                approving ? AuditActions.ApproveSapTransfer : AuditActions.RejectSapTransfer,
                "SapApprovalRequest",
                request.Code.ToString(),
                $"{request.Decision} SAP transfer approval request {request.Code}. {message}",
                success,
                success ? null : message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to audit the decision on SAP transfer approval request {Code}", request.Code);
        }
    }
}
