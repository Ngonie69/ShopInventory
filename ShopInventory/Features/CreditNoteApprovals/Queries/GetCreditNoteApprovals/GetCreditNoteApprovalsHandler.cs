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
/// the list its answer. The open queues leave out drafts already credited some other way
/// (<see cref="ICreditNoteAlreadyCreditedIndex"/>), and the count and the cursor allow for them. Their
/// approved half is only the requests whose draft SAP still holds (<see cref="CreditNoteApprovedQueue"/>):
/// the thousands of approved requests whose drafts were added long ago are history, not work.
/// </summary>
public sealed class GetCreditNoteApprovalsHandler(
    ISAPServiceLayerClient sap,
    ISapApprovalLookups lookups,
    ICreditNoteApprovalStageScope stageScope,
    ICreditNoteAlreadyCreditedIndex alreadyCreditedIndex,
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

    /// <summary>The client's own ceiling on one page of approval requests.</summary>
    private const int MaxRowsPerRead = 500;

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

        // A stage-scoped role's queue is filtered in SAP rather than here, so the count and the cursor
        // describe the queue it sees instead of a page of the whole one with rows missing.
        var scope = await stageScope.ResolveAsync(query.CallerUserId, cancellationToken);
        if (scope.IsError)
        {
            return scope.Errors;
        }

        // The open queues hide a draft already credited some other way, as SAP hides a draft once it is
        // added; "All requests" still shows it, flagged. The index covers both open queues and is cached,
        // so on every load but the first of its window this costs nothing.
        var index = await TryReadIndexAsync(cancellationToken);
        var showsAll = string.Equals(filter, CreditNoteApprovalStatusFilters.All, StringComparison.Ordinal);
        HashSet<int> Hidden(IReadOnlyCollection<string> sapStatuses) => showsAll
            ? []
            : index.Values
                .Where(entry => entry.FallsIn(sapStatuses, scope.Value, query.Filter))
                .Select(entry => entry.Code)
                .ToHashSet();

        // "All requests" is the history, so it keeps every approved request; the open queues keep only
        // those SAP still holds a draft for. Null when SAP cannot say which those are: the queue is then
        // read as it always was.
        var held = !showsAll && statuses.Contains(SapApprovalRequestStatuses.Approved)
            ? await CreditNoteApprovedQueue.TryGetHeldDraftsAsync(alreadyCreditedIndex, logger, cancellationToken)
            : null;

        var (requests, total, more) = held is null
            ? await ReadQueueAsync(query, statuses, scope.Value, Hidden(statuses), cancellationToken)
            : await ReadWithApprovedQueueAsync(query, statuses, scope.Value, Hidden, held, cancellationToken);

        var draftEntries = requests
            .Where(request => request.DraftEntry is > 0)
            .Select(request => request.DraftEntry!.Value)
            .Distinct()
            .ToList();

        // Who this app decides as is configuration, not a property of any row. Read beside the drafts, not
        // beside the queue: the queue's wave already holds three connections when the approved half is read
        // with it, and the drafts' wave holds two for a page of 25.
        var serviceApproverTask = TryLookupAsync(() => lookups.GetServiceApproverAsync(cancellationToken), "the service approver");

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
            // the decision itself is guarded again in its handler. Only "All requests" can show an
            // already-credited row; it names nobody here — the drawer reads who keyed the credit note.
            items.Add(CreditNoteApprovalProjection.ToListItem(
                request, draft, originator, template, stage, serviceApprover, lookups.ServiceApproverUserCode,
                serviceApproverAlreadyDecided: false, alreadyCredited: index.GetValueOrDefault(request.Code)?.CreditNote));
        }

        return new CreditNoteApprovalListResponseDto
        {
            Items = items,
            TotalCount = total,
            Page = query.Page,
            PageSize = query.PageSize,
            Status = filter,

            // Nothing below is the end of the queue, so there is nothing to continue from. The rows are
            // ordered Code desc, so the last one shown is the lowest and the next page starts below it;
            // hidden rows below it are skipped again then.
            NextCursor = more && requests.Count > 0 ? requests[^1].Code : null
        };
    }

    /// <summary>
    /// One page of SAP's queue as SAP pages it, less the hidden rows: enough rows that the page still
    /// fills once every hidden one below the cursor is dropped, in one read.
    /// </summary>
    private async Task<(List<SAPApprovalRequest> Requests, int Total, bool More)> ReadQueueAsync(
        GetCreditNoteApprovalsQuery query,
        IReadOnlyCollection<string> statuses,
        CreditNoteApprovalStageFilter scope,
        HashSet<int> hidden,
        CancellationToken cancellationToken)
    {
        // An offset read past page 1 cannot know how many hidden rows lie before it, so it reads a plain
        // page; the Web pages by cursor.
        var keyed = query.BeforeCode is not null || query.Page <= 1;
        var hiddenAhead = keyed ? hidden.Count(code => query.BeforeCode is not int cursor || code < cursor) : 0;
        var readSize = Math.Min(query.PageSize + hiddenAhead, MaxRowsPerRead);

        var (read, sapTotal) = await sap.GetCreditNoteApprovalRequestsAsync(
            statuses, query.Page, readSize, query.BeforeCode, scope.StageCodes, query.Filter, cancellationToken);

        var visible = read.Where(request => !hidden.Contains(request.Code)).ToList();
        var requests = visible.Take(query.PageSize).ToList();

        // More lies below when SAP filled the read, or the read held more than this page can show.
        var more = read.Count >= readSize || visible.Count > requests.Count;

        // SAP counted the hidden rows too. Never fewer than are on this page: the index can be a few
        // minutes behind SAP.
        return (requests, Math.Max(requests.Count, sapTotal - hidden.Count), more);
    }

    /// <summary>
    /// One page of a queue whose approved half is <see cref="CreditNoteApprovedQueue"/> (read in full, a
    /// couple of hundred rows at most) and whose pending half, if it has one, is SAP's live queue. The two
    /// are read together and merged newest first, under the same cursor.
    /// </summary>
    private async Task<(List<SAPApprovalRequest> Requests, int Total, bool More)> ReadWithApprovedQueueAsync(
        GetCreditNoteApprovalsQuery query,
        IReadOnlyCollection<string> statuses,
        CreditNoteApprovalStageFilter scope,
        Func<IReadOnlyCollection<string>, HashSet<int>> hiddenIn,
        IReadOnlySet<int> heldDrafts,
        CancellationToken cancellationToken)
    {
        var cursor = query.BeforeCode;

        // Offset paging reads from the top and skips, since neither half alone knows where page N starts.
        var skip = cursor is null && query.Page > 1 ? (query.Page - 1) * query.PageSize : 0;
        var wanted = skip + query.PageSize;

        var liveStatuses = statuses.Where(status => status != SapApprovalRequestStatuses.Approved).ToList();
        var hiddenLive = liveStatuses.Count > 0 ? hiddenIn(liveStatuses) : [];
        var readSize = Math.Min(wanted + hiddenLive.Count(code => cursor is not int below || code < below), MaxRowsPerRead);

        // One connection for the approved half, two for the live page and its count: the wave a plain
        // queue read costs, plus one, and still within the three a list may hold.
        var approvedTask = CreditNoteApprovedQueue.ReadAsync(sap, heldDrafts, scope, query.Filter, cancellationToken);
        var liveTask = liveStatuses.Count > 0
            ? sap.GetCreditNoteApprovalRequestsAsync(liveStatuses, 1, readSize, cursor, scope.StageCodes, query.Filter, cancellationToken)
            : null;

        // WhenAll first so a failure on either side is observed rather than left dangling.
        await Task.WhenAll(liveTask is null ? [approvedTask] : new Task[] { approvedTask, liveTask });

        var approvedQueue = await approvedTask;
        var hiddenApproved = hiddenIn([SapApprovalRequestStatuses.Approved]);
        var approved = approvedQueue.Where(request => !hiddenApproved.Contains(request.Code)).ToList();

        var merged = approved.Where(request => cursor is not int below || request.Code < below).ToList();
        var liveTotal = 0;
        var liveMore = false;

        if (liveTask is not null)
        {
            var (read, sapTotal) = await liveTask;

            liveMore = read.Count >= readSize;
            liveTotal = Math.Max(0, sapTotal - hiddenLive.Count);
            merged.AddRange(read.Where(request => !hiddenLive.Contains(request.Code)));

            // Below the lowest live row read, SAP may hold live rows not read yet, so nothing below it is
            // shown on this page: it would jump ahead of rows the next page will bring.
            if (liveMore && read.Count > 0)
            {
                var floor = read.Min(request => request.Code);
                merged.RemoveAll(request => request.Code < floor);
            }
        }

        merged.Sort((left, right) => right.Code.CompareTo(left.Code));
        var requests = merged.Skip(skip).Take(query.PageSize).ToList();
        var more = liveMore || merged.Count > skip + requests.Count;

        return (requests, Math.Max(requests.Count, liveTotal + approved.Count), more);
    }

    /// <summary>
    /// The already-credited index, or an empty one when it cannot be built. A failed build costs the
    /// open queues their hiding, not the list its answer — the approve and add handlers check afresh
    /// and refuse an already-credited draft either way.
    /// </summary>
    private async Task<IReadOnlyDictionary<int, AlreadyCreditedRequest>> TryReadIndexAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await alreadyCreditedIndex.GetAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not build the already-credited index; the open queues show every request");
            return new Dictionary<int, AlreadyCreditedRequest>();
        }
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
