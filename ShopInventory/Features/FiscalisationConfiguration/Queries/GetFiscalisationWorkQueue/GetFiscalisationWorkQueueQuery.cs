using ErrorOr;
using MediatR;

namespace ShopInventory.Features.FiscalisationConfiguration.Queries.GetFiscalisationWorkQueue;

/// <summary>
/// Everything owed to ZIMRA: documents eligible for fiscalisation, and documents that failed at it.
/// </summary>
/// <remarks>
/// Every filter here is applied in the database, and that is the point of the query rather than a
/// detail of it. The invoices page filters fiscal status in the browser over whichever page it happened
/// to fetch, so it can only ever show the failures that fell inside that page — a queue that is
/// complete by accident is not a queue. This one narrows first and pages afterwards, so the count at
/// the top is the real number of outstanding items and page two exists.
///
/// <c>Status</c> is one of <see cref="FiscalWorkQueueFilters"/>; null or <c>all</c> for everything
/// outstanding. <c>DeviceId</c> narrows to one fiscal device and so only ever matches sales — a SAP
/// document is fiscalised on whichever device the platform picks, and that choice is not recorded
/// against the document here.
/// </remarks>
public sealed record GetFiscalisationWorkQueueQuery(
    string? Status = null,
    int? DeviceId = null,
    DateTime? FromDate = null,
    DateTime? ToDate = null,
    string? Search = null,
    int Page = 1,
    int PageSize = 50
) : IRequest<ErrorOr<FiscalWorkQueueResult>>;

/// <summary>
/// The filter keys the console offers. Strings rather than an enum because they cross the wire to a
/// hand-mirrored client, where an enum's numeric value is one renumbering away from silently selecting
/// a different filter.
/// </summary>
public static class FiscalWorkQueueFilters
{
    public const string All = "all";

    /// <summary>Waiting to be fiscalised. Nothing has gone wrong yet.</summary>
    public const string AwaitingFiscalisation = "awaiting-fiscalisation";

    /// <summary>Refused for a stated reason. Retryable once the reason is addressed.</summary>
    public const string FiscalisationFailed = "fiscalisation-failed";

    /// <summary>The platform could not say whether the receipt exists. Must be looked up, never resent.</summary>
    public const string NeedsReconciliation = "needs-reconciliation";

    /// <summary>A signed receipt the platform would not take, for a reason that may not repeat.</summary>
    public const string HandoverFailed = "handover-failed";

    /// <summary>A signed receipt that does not continue its device's chain. The device is stopped.</summary>
    public const string ChainBroken = "chain-broken";

    /// <summary>A van sale whose upload carried no usable signature. It can never be archived.</summary>
    public const string Unsignable = "unsignable";

    /// <summary>A van sale the handset never stamped at all, because it predates the signing release.</summary>
    public const string Unstamped = "unstamped";

    /// <summary>
    /// How bad each filter's rows are, as a Nocturne family.
    /// </summary>
    /// <remarks>
    /// One map, read both by the rows — every arm of the work queue's mapping takes its severity from
    /// here — and by the console's filter menu, so a filter's swatch is the same colour as the dot on the
    /// rows it selects. Two switches over the same statuses drift apart the first time a status is added,
    /// and on this page the colour is the summary someone acts on.
    /// </remarks>
    public static string SeverityOf(string filter) => filter switch
    {
        NeedsReconciliation or ChainBroken or Unsignable => FiscalWorkQueueSeverities.Bad,
        FiscalisationFailed or HandoverFailed or Unstamped => FiscalWorkQueueSeverities.Warn,
        _ => FiscalWorkQueueSeverities.Neutral
    };
}

/// <summary>The Nocturne families a work item may carry.</summary>
public static class FiscalWorkQueueSeverities
{
    public const string Neutral = "neutral";
    public const string Warn = "warn";
    public const string Bad = "bad";
}

public sealed record FiscalWorkQueueResult(
    List<FiscalConsoleWorkItemDto> Items,
    int TotalCount,
    int SaleCount,
    int DocumentCount,
    int Page,
    int PageSize,
    bool HasMore);

/// <summary>
/// One thing owed to ZIMRA.
/// </summary>
/// <remarks>
/// <c>Key</c> is stable across a refresh and unique across both sources. The console keys its per-row
/// busy state and its expanded rows on it, and a positional index would move under a reload.
///
/// <c>Stage</c> is which step is outstanding: <c>Fiscalisation</c> (no receipt exists yet) or
/// <c>Hand-over</c> (a receipt exists, signed on a handset, and the platform has not taken it). The
/// distinction decides whether the customer is holding a legal receipt.
///
/// <c>Disposition</c> is what may be done about it — one of <see cref="FiscalWorkQueueDispositions"/>.
/// Computed here rather than inferred from the status in the page, because "may this be retried" is the
/// one question on this screen where a wrong answer is irreversible.
/// </remarks>
public sealed record FiscalConsoleWorkItemDto(
    string Key,
    string Source,
    int? SaleId,
    int? DocNum,
    string Reference,
    string? CustomerName,
    string? WarehouseCode,
    decimal Amount,
    string Currency,
    DateTime OccurredAtUtc,
    string Stage,
    string Status,
    string Severity,
    int? DeviceId,
    int? ReceiptGlobalNo,
    int Attempts,
    string? Error,
    string Disposition,
    string DispositionNote);

/// <summary>What the console may offer for a work item.</summary>
public static class FiscalWorkQueueDispositions
{
    /// <summary>Safe to send again. Nothing reached FDMS, or what did was refused outright.</summary>
    public const string Retry = "retry";

    /// <summary>
    /// Must be looked up at the platform and resolved by hand. Sending it again risks a second fiscal
    /// receipt for one sale, and a fiscal receipt cannot be withdrawn.
    /// </summary>
    public const string Reconcile = "reconcile";

    /// <summary>Nothing will fix it by resending — the receipt itself is unusable or out of sequence.</summary>
    public const string Unrecoverable = "unrecoverable";

    /// <summary>A scheduled run owns this one. Retrying by hand would only race it.</summary>
    public const string Automatic = "automatic";

    /// <summary>
    /// Nothing owns it. No scheduled run selects this row and this page has no route to send it, so it
    /// will sit here until someone acts on it somewhere else.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Automatic"/> because the two are opposites and the console used to
    /// conflate them: a sale the sweep does not select — the wrong source system, older than the
    /// lookback, or out of attempts — was told it was handled automatically, which is the one thing
    /// nobody was doing.
    /// </remarks>
    public const string Stalled = "stalled";
}
