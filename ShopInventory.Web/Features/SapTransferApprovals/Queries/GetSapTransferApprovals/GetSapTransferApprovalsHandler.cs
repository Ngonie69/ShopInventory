using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.SapTransferApprovals.Queries.GetSapTransferApprovals;

public sealed class GetSapTransferApprovalsHandler(
    ISapTransferApprovalService approvalService,
    ILogger<GetSapTransferApprovalsHandler> logger)
    : IRequestHandler<GetSapTransferApprovalsQuery, ErrorOr<SapTransferApprovalListResponseDto>>
{
    public async Task<ErrorOr<SapTransferApprovalListResponseDto>> Handle(
        GetSapTransferApprovalsQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var (success, message, response) = await approvalService.GetApprovalsAsync(
                request.Page, request.PageSize, request.BeforeDocEntry, request.Filter);
            if (!success || response is null)
            {
                return Errors.SapTransferApproval.LoadFailed(message);
            }

            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading the transfers SAP is holding");
            return Errors.SapTransferApproval.LoadFailed("The transfers SAP is holding could not be read.");
        }
    }
}
