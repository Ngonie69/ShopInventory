using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Features.CreditNoteApprovals;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.SapTransferApprovals.Queries.GetSapTransferApprovals;

/// <summary>
/// Reads the approved, still-open transfer drafts from SAP, joins the approval requests behind them in
/// one batched read, and names the people, templates and stages through the cached lookups.
/// </summary>
/// <remarks>
/// The queue is read from the draft side: the draft is what says a transfer is approved and still
/// waiting, and it carries the warehouses, so a depot controller's queue is filtered and counted in
/// SAP rather than thinned out of a page here. A label that fails to read costs the row its name, not
/// the list its answer.
/// </remarks>
public sealed class GetSapTransferApprovalsHandler(
    ISAPServiceLayerClient sap,
    ISapApprovalLookups lookups,
    ISapTransferApprovalAccess access,
    IOptions<SAPSettings> sapSettings,
    ILogger<GetSapTransferApprovalsHandler> logger)
    : IRequestHandler<GetSapTransferApprovalsQuery, ErrorOr<SapTransferApprovalListResponseDto>>
{
    /// <summary>
    /// How many distinct labels are read at once. Half of <c>SAP:MaxConcurrentRequests</c> (6), which
    /// is the whole application's budget, as for the credit memo queue.
    /// </summary>
    private const int LookupConcurrency = 3;

    public async Task<ErrorOr<SapTransferApprovalListResponseDto>> Handle(
        GetSapTransferApprovalsQuery query,
        CancellationToken cancellationToken)
    {
        if (!sapSettings.Value.Enabled)
        {
            return Errors.SapTransferApproval.SapDisabled;
        }

        var caller = await access.ResolveAsync(query.CallerUserId, cancellationToken);
        if (caller.IsError)
        {
            return caller.Errors;
        }

        List<SAPStockTransferDraft> drafts;
        int total;
        List<SAPApprovalRequest> requests;
        try
        {
            (drafts, total) = await sap.GetApprovedStockTransferDraftsAsync(
                query.Page,
                query.PageSize,
                query.BeforeDocEntry,
                caller.Value.SourceWarehouses,
                query.EffectiveFilter,
                cancellationToken);

            requests = drafts.Count == 0
                ? []
                : await sap.GetStockTransferApprovalRequestsAsync(
                    drafts.Select(draft => draft.DocEntry).ToList(), withLines: false, cancellationToken);
        }
        catch (SapRequestRejectedException rejected)
        {
            return Errors.SapTransferApproval.SapRejected(rejected.SapMessage);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Could not read the approved stock transfer drafts from SAP");
            return Errors.SapTransferApproval.SapUnavailable(exception.Message);
        }

        var requestsByDraft = requests
            .Where(request => request.DraftEntry is > 0)
            .GroupBy(request => request.DraftEntry!.Value)
            .ToDictionary(group => group.Key, group => (IReadOnlyCollection<SAPApprovalRequest>)group.ToList());

        var primaries = drafts
            .Select(draft => SapTransferApprovalProjection.Primary(requestsByDraft.GetValueOrDefault(draft.DocEntry) ?? []))
            .OfType<SAPApprovalRequest>()
            .ToList();

        using var slots = new SemaphoreSlim(LookupConcurrency, LookupConcurrency);
        var originatorsTask = ResolveAsync(
            primaries.Select(request => request.OriginatorID), slots,
            (id, token) => lookups.GetUserAsync(id, token), id => $"SAP user {id}", cancellationToken);
        var templatesTask = ResolveAsync(
            primaries.Select(request => request.ApprovalTemplatesID), slots,
            (code, token) => lookups.GetTemplateAsync(code, token), code => $"approval template {code}", cancellationToken);
        var stagesTask = ResolveAsync(
            primaries.Select(request => request.CurrentStage), slots,
            (code, token) => lookups.GetStageAsync(code, token), code => $"approval stage {code}", cancellationToken);

        await Task.WhenAll(originatorsTask, templatesTask, stagesTask);
        var originators = await originatorsTask;
        var templates = await templatesTask;
        var stages = await stagesTask;

        var items = new List<SapTransferApprovalListItemDto>(drafts.Count);
        foreach (var draft in drafts)
        {
            var held = requestsByDraft.GetValueOrDefault(draft.DocEntry) ?? [];
            var primary = SapTransferApprovalProjection.Primary(held);
            items.Add(SapTransferApprovalProjection.ToListItem(
                draft,
                held,
                Labelled(originators, primary?.OriginatorID),
                Labelled(templates, primary?.ApprovalTemplatesID),
                Labelled(stages, primary?.CurrentStage),
                caller.Value));
        }

        return new SapTransferApprovalListResponseDto
        {
            Items = items,
            TotalCount = total,
            Page = query.Page,
            PageSize = query.PageSize,
            Warehouse = string.IsNullOrWhiteSpace(query.Warehouse) ? null : query.Warehouse.Trim(),
            ScopedWarehouses = caller.Value.SourceWarehouses?.ToList(),

            // Ordered DocEntry desc, so the last row is the lowest and the next page starts below it.
            NextCursor = drafts.Count < query.PageSize ? null : drafts[^1].DocEntry
        };
    }

    private async Task<Dictionary<int, T>> ResolveAsync<T>(
        IEnumerable<int?> codes,
        SemaphoreSlim slots,
        Func<int, CancellationToken, Task<T?>> lookup,
        Func<int, string> describe,
        CancellationToken cancellationToken)
        where T : class
    {
        var distinct = codes.OfType<int>().Distinct().ToList();
        var resolved = new Dictionary<int, T>(distinct.Count);
        if (distinct.Count == 0)
        {
            return resolved;
        }

        var gate = new Lock();
        await Task.WhenAll(distinct.Select(async code =>
        {
            T? value;
            await slots.WaitAsync(cancellationToken);
            try
            {
                value = await lookup(code, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Could not read {What} from SAP; the held transfers list shows the code instead", describe(code));
                value = null;
            }
            finally
            {
                slots.Release();
            }

            if (value is null)
            {
                return;
            }

            lock (gate)
            {
                resolved[code] = value;
            }
        }));

        return resolved;
    }

    private static T? Labelled<T>(Dictionary<int, T> resolved, int? code) where T : class =>
        code is int value && resolved.TryGetValue(value, out var found) ? found : null;
}
