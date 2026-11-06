using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.SapTransferApprovals.Queries.GetSapTransferApproval;

public sealed class GetSapTransferApprovalHandler(
    ISapTransferApprovalService approvalService,
    ILogger<GetSapTransferApprovalHandler> logger)
    : IRequestHandler<GetSapTransferApprovalQuery, ErrorOr<SapTransferApprovalDetailDto>>
{
    public async Task<ErrorOr<SapTransferApprovalDetailDto>> Handle(
        GetSapTransferApprovalQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var (success, message, detail) = await approvalService.GetApprovalAsync(request.DraftEntry);
            if (!success || detail is null)
            {
                return Errors.SapTransferApproval.LoadFailed(message);
            }

            return detail;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading SAP transfer draft {DraftEntry}", request.DraftEntry);
            return Errors.SapTransferApproval.LoadFailed($"Transfer draft {request.DraftEntry} could not be read from SAP.");
        }
    }
}
