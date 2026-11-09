using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.CreditNoteApprovals.Queries.GetCreditNoteApprovalCounts;

public sealed class GetCreditNoteApprovalCountsHandler(
    ICreditNoteApprovalService approvalService,
    ILogger<GetCreditNoteApprovalCountsHandler> logger)
    : IRequestHandler<GetCreditNoteApprovalCountsQuery, ErrorOr<CreditNoteApprovalCountsDto>>
{
    public async Task<ErrorOr<CreditNoteApprovalCountsDto>> Handle(
        GetCreditNoteApprovalCountsQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var (success, message, counts) = await approvalService.GetCountsAsync();
            if (!success || counts is null)
            {
                return Errors.CreditNoteApproval.LoadFailed(message);
            }

            return counts;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error counting SAP credit note approval requests");
            return Errors.CreditNoteApproval.LoadFailed("The queue counts could not be read from SAP.");
        }
    }
}
