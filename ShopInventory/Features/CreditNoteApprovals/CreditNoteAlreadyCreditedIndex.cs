using Microsoft.Extensions.Caching.Memory;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.CreditNoteApprovals;

/// <summary>A pending or approved request whose draft some other credit note already credits.</summary>
/// <param name="Code">The approval request.</param>
/// <param name="Status">Its SAP status when the index was built.</param>
/// <param name="CurrentStage">The stage it waits at, for the stage scopes.</param>
/// <param name="RaisedOn">The day it was raised, for the raised-date filter.</param>
/// <param name="OriginatorId">Who raised it, for the raised-by filter.</param>
/// <param name="CreditNote">The credit note that already credits it.</param>
public sealed record AlreadyCreditedRequest(
    int Code,
    string Status,
    int? CurrentStage,
    DateTime? RaisedOn,
    int? OriginatorId,
    SAPCreditNote CreditNote)
{
    /// <summary>Whether this request falls in a queue read with these statuses, stages and narrowing.</summary>
    public bool FallsIn(
        IReadOnlyCollection<string> sapStatuses,
        CreditNoteApprovalStageFilter scope,
        CreditNoteApprovalRequestFilter? narrowing) =>
        sapStatuses.Contains(Status, StringComparer.OrdinalIgnoreCase)
        && scope.Admits(CurrentStage)
        && (narrowing?.RaisedFrom is not DateTime from || RaisedOn is DateTime raised && raised.Date >= from.Date)
        && (narrowing?.RaisedTo is not DateTime to || RaisedOn is DateTime raisedBy && raisedBy.Date <= to.Date)
        && (narrowing?.OriginatorId is not int originator || OriginatorId == originator);
}

/// <summary>
/// Every request in the open queues whose draft is already credited — the drafts SAP still holds
/// Pending after somebody keyed the credit straight into SAP. The open queues hide them, as SAP hides a
/// draft once it has been added; "All requests" still shows them, flagged.
/// </summary>
public interface ICreditNoteAlreadyCreditedIndex
{
    /// <summary>The index, keyed by request code; built at most once per <see cref="CreditNoteAlreadyCreditedIndex.Lifetime"/>.</summary>
    Task<IReadOnlyDictionary<int, AlreadyCreditedRequest>> GetAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The DocEntry of every credit memo draft SAP still holds for approval (open, Pending or Approved),
    /// from the same build as <see cref="GetAsync"/>. The Approved queue shows only requests over these:
    /// the rest were added long ago.
    /// </summary>
    Task<IReadOnlySet<int>> GetHeldDraftEntriesAsync(CancellationToken cancellationToken);

    /// <summary>Forgets the index, so the next read rebuilds it. Called after a decision or an add.</summary>
    void Invalidate();
}

/// <remarks>
/// <para>
/// Hiding a row needs every hidden row known, not one page's: a page that drops rows SAP counted would
/// promise pages that never come, and the queue tiles would disagree with the list. So the index covers
/// every draft SAP still holds — 257 on production on 2026-11-11 — and is cached, and the list and the
/// counts subtract from SAP's own counts what it holds. The rows themselves are still read from SAP on
/// every load, so a request raised a minute ago shows at once.
/// </para>
/// <para>
/// One build is the held drafts in one read, the already-credited read in a few, and the requests of
/// the drafts it found: a couple of seconds, paid once per <see cref="Lifetime"/> by the first page to
/// ask, and shared — concurrent first readers wait for one build rather than each starting their own.
/// </para>
/// <para>
/// Stale by up to <see cref="Lifetime"/> in one direction only that matters: a credit keyed in that
/// window leaves its draft visible a little longer. The approve and add handlers check afresh every
/// time, so a stale index never lets a second credit through.
/// </para>
/// </remarks>
public sealed class CreditNoteAlreadyCreditedIndex(
    ISAPServiceLayerClient sap,
    IMemoryCache cache,
    ILogger<CreditNoteAlreadyCreditedIndex> logger) : ICreditNoteAlreadyCreditedIndex
{
    internal const string CacheKey = "credit-note-approvals:already-credited-index";

    /// <summary>The labels' ten-minute window: a credit keyed in SAP shows here within it.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    // Process-wide, because the index is: scoped instances share the one cache entry and so one build.
    private static readonly SemaphoreSlim BuildGate = new(1, 1);

    private static readonly string[] OpenStatuses = [SapApprovalRequestStatuses.Pending, SapApprovalRequestStatuses.Approved];

    public async Task<IReadOnlyDictionary<int, AlreadyCreditedRequest>> GetAsync(CancellationToken cancellationToken)
        => (await GetSnapshotAsync(cancellationToken)).AlreadyCredited;

    public async Task<IReadOnlySet<int>> GetHeldDraftEntriesAsync(CancellationToken cancellationToken)
        => (await GetSnapshotAsync(cancellationToken)).HeldDraftEntries;

    private async Task<Snapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(CacheKey, out Snapshot? cached) && cached is not null)
        {
            return cached;
        }

        await BuildGate.WaitAsync(cancellationToken);
        try
        {
            if (cache.TryGetValue(CacheKey, out cached) && cached is not null)
            {
                return cached;
            }

            var built = await BuildAsync(cancellationToken);
            cache.Set(CacheKey, built, Lifetime);
            return built;
        }
        finally
        {
            BuildGate.Release();
        }
    }

    public void Invalidate() => cache.Remove(CacheKey);

    /// <remarks>
    /// Built from the drafts, not the requests. Production's Approved queue holds over 6,700 requests,
    /// nearly all of them for drafts added long ago; the drafts SAP still holds number a few hundred.
    /// So: the held drafts, then which of them are already credited, then only those drafts' requests.
    /// </remarks>
    private async Task<Snapshot> BuildAsync(CancellationToken cancellationToken)
    {
        var drafts = await sap.GetOpenHeldCreditNoteDraftsAsync(cancellationToken);
        var held = drafts.Select(draft => draft.DocEntry).Where(entry => entry > 0).ToHashSet();
        var credited = await CreditNoteAlreadyCredited.FindAsync(sap, drafts, cancellationToken);

        var requests = credited.Count == 0
            ? []
            : await sap.GetCreditNoteApprovalRequestsForDraftsAsync(OpenStatuses, credited.Keys.ToList(), cancellationToken: cancellationToken);

        var index = new Dictionary<int, AlreadyCreditedRequest>();
        foreach (var request in requests)
        {
            if (request.DraftEntry is int draftEntry
                && credited.TryGetValue(draftEntry, out var creditNote)
                && !string.IsNullOrWhiteSpace(request.Status))
            {
                index[request.Code] = new AlreadyCreditedRequest(
                    request.Code,
                    request.Status,
                    request.CurrentStage,
                    CreditNoteApprovalProjection.ParseSapDate(request.CreationDate),
                    request.OriginatorID,
                    creditNote);
            }
        }

        logger.LogInformation(
            "Indexed {Requests} open credit note approval request(s) over {Credited} of {Drafts} held draft(s) as already credited",
            index.Count, credited.Count, drafts.Count);
        return new Snapshot(index, held);
    }

    /// <summary>One build: the already-credited requests and the drafts SAP still holds, cached together.</summary>
    private sealed record Snapshot(IReadOnlyDictionary<int, AlreadyCreditedRequest> AlreadyCredited, IReadOnlySet<int> HeldDraftEntries);
}
