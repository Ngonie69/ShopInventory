using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Data;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.SapTransferApprovals.Commands.AddApprovedSapTransfer;

public sealed class AddApprovedSapTransferHandler(
    ISapTransferApprovalService approvalService,
    IAuditService auditService,
    ILogger<AddApprovedSapTransferHandler> logger)
    : IRequestHandler<AddApprovedSapTransferCommand, ErrorOr<AddApprovedSapTransferResultDto>>
{
    public async Task<ErrorOr<AddApprovedSapTransferResultDto>> Handle(
        AddApprovedSapTransferCommand request,
        CancellationToken cancellationToken)
    {
        try
        {
            var (success, message, value) = await approvalService.AddAsync(request.DraftEntry, request.ClientRequestId);

            await TryAuditAsync(request, success, message);

            if (!success || value is null)
            {
                return Errors.SapTransferApproval.AddFailed(message);
            }

            return value;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error adding SAP transfer draft {DraftEntry}", request.DraftEntry);
            await TryAuditAsync(request, false, ex.Message);
            return Errors.SapTransferApproval.AddFailed("The transfer could not be added in SAP.");
        }
    }

    private async Task TryAuditAsync(AddApprovedSapTransferCommand request, bool success, string message)
    {
        try
        {
            await auditService.LogAsync(
                AuditActions.AddApprovedSapTransfer,
                "SapStockTransferDraft",
                request.DraftEntry.ToString(),
                $"Add the approved SAP transfer draft {request.DraftEntry}. {message}",
                success,
                success ? null : message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to audit the add of SAP transfer draft {DraftEntry}", request.DraftEntry);
        }
    }
}
