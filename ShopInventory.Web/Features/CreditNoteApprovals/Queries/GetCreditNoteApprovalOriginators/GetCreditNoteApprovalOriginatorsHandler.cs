using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.CreditNoteApprovals.Queries.GetCreditNoteApprovalOriginators;

public sealed class GetCreditNoteApprovalOriginatorsHandler(
    ICreditNoteApprovalService approvalService,
    ILogger<GetCreditNoteApprovalOriginatorsHandler> logger)
    : IRequestHandler<GetCreditNoteApprovalOriginatorsQuery, ErrorOr<List<CreditNoteApprovalOriginatorDto>>>
{
    public async Task<ErrorOr<List<CreditNoteApprovalOriginatorDto>>> Handle(
        GetCreditNoteApprovalOriginatorsQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var (success, message, originators) = await approvalService.GetOriginatorsAsync();
            if (!success || originators is null)
            {
                return Errors.CreditNoteApproval.LoadFailed(message);
            }

            return originators;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error listing SAP credit note approval originators");
            return Errors.CreditNoteApproval.LoadFailed("The people who raised credit notes could not be read from SAP.");
        }
    }
}
