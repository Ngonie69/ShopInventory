using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.CreditNoteApprovals.Queries.GetCreditNoteApprovalCounts;

/// <summary>
/// Three <c>$count</c> reads, one per queue, through the same filter the list uses, so a tile and the
/// list it opens always agree.
/// </summary>
/// <remarks>
/// Read one after another, not together. Counting the whole request table is not quick, the page
/// asks for these after its list has already answered, and the application has six SAP connections
/// in all; three at once beside somebody's list read would take half of them for a number nobody is
/// waiting on.
/// </remarks>
public sealed class GetCreditNoteApprovalCountsHandler(
    ISAPServiceLayerClient sap,
    ICreditNoteApprovalStageScope stageScope,
    IOptions<SAPSettings> sapSettings)
    : IRequestHandler<GetCreditNoteApprovalCountsQuery, ErrorOr<CreditNoteApprovalCountsDto>>
{
    public async Task<ErrorOr<CreditNoteApprovalCountsDto>> Handle(
        GetCreditNoteApprovalCountsQuery query,
        CancellationToken cancellationToken)
    {
        if (!sapSettings.Value.Enabled)
        {
            return Errors.CreditNoteApproval.SapDisabled;
        }

        var scope = await stageScope.ResolveAsync(query.CallerUserId, cancellationToken);
        if (scope.IsError)
        {
            return scope.Errors;
        }

        var stages = scope.Value.StageCodes;

        return new CreditNoteApprovalCountsDto
        {
            Pending = await sap.CountCreditNoteApprovalRequestsAsync(
                CreditNoteApprovalStatusFilters.ToSapStatuses(CreditNoteApprovalStatusFilters.Pending), stages, cancellationToken),
            Approved = await sap.CountCreditNoteApprovalRequestsAsync(
                CreditNoteApprovalStatusFilters.ToSapStatuses(CreditNoteApprovalStatusFilters.Approved), stages, cancellationToken),
            Rejected = await sap.CountCreditNoteApprovalRequestsAsync(
                CreditNoteApprovalStatusFilters.ToSapStatuses(CreditNoteApprovalStatusFilters.Rejected), stages, cancellationToken)
        };
    }
}
