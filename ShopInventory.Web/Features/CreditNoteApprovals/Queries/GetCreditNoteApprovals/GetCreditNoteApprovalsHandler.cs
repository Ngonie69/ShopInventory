using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.CreditNoteApprovals.Queries.GetCreditNoteApprovals;

public sealed class GetCreditNoteApprovalsHandler(
    ICreditNoteApprovalService approvalService,
    ILogger<GetCreditNoteApprovalsHandler> logger)
    : IRequestHandler<GetCreditNoteApprovalsQuery, ErrorOr<CreditNoteApprovalListResponseDto>>
{
    public async Task<ErrorOr<CreditNoteApprovalListResponseDto>> Handle(
        GetCreditNoteApprovalsQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await approvalService.GetApprovalsAsync(request.Status, request.Page, request.PageSize);
            if (response is null)
            {
                return Errors.CreditNoteApproval.LoadFailed("The held credit notes could not be read from SAP.");
            }

            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading SAP credit note approval requests");
            return Errors.CreditNoteApproval.LoadFailed("The held credit notes could not be read from SAP.");
        }
    }
}
