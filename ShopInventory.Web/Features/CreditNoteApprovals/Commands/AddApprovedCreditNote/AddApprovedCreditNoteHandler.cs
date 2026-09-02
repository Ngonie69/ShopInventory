using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Data;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.CreditNoteApprovals.Commands.AddApprovedCreditNote;

public sealed class AddApprovedCreditNoteHandler(
    ICreditNoteApprovalService approvalService,
    IAuditService auditService,
    ILogger<AddApprovedCreditNoteHandler> logger)
    : IRequestHandler<AddApprovedCreditNoteCommand, ErrorOr<AddApprovedCreditNoteResultDto>>
{
    public async Task<ErrorOr<AddApprovedCreditNoteResultDto>> Handle(
        AddApprovedCreditNoteCommand request,
        CancellationToken cancellationToken)
    {
        try
        {
            var (success, message, value) = await approvalService.AddAsync(request.Code, request.ClientRequestId);

            await TryAuditAsync(request, success, message);

            if (!success || value is null)
            {
                return Errors.CreditNoteApproval.AddFailed(message);
            }

            return value;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error adding the credit note for SAP approval request {Code}", request.Code);
            await TryAuditAsync(request, false, ex.Message);
            return Errors.CreditNoteApproval.AddFailed("The credit note could not be added in SAP.");
        }
    }

    private async Task TryAuditAsync(AddApprovedCreditNoteCommand request, bool success, string message)
    {
        try
        {
            await auditService.LogAsync(
                AuditActions.AddApprovedSapCreditNote,
                "SapApprovalRequest",
                request.Code.ToString(),
                $"Add the approved credit memo draft for SAP approval request {request.Code}. {message}",
                success,
                success ? null : message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to audit the add for SAP approval request {Code}", request.Code);
        }
    }
}
