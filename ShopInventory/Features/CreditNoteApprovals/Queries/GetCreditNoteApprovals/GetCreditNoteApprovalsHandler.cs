using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.CreditNoteApprovals.Queries.GetCreditNoteApprovals;

/// <summary>
/// Reads the requests from SAP, joins the drafts they hold in one batched read, and names the people,
/// templates and stages through the cached lookups. A lookup that fails costs the row its label, not
/// the list its answer.
/// </summary>
public sealed class GetCreditNoteApprovalsHandler(
    ISAPServiceLayerClient sap,
    ISapApprovalLookups lookups,
    IOptions<SAPSettings> sapSettings,
    ILogger<GetCreditNoteApprovalsHandler> logger)
    : IRequestHandler<GetCreditNoteApprovalsQuery, ErrorOr<CreditNoteApprovalListResponseDto>>
{
    public async Task<ErrorOr<CreditNoteApprovalListResponseDto>> Handle(
        GetCreditNoteApprovalsQuery query,
        CancellationToken cancellationToken)
    {
        if (!sapSettings.Value.Enabled)
        {
            return Errors.CreditNoteApproval.SapDisabled;
        }

        var filter = CreditNoteApprovalStatusFilters.Normalise(query.Status);
        var statuses = CreditNoteApprovalStatusFilters.ToSapStatuses(filter);

        var (requests, total) = await sap.GetCreditNoteApprovalRequestsAsync(statuses, query.Page, query.PageSize, cancellationToken);

        var draftEntries = requests
            .Where(request => request.DraftEntry is > 0)
            .Select(request => request.DraftEntry!.Value)
            .Distinct()
            .ToList();
        var drafts = draftEntries.Count == 0
            ? []
            : (await sap.GetCreditNoteDraftsAsync(draftEntries, cancellationToken))
                .GroupBy(draft => draft.DocEntry)
                .ToDictionary(group => group.Key, group => group.First());

        var serviceApprover = await TryLookupAsync(() => lookups.GetServiceApproverAsync(cancellationToken), "the service approver");

        var items = new List<CreditNoteApprovalListItemDto>(requests.Count);
        foreach (var request in requests)
        {
            var draft = request.DraftEntry is int draftEntry && drafts.TryGetValue(draftEntry, out var found) ? found : null;
            var originator = request.OriginatorID is int originatorId
                ? await TryLookupAsync(() => lookups.GetUserAsync(originatorId, cancellationToken), $"SAP user {originatorId}")
                : null;
            var template = request.ApprovalTemplatesID is int templateCode
                ? await TryLookupAsync(() => lookups.GetTemplateAsync(templateCode, cancellationToken), $"approval template {templateCode}")
                : null;
            var stage = request.CurrentStage is int stageCode
                ? await TryLookupAsync(() => lookups.GetStageAsync(stageCode, cancellationToken), $"approval stage {stageCode}")
                : null;

            // The list reads no approver lines, so "already decided on this stage" is the detail's call;
            // the decision itself is guarded again in its handler.
            items.Add(CreditNoteApprovalProjection.ToListItem(
                request, draft, originator, template, stage, serviceApprover, lookups.ServiceApproverUserCode,
                serviceApproverAlreadyDecided: false));
        }

        return new CreditNoteApprovalListResponseDto
        {
            Items = items,
            TotalCount = total,
            Page = query.Page,
            PageSize = query.PageSize,
            Status = filter
        };
    }

    private async Task<T?> TryLookupAsync<T>(Func<Task<T?>> lookup, string what) where T : class
    {
        try
        {
            return await lookup();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not read {What} from SAP; the approvals list shows the code instead", what);
            return null;
        }
    }
}
