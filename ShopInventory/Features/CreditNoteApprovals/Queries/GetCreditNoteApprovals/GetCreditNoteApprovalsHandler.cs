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
    /// <summary>
    /// How many distinct labels are read at once — one budget for the whole wave, not one per kind.
    /// Half of <c>SAP:MaxConcurrentRequests</c> (6), which is the whole application's connection
    /// budget rather than this list's, and of which two slots are reserved for interactive work.
    /// </summary>
    private const int LookupConcurrency = 3;

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

        // Who this app decides as is configuration, not a property of any row, so it is read
        // alongside the list rather than after it.
        var serviceApproverTask = TryLookupAsync(() => lookups.GetServiceApproverAsync(cancellationToken), "the service approver");

        var (requests, total) = await sap.GetCreditNoteApprovalRequestsAsync(
            statuses, query.Page, query.PageSize, query.BeforeCode, cancellationToken);

        var draftEntries = requests
            .Where(request => request.DraftEntry is > 0)
            .Select(request => request.DraftEntry!.Value)
            .Distinct()
            .ToList();

        var drafts = (draftEntries.Count == 0
                ? []
                : await sap.GetCreditNoteDraftsAsync(draftEntries, cancellationToken))
            .GroupBy(draft => draft.DocEntry)
            .ToDictionary(group => group.Key, group => group.First());

        // The labels were read per row and awaited in turn, so a cold page of 25 was a chain of up to
        // 76 waits. They are read once per distinct code instead — the live queue names a handful of
        // originators and stages over 25 rows — and together rather than one after another.
        // Deliberately after the drafts rather than alongside them: on every load but the first of
        // the ten-minute window these are cache hits costing nothing, and overlapping them with the
        // drafts would buy one round trip once in ten minutes at the price of holding most of the
        // application's SAP connections on every load.
        using var slots = new SemaphoreSlim(LookupConcurrency, LookupConcurrency);

        var originatorsTask = ResolveAsync(
            requests.Select(request => request.OriginatorID),
            slots,
            (id, token) => lookups.GetUserAsync(id, token),
            id => $"SAP user {id}",
            cancellationToken);
        var templatesTask = ResolveAsync(
            requests.Select(request => request.ApprovalTemplatesID),
            slots,
            (code, token) => lookups.GetTemplateAsync(code, token),
            code => $"approval template {code}",
            cancellationToken);
        var stagesTask = ResolveAsync(
            requests.Select(request => request.CurrentStage),
            slots,
            (code, token) => lookups.GetStageAsync(code, token),
            code => $"approval stage {code}",
            cancellationToken);

        await Task.WhenAll(originatorsTask, templatesTask, stagesTask, serviceApproverTask);

        var serviceApprover = await serviceApproverTask;
        var originators = await originatorsTask;
        var templates = await templatesTask;
        var stages = await stagesTask;

        var items = new List<CreditNoteApprovalListItemDto>(requests.Count);
        foreach (var request in requests)
        {
            var draft = request.DraftEntry is int draftEntry && drafts.TryGetValue(draftEntry, out var found) ? found : null;
            var originator = Labelled(originators, request.OriginatorID);
            var template = Labelled(templates, request.ApprovalTemplatesID);
            var stage = Labelled(stages, request.CurrentStage);

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
            Status = filter,

            // A short page is the end of the queue, so there is nothing to continue from. The rows
            // are ordered Code desc, so the last one is the lowest and the next page starts below it.
            NextCursor = requests.Count < query.PageSize ? null : requests[^1].Code
        };
    }

    /// <summary>
    /// Reads one label per distinct code, concurrently within <paramref name="slots"/> — the budget
    /// the whole wave shares, so a page naming many different originators cannot take every SAP
    /// connection the application has.
    /// </summary>
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
        var reads = distinct.Select(async code =>
        {
            // A lookup that fails costs the row its label, not the list its answer.
            var value = await TryLookupAsync(
                () => Gated(slots, token => lookup(code, token), cancellationToken), describe(code));
            if (value is null)
            {
                return;
            }

            lock (gate)
            {
                resolved[code] = value;
            }
        });

        await Task.WhenAll(reads);
        return resolved;
    }

    /// <summary>Runs one read while holding a slot from the wave's shared budget.</summary>
    private static async Task<T?> Gated<T>(
        SemaphoreSlim slots,
        Func<CancellationToken, Task<T?>> read,
        CancellationToken cancellationToken)
        where T : class
    {
        await slots.WaitAsync(cancellationToken);
        try
        {
            return await read(cancellationToken);
        }
        finally
        {
            slots.Release();
        }
    }

    /// <summary>The label read for this code, or null — an unread label leaves the row showing the code.</summary>
    private static T? Labelled<T>(Dictionary<int, T> resolved, int? code) where T : class =>
        code is int value && resolved.TryGetValue(value, out var found) ? found : null;

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
