using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.CreditNoteApprovals.Queries.GetCreditNoteApproval;

public sealed class GetCreditNoteApprovalHandler(
    ICreditNoteApprovalService approvalService,
    ILogger<GetCreditNoteApprovalHandler> logger)
    : IRequestHandler<GetCreditNoteApprovalQuery, ErrorOr<CreditNoteApprovalDetailDto>>
{
    public async Task<ErrorOr<CreditNoteApprovalDetailDto>> Handle(
        GetCreditNoteApprovalQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var detail = await approvalService.GetApprovalAsync(request.Code);
            if (detail is null)
            {
                return Errors.CreditNoteApproval.LoadFailed($"Approval request {request.Code} could not be read from SAP.");
            }

            return detail;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading SAP credit note approval request {Code}", request.Code);
            return Errors.CreditNoteApproval.LoadFailed($"Approval request {request.Code} could not be read from SAP.");
        }
    }
}
