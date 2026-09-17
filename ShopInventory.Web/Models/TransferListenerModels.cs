namespace ShopInventory.Web.Models;

/// <summary>
/// The API's <c>DesktopIntegration/transfer-listener/status</c> reply, mirrored by hand.
/// </summary>
/// <remarks>
/// Nullability has to match the API's record exactly. <c>System.Text.Json</c> throws when a null
/// arrives at a non-nullable member, and the page's catch turns that into "no data" — so a mismatch
/// here does not look like a bug, it looks like a listener that is down, which is precisely the
/// reading this page exists to make trustworthy.
///
/// Collections are initialised rather than left null so the markup can enumerate them without a
/// guard on every loop.
/// </remarks>
public sealed class TransferListenerStatusModel
{
    public bool Enabled { get; set; }

    public bool Reachable { get; set; }

    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Why the listener could not be read, when <see cref="Reachable"/> is false.</summary>
    public string? UnreachableReason { get; set; }

    /// <summary>"healthy", "degraded" or "unhealthy" as the listener judges itself.</summary>
    public string? Status { get; set; }

    public string? Message { get; set; }

    public TransferListenerPollModel? Poll { get; set; }

    /// <summary>
    /// Lines on their way from the listener to the API's ledger. Null when the listener could not be
    /// read. Reading SAP and delivering fail independently, and the page once showed only the first.
    /// </summary>
    public TransferListenerDeliveryModel? Delivery { get; set; }

    /// <summary>What the API's own ledger applied, read from its database even when the listener is down.</summary>
    public TransferListenerLedgerModel Ledger { get; set; } = new();

    /// <summary>
    /// Counters since the listener process started. A restart zeroes them, so a zero here means
    /// "nothing since the last restart" and never "no transfers happened" — which is why the page
    /// shows the process start beside them.
    /// </summary>
    public int DocumentsSeen { get; set; }

    public int LinesSeen { get; set; }

    public int InboundDocuments { get; set; }

    public int OutboundDocuments { get; set; }

    public int WebhookSuccessCount { get; set; }

    /// <summary>
    /// Failures of the listener's batch-sync call. Not the ledger's delivery — that is
    /// <see cref="Delivery"/>. This figure read 0 on the day no transfer reached the ledger.
    /// </summary>
    public int WebhookFailureCount { get; set; }

    public List<string> WatchedWarehouses { get; set; } = [];

    /// <summary>
    /// Warehouses the API snapshots that the listener does not watch. Each is read at 07:00 and never
    /// adjusted again, which looks like a quiet day rather than a gap.
    /// </summary>
    public List<string> UnwatchedWarehouses { get; set; } = [];

    public Dictionary<string, int> DocumentsByWarehouse { get; set; } = [];

    public List<TransferListenerDocumentModel> RecentDocuments { get; set; } = [];
}

public sealed class TransferListenerPollModel
{
    public DateTime ProcessStartedUtc { get; set; }

    public bool PollingStarted { get; set; }

    public DateTime? LastAttemptUtc { get; set; }

    public DateTime? LastSuccessUtc { get; set; }

    /// <summary>
    /// The left edge of the window the listener reads next. It does not advance on a failed cycle, so
    /// a value far behind the last attempt means the backlog is being retried rather than lost.
    /// </summary>
    public DateTime? PollingSinceUtc { get; set; }

    public int ConsecutiveFailures { get; set; }

    public string? LastError { get; set; }

    public DateTime? LastErrorUtc { get; set; }

    public int PollIntervalSeconds { get; set; }

    public double MinutesSinceSuccessfulPoll { get; set; }

    public bool ResumedFromSavedState { get; set; }

    public int ProcessedDocuments { get; set; }
}

public sealed class TransferListenerDeliveryModel
{
    /// <summary>Lines retried every cycle until they land or their ledger day ends.</summary>
    public int PendingLines { get; set; }

    public DateTime? OldestPendingUtc { get; set; }

    public double? MinutesOldestPending { get; set; }

    public int AbandonedLines { get; set; }

    public int RejectedLines { get; set; }

    public string? WebhookUrl { get; set; }

    public DateTime? LastDeliveredUtc { get; set; }

    public string? LastError { get; set; }

    public DateTime? LastErrorUtc { get; set; }
}

public sealed class TransferListenerLedgerModel
{
    public DateTime SnapshotDate { get; set; }

    /// <summary>Adjustment rows for the day. A line between two monitored warehouses writes two.</summary>
    public int MovementsToday { get; set; }

    public int DocumentsToday { get; set; }

    public DateTime? LastAppliedUtc { get; set; }

    public int? LastAppliedDocNum { get; set; }

    public string? LastAppliedWarehouse { get; set; }

    public Dictionary<string, int> DocumentsTodayByWarehouse { get; set; } = [];

    /// <summary>False when the API could not read its ledger; the figures are then not a finding.</summary>
    public bool Available { get; set; }
}

public sealed class TransferListenerDocumentModel
{
    public int? SapDocNum { get; set; }

    public string? SapDocDate { get; set; }

    public DateTime DetectedAt { get; set; }

    /// <summary>"IN" or "OUT", relative to the monitored warehouse.</summary>
    public string? Direction { get; set; }

    public string? MonitoredWarehouse { get; set; }

    public string? SourceWarehouse { get; set; }

    public string? DestinationWarehouse { get; set; }

    public bool WebhookSuccess { get; set; }

    public int LineCount { get; set; }

    public List<TransferListenerLineModel> Lines { get; set; } = [];

    /// <summary>"Applied", "Waiting" or "NotApplied", judged by the API against its own ledger.</summary>
    public string? LocalStock { get; set; }

    public DateTime? AppliedAtUtc { get; set; }
}

public sealed class TransferListenerLineModel
{
    public string? ItemCode { get; set; }

    public string? ItemDescription { get; set; }

    public decimal Quantity { get; set; }
}

/// <summary>The API's reply to <c>transfer-listener/check-now</c>.</summary>
public sealed class TransferListenerCheckModel
{
    public DateTime CheckedAt { get; set; }

    public int TotalSapTransfers { get; set; }

    public int MonitoredEventsDetected { get; set; }

    public bool WebhookTriggered { get; set; }

    public bool WebhookSuccess { get; set; }

    public int NotificationsDelivered { get; set; }

    public int NotificationsQueued { get; set; }

    public int NotificationsReplayed { get; set; }

    public int NotificationsRejected { get; set; }

    public int NotificationsAbandoned { get; set; }

    /// <summary>Lines still waiting to reach the ledger after the check.</summary>
    public int PendingNotifications { get; set; }

    public string? Message { get; set; }
}
