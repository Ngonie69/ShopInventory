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
/// <remarks>
/// The two open queues leave out drafts already credited some other way, as the list does, so their
/// counts do too — from the same cached index, which the list has normally just read. The Approved count
/// is of the requests whose draft SAP still holds (<see cref="CreditNoteApprovedQueue"/>), as the list
/// shows them, rather than a <c>$count</c> of every approved request ever added.
/// </remarks>
public sealed class GetCreditNoteApprovalCountsHandler(
    ISAPServiceLayerClient sap,
    ICreditNoteApprovalStageScope stageScope,
    ICreditNoteAlreadyCreditedIndex alreadyCreditedIndex,
    IOptions<SAPSettings> sapSettings,
    ILogger<GetCreditNoteApprovalCountsHandler> logger)
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
        var filter = query.Filter;
        var pending = CreditNoteApprovalStatusFilters.ToSapStatuses(CreditNoteApprovalStatusFilters.Pending);
        var approved = CreditNoteApprovalStatusFilters.ToSapStatuses(CreditNoteApprovalStatusFilters.Approved);

        IReadOnlyDictionary<int, AlreadyCreditedRequest> index;
        try
        {
            index = await alreadyCreditedIndex.GetAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not build the already-credited index; the queue counts include those drafts");
            index = new Dictionary<int, AlreadyCreditedRequest>();
        }

        int Hidden(IReadOnlyCollection<string> statuses) =>
            index.Values.Count(entry => entry.FallsIn(statuses, scope.Value, filter));

        var pendingCount = Math.Max(0, await sap.CountCreditNoteApprovalRequestsAsync(pending, stages, filter, cancellationToken) - Hidden(pending));

        var held = await CreditNoteApprovedQueue.TryGetHeldDraftsAsync(alreadyCreditedIndex, logger, cancellationToken);
        var approvedCount = held is null
            ? Math.Max(0, await sap.CountCreditNoteApprovalRequestsAsync(approved, stages, filter, cancellationToken) - Hidden(approved))
            : (await CreditNoteApprovedQueue.ReadAsync(sap, held, scope.Value, filter, cancellationToken))
                .Count(request => !index.ContainsKey(request.Code));

        return new CreditNoteApprovalCountsDto
        {
            Pending = pendingCount,
            Approved = approvedCount,
            Rejected = await sap.CountCreditNoteApprovalRequestsAsync(
                CreditNoteApprovalStatusFilters.ToSapStatuses(CreditNoteApprovalStatusFilters.Rejected), stages, filter, cancellationToken)
        };
    }
}
