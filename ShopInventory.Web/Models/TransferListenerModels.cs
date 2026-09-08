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
    /// Counters since the listener process started. A restart zeroes them, so a zero here means
    /// "nothing since the last restart" and never "no transfers happened" — which is why the page
    /// shows the process start beside them.
    /// </summary>
    public int DocumentsSeen { get; set; }

    public int LinesSeen { get; set; }

    public int InboundDocuments { get; set; }

    public int OutboundDocuments { get; set; }

    public int WebhookSuccessCount { get; set; }

    /// <summary>Documents whose webhook never landed. Nothing retries them.</summary>
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

    public string? Message { get; set; }
}
