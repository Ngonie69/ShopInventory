namespace ShopInventory.Features.DesktopIntegration.Queries.GetTransferListenerStatus;

/// <summary>
/// TransferEventListener as seen from this API.
/// </summary>
/// <remarks>
/// Deliberately answers even when the listener is unreachable — <see cref="Reachable"/> false with a
/// reason is the most important thing this query can say, and returning an error instead would leave
/// the page with nothing to show at the moment it matters most.
/// </remarks>
public sealed record TransferListenerStatusResult(
    bool Enabled,
    bool Reachable,
    string BaseUrl,

    // Why the listener could not be read, when Reachable is false.
    string? UnreachableReason,

    // "healthy", "degraded" or "unhealthy" as the listener judges itself.
    string? Status,
    string? Message,

    TransferListenerPollSummary? Poll,

    // Null when the listener could not be read.
    TransferListenerDeliverySummary? Delivery,

    // Read from this API's database, so present even when the listener is down or switched off.
    TransferListenerLedgerSummary Ledger,

    // Counters since the listener process started; a restart zeroes them.
    int DocumentsSeen,
    int LinesSeen,
    int InboundDocuments,
    int OutboundDocuments,

    // Documents by delivery to this API's transfer-event webhook: every line taken; a line refused
    // or given up on; a line still retrying and none failed. The first two keep the listener's names.
    int WebhookSuccessCount,
    int WebhookFailureCount,
    int RetryingDocuments,

    IReadOnlyList<string> WatchedWarehouses,

    // Warehouses in this API's daily snapshot that the listener does not watch. Each one is
    // snapshotted at 07:00 and never adjusted again, which looks like a quiet day rather than a gap.
    IReadOnlyList<string> UnwatchedWarehouses,

    // Documents per monitored warehouse, since the listener started.
    IReadOnlyDictionary<string, int> DocumentsByWarehouse,

    IReadOnlyList<TransferListenerDocumentSummary> RecentDocuments
);

/// <summary>
/// The poll loop's own account of itself, with the staleness this API computed from it.
/// </summary>
public sealed record TransferListenerPollSummary(
    DateTime ProcessStartedUtc,
    bool PollingStarted,
    DateTime? LastAttemptUtc,
    DateTime? LastSuccessUtc,

    // The left edge of the window the listener reads next. It does not advance on a failed cycle, so
    // a value far behind the last attempt means the backlog is being retried rather than lost.
    DateTime? PollingSinceUtc,

    int ConsecutiveFailures,
    string? LastError,
    DateTime? LastErrorUtc,
    int PollIntervalSeconds,

    // Minutes since the last cycle that actually read SAP. Measured from the listener's process
    // start when no cycle has ever succeeded, so a listener that has never worked reads as stale
    // rather than as fresh.
    double MinutesSinceSuccessfulPoll,

    // Whether the listener picked up its poll window, processed set and retry queue from disk when
    // it started, and how many documents that set holds.
    bool ResumedFromSavedState,
    int ProcessedDocuments
);

/// <summary>
/// The second hop: lines the listener found, on their way to this API's transfer-event webhook.
/// </summary>
/// <remarks>
/// Reading SAP and delivering to this API fail independently, and only the first used to be on the
/// page. On 2026-09-17 the listener read SAP every two minutes while posting every line to a port
/// nothing listened on; the page said "reading SAP normally" and no transfer had reached a till since
/// the previous morning.
/// </remarks>
public sealed record TransferListenerDeliverySummary(
    // Lines retried every cycle until they land or their ledger day ends.
    int PendingLines,
    DateTime? OldestPendingUtc,

    // Minutes the oldest waiting line has waited; null when nothing waits.
    double? MinutesOldestPending,

    // Given up on when their ledger day ended, since the listener started.
    int AbandonedLines,

    // Refused by this API as invalid, since the listener started. Never retried.
    int RejectedLines,

    // Where the listener posts. Null from a listener that does not report it.
    string? WebhookUrl,
    DateTime? LastDeliveredUtc,
    string? LastError,
    DateTime? LastErrorUtc
);

/// <summary>
/// The third hop, read from this API's own ledger rather than taken from the listener.
/// </summary>
/// <remarks>
/// The listener can only say a line was accepted. Whether the stock moved is decided here — a line
/// for a warehouse with no snapshot today is accepted and changes nothing — so the page asks the
/// table the transfer handler writes, <c>StockTransferAdjustments</c>.
/// </remarks>
public sealed record TransferListenerLedgerSummary(
    // The snapshot day in force, which rolls at the morning fetch rather than at midnight.
    DateTime SnapshotDate,

    // Adjustment rows written for that day. A line between two monitored warehouses writes two.
    int MovementsToday,
    int DocumentsToday,

    // The newest adjustment on any day, so "nothing today" can say how long it has been.
    DateTime? LastAppliedUtc,
    int? LastAppliedDocNum,
    string? LastAppliedWarehouse,

    // Documents applied today, per warehouse.
    IReadOnlyDictionary<string, int> DocumentsTodayByWarehouse,

    // False when the ledger could not be read; the figures above are then zero, not a finding.
    bool Available
);

public sealed record TransferListenerDocumentSummary(
    int? SapDocNum,
    string? SapDocDate,
    DateTime DetectedAt,
    string? Direction,
    string? MonitoredWarehouse,
    string? SourceWarehouse,
    string? DestinationWarehouse,

    // Whether this API took every line, as the listener reports it.
    bool WebhookSuccess,

    // The listener's word on delivery to this API: its least-delivered line, one of
    // TransferListenerDelivery. Null from a listener that still sent sync-batches.
    string? Delivery,
    int LinesDelivered,

    // This API's answer for the line that decided Delivery.
    string? DeliveryDetail,

    int LineCount,
    IReadOnlyList<TransferListenerLineSummary> Lines,

    // "Applied", "Waiting" or "NotApplied", judged against this API's ledger; see
    // GetTransferListenerStatusHandler.LocalStockState.
    string LocalStock,

    // When the ledger first recorded this document, if it has.
    DateTime? AppliedAtUtc
);

public sealed record TransferListenerLineSummary(
    string? ItemCode,
    string? ItemDescription,
    decimal Quantity,
    string? Delivery,
    string? DeliveryDetail
);
