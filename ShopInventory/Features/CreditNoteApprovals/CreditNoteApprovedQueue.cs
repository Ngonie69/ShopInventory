using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.CreditNoteApprovals;

/// <summary>
/// The Approved queue somebody can still act on: the approved requests whose draft SAP still holds.
/// </summary>
/// <remarks>
/// <para>
/// SAP leaves a request <c>arsApproved</c> after its draft has been added from the B1 client, so the
/// status alone describes history. On production on 2026-11-11, 6,774 credit memo requests were
/// Approved and only 169 of them still pointed at an open draft; the rest were added long ago and
/// showed in the queue as "The draft is closed or cancelled in SAP", unactionable, burying the
/// handful that could be added.
/// </para>
/// <para>
/// No OData filter on <c>ApprovalRequests</c> can ask about the draft, so the queue is read the other
/// way round: from the drafts SAP still holds (a few hundred, cached in
/// <see cref="ICreditNoteAlreadyCreditedIndex"/>) to their approved requests, which are read fresh on
/// every call. A decision made in the B1 client therefore shows at once. The one thing the cached
/// draft list can miss is a draft raised and approved inside the cache's ten minutes; that request
/// shows once the index is rebuilt.
/// </para>
/// </remarks>
public static class CreditNoteApprovedQueue
{
    private static readonly string[] ApprovedStatus = [SapApprovalRequestStatuses.Approved];

    /// <summary>
    /// The drafts SAP still holds, from the cached index; or null when SAP cannot say, in which case the
    /// caller reads the queue as it always did, history included. Asked before any queue read, so the
    /// fallback never throws a read away.
    /// </summary>
    public static async Task<IReadOnlySet<int>?> TryGetHeldDraftsAsync(
        ICreditNoteAlreadyCreditedIndex index,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            return await index.GetHeldDraftEntriesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not read which credit note drafts SAP still holds; the Approved queue shows every approved request");
            return null;
        }
    }

    /// <summary>Every approved request over one of <paramref name="held"/>, in this scope and narrowing, newest first.</summary>
    public static async Task<List<SAPApprovalRequest>> ReadAsync(
        ISAPServiceLayerClient sap,
        IReadOnlySet<int> held,
        CreditNoteApprovalStageFilter scope,
        CreditNoteApprovalRequestFilter? narrowing,
        CancellationToken cancellationToken)
    {
        if (held.Count == 0)
        {
            return [];
        }

        var requests = await sap.GetCreditNoteApprovalRequestsForDraftsAsync(
            ApprovedStatus, held.ToList(), scope.StageCodes, narrowing, cancellationToken);

        // One read per batch of drafts, so the batches come back each in order but not in order together.
        return requests
            .GroupBy(request => request.Code)
            .Select(group => group.First())
            .OrderByDescending(request => request.Code)
            .ToList();
    }
}
