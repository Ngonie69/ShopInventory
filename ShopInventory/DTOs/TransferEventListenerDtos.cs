namespace ShopInventory.DTOs;

/// <summary>
/// The shapes TransferEventListener answers with, mirrored here by hand.
/// </summary>
/// <remarks>
/// Nullability is load-bearing, not decorative. These are deserialised with
/// <c>System.Text.Json</c>, which throws on a null arriving at a non-nullable member and takes the
/// whole reply down with it — so anything the listener can leave out is nullable here even where the
/// listener's own model says otherwise. Its <c>DocumentSummary.SapDocNum</c> and <c>SapDocDate</c>
/// are both nullable at the source, and a value type that is not nullable here would turn a routine
/// event into a failed page.
/// </remarks>
public sealed class TransferListenerHealthDto
{
    /// <summary>"healthy", "degraded" or "unhealthy" as the listener judges itself.</summary>
    public string? Status { get; set; }

    public string? Message { get; set; }

    public TransferListenerPollDto? Poll { get; set; }

    public int MonitoredWarehouseCount { get; set; }

    public int DocumentsSeen { get; set; }

    public int LinesSeen { get; set; }
}

/// <summary>
/// What the listener's SAP poll loop has been doing. The freshness figure is
/// <see cref="LastSuccessUtc"/>: the listener answers HTTP from memory, so everything else can look
/// healthy while nothing has been read from SAP for hours.
/// </summary>
public sealed class TransferListenerPollDto
{
    public DateTime ProcessStartedUtc { get; set; }

    public bool PollingStarted { get; set; }

    public DateTime? LastAttemptUtc { get; set; }

    public DateTime? LastSuccessUtc { get; set; }

    /// <summary>
    /// The left edge of the window the listener will read next. It does not advance on a failed
    /// cycle, so a value far behind <see cref="LastAttemptUtc"/> means the backlog is being retried.
    /// </summary>
    public DateTime? PollingSinceUtc { get; set; }

    public int ConsecutiveFailures { get; set; }

    public string? LastError { get; set; }

    public DateTime? LastErrorUtc { get; set; }

    public int PollIntervalSeconds { get; set; }

    /// <summary>
    /// Failures of the listener's batch-sync call, which is not the call that moves the ledger. The
    /// ledger's own delivery is <see cref="PendingNotifications"/> and the members after it.
    /// </summary>
    public int WebhookFailures { get; set; }

    public bool ResumedFromSavedState { get; set; }

    public int ProcessedDocuments { get; set; }

    // ── Delivery to this API's transfer-event webhook ──────────────────────
    //
    // Every member below is absent from a listener older than its retry queue, and reads as zero or
    // null there. None of them may be made non-nullable: see the class remark on the file.

    /// <summary>
    /// Lines found in SAP that have not reached this API yet and are retried every cycle. On
    /// 2026-09-17 this stood at 106 while every other figure the page showed was green — the listener
    /// was posting to a port nothing listened on, and a till was missing every transfer since the
    /// previous morning.
    /// </summary>
    public int PendingNotifications { get; set; }

    public DateTime? OldestPendingNotificationUtc { get; set; }

    /// <summary>Lines the listener stopped retrying because their ledger day ended.</summary>
    public int AbandonedNotifications { get; set; }

    /// <summary>Lines this API refused as invalid. Not retried.</summary>
    public int RejectedNotifications { get; set; }

    /// <summary>Where the listener posts lines. Never carries the key.</summary>
    public string? WebhookUrl { get; set; }

    public DateTime? LastDeliveredUtc { get; set; }

    /// <summary>The answer to the most recent line that did not get through, e.g. "HTTP 404".</summary>
    public string? LastDeliveryError { get; set; }

    public DateTime? LastDeliveryErrorUtc { get; set; }
}

/// <summary>
/// One warehouse's non-batch stock as the listener reads it, from <c>GET /item-quantities/{code}</c>.
/// </summary>
public sealed class TransferListenerWarehouseStockDto
{
    public string? WarehouseCode { get; set; }

    public int ItemCount { get; set; }

