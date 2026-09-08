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

    // Counters since the listener process started; a restart zeroes them.
    int DocumentsSeen,
    int LinesSeen,
    int InboundDocuments,
    int OutboundDocuments,
    int WebhookSuccessCount,
    int WebhookFailureCount,

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
    double MinutesSinceSuccessfulPoll
);

public sealed record TransferListenerDocumentSummary(
    int? SapDocNum,
    string? SapDocDate,
    DateTime DetectedAt,
    string? Direction,
    string? MonitoredWarehouse,
    string? SourceWarehouse,
    string? DestinationWarehouse,
    bool WebhookSuccess,
    int LineCount,
    IReadOnlyList<TransferListenerLineSummary> Lines
);

public sealed record TransferListenerLineSummary(
    string? ItemCode,
    string? ItemDescription,
    decimal Quantity
);