    /// <summary>
    /// Which read path answered: <c>sql-query</c> is complete, <c>item-scan-fallback</c> covers only
    /// the listener's configured item groups and may be short. The listener answers 503 rather than
    /// sending <c>unavailable</c>, so a body that arrives is always one of those two.
    /// </summary>
    public string? Source { get; set; }

    public List<TransferListenerItemQuantityDto> Items { get; set; } = [];
}

/// <summary>One item's position in one warehouse.</summary>
/// <remarks>
/// <c>Available</c> is computed by the listener as <c>InStock - Committed + Ordered</c> and so
/// already includes stock that has not arrived. It is deliberately not mapped onto
/// <see cref="StockQuantityDto.Available"/> by the caller, which needs what is on the shelf.
/// </remarks>
public sealed class TransferListenerItemQuantityDto
{
    public string? ItemCode { get; set; }

    public string? ItemName { get; set; }

    public int? ItemGroupCode { get; set; }

    public string? WarehouseCode { get; set; }

    public string? BusinessPartnerCode { get; set; }

    public string? BusinessPartnerName { get; set; }

    public int? PriceListNum { get; set; }

    public decimal? UnitPrice { get; set; }

    public string? PriceCurrency { get; set; }

    public decimal InStock { get; set; }

    public decimal Committed { get; set; }

    public decimal Ordered { get; set; }

    public decimal Available { get; set; }
}

/// <summary>
/// The listener's in-memory dashboard counters, from <c>GET /stats</c>.
/// </summary>
/// <remarks>
/// Everything here is counted since the listener process started and is lost on restart. A zero
/// therefore means "nothing since the last restart", never "no transfers happened" — read it beside
/// <see cref="TransferListenerPollDto.ProcessStartedUtc"/> or it is misleading.
/// </remarks>
public sealed class TransferListenerStatsDto
{
    public int TotalDocuments { get; set; }

    public int WebhookSuccessCount { get; set; }

    /// <summary>
    /// Documents whose webhook did not land. Nothing retries them, so each one is a document the
    /// snapshot never heard about.
    /// </summary>
    public int WebhookFailureCount { get; set; }

    public int InboundDocuments { get; set; }

    public int OutboundDocuments { get; set; }

    public int TotalLines { get; set; }

    public List<TransferListenerDocumentDto> RecentDocuments { get; set; } = [];

    public Dictionary<string, int> TransfersByWarehouse { get; set; } = [];
}

/// <summary>One transfer document the listener saw, with the lines it raised events for.</summary>
public sealed class TransferListenerDocumentDto
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

    public List<TransferListenerLineDto> Lines { get; set; } = [];
}

public sealed class TransferListenerLineDto
{
    public string? ItemCode { get; set; }

    public string? ItemDescription { get; set; }

    public decimal Quantity { get; set; }
}

/// <summary>
/// The summary half of the listener's manual-check report, from <c>POST /check-now</c>.
/// </summary>
/// <remarks>
/// The listener also returns <c>allTransfers</c> — every document it examined, with all its lines.
/// That is deliberately not mirrored: it is a diagnostic dump that grows with the poll window, and
/// nothing on this side would do anything with it but pass it through. Anyone who needs it can read
/// the listener's own endpoint.
/// </remarks>
public sealed class TransferListenerCheckResultDto
{
    public DateTime CheckedAt { get; set; }

    /// <summary>Documents SAP returned for the window, monitored or not.</summary>
    public int TotalSapTransfers { get; set; }

    /// <summary>Lines among those that touched a monitored warehouse and had not been seen before.</summary>
    public int MonitoredEventsDetected { get; set; }

    public bool WebhookTriggered { get; set; }

    public bool WebhookSuccess { get; set; }

    // What happened to the lines on their way to this API's ledger. "WebhookSuccess" above is the
    // batch-sync call and says nothing about that. Zero from a listener older than its retry queue.

    public int NotificationsDelivered { get; set; }

    public int NotificationsQueued { get; set; }

    public int NotificationsReplayed { get; set; }

    public int NotificationsRejected { get; set; }

    public int NotificationsAbandoned { get; set; }

    public int PendingNotifications { get; set; }

    public string? Message { get; set; }
}
