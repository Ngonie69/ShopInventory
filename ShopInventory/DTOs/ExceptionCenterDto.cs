namespace ShopInventory.DTOs;

/// <summary>
/// Everything the exception center page needs to answer three questions: what is
/// actually stuck, why is it stuck, and what is it costing.
/// </summary>
public sealed class ExceptionCenterDashboardDto
{
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Headline triage numbers, counted over the whole failing population.</summary>
    public ExceptionCenterTriageDto Triage { get; set; } = new();

    /// <summary>Distinct root causes, most impactful first.</summary>
    public List<ExceptionCenterClusterDto> Clusters { get; set; } = new();

    /// <summary>Per-pipeline rollup, so a single sick integration is obvious.</summary>
    public List<ExceptionCenterSourceDto> Sources { get; set; } = new();

    /// <summary>Money and volume held up by the failures.</summary>
    public List<ExceptionCenterExposureDto> Exposure { get; set; } = new();

    /// <summary>Hourly arrivals over the last 24 hours, oldest first.</summary>
    public List<ExceptionCenterTrendPointDto> Trend { get; set; } = new();

    /// <summary>The page of items returned for the work queue.</summary>
    public List<ExceptionCenterItemDto> Items { get; set; } = new();

    /// <summary>Size of the whole failing population, regardless of how many items were returned.</summary>
    public int TotalItemCount { get; set; }

    /// <summary>True when <see cref="Items"/> is a subset of the population.</summary>
    public bool ItemsTruncated { get; set; }

    /// <summary>
    /// True when a source held more rows than the analysis scan would read, so
    /// clusters and exposure describe a sample rather than everything.
    /// </summary>
    public bool AnalysisTruncated { get; set; }
}

/// <summary>
/// The triage split. The distinction that matters operationally is between work
/// that will never recover on its own and work that is still retrying.
/// </summary>
public sealed class ExceptionCenterTriageDto
{
    /// <summary>Needs a human: awaiting review, or out of retry attempts.</summary>
    public int BlockedCount { get; set; }

    /// <summary>Blocked items nobody has taken ownership of.</summary>
    public int BlockedUnassignedCount { get; set; }

    /// <summary>Blocked items still unacknowledged.</summary>
    public int BlockedUnacknowledgedCount { get; set; }

    /// <summary>Failed but with attempts left — will heal without intervention.</summary>
    public int RetryingCount { get; set; }

    /// <summary>Left mid-flight by a crashed or restarted worker.</summary>
    public int StalledCount { get; set; }

    /// <summary>Past their scheduled retry time — a sign the queue processor is not running.</summary>
    public int RetryOverdueCount { get; set; }

    public int AcknowledgedCount { get; set; }
    public int AssignedToMeCount { get; set; }

    public int NewLastHourCount { get; set; }
    public int NewLast24hCount { get; set; }
    public int PreviousDayCount { get; set; }

    public DateTime? OldestOpenAtUtc { get; set; }
    public DateTime? NewestOpenAtUtc { get; set; }

    /// <summary>Soonest scheduled retry across every item still retrying.</summary>
    public DateTime? NextRetryAtUtc { get; set; }

    public int AgeUnder1hCount { get; set; }
    public int Age1To24hCount { get; set; }
    public int Age1To7dCount { get; set; }
    public int AgeOver7dCount { get; set; }
}

/// <summary>
/// One root cause: a group of items whose error text normalises to the same shape.
/// </summary>
public sealed class ExceptionCenterClusterDto
{
    public string Signature { get; set; } = string.Empty;

    /// <summary>Plain-language name for the failure family.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>What to do about it.</summary>
    public string Guidance { get; set; } = string.Empty;

    /// <summary>Coarse family used for colour and icon: Sap, Fiscal, Stock, Connectivity, Payment, Data, Unknown.</summary>
    public string Family { get; set; } = "Unknown";

    /// <summary>Representative raw error text.</summary>
    public string SampleError { get; set; } = string.Empty;

    public int Count { get; set; }
    public int BlockedCount { get; set; }
    public int RetryingCount { get; set; }
    public int RetryableCount { get; set; }

    public List<string> Sources { get; set; } = new();
    public List<string> Categories { get; set; } = new();
    public List<string> SampleReferences { get; set; } = new();

    public DateTime? FirstSeenUtc { get; set; }
    public DateTime? LastSeenUtc { get; set; }

    /// <summary>Money held up by this cause, largest currency first.</summary>
    public List<ExceptionCenterExposureDto> Exposure { get; set; } = new();

    /// <summary>Item keys that can be retried in bulk.</summary>
    public List<ExceptionCenterItemRefDto> RetryableItems { get; set; } = new();
}

public sealed class ExceptionCenterItemRefDto
{
    public string Source { get; set; } = string.Empty;

    /// <summary>Routing key, so a batch can mix int-keyed and Guid-keyed sources.</summary>
    public string ItemKey { get; set; } = string.Empty;
}

public sealed class ExceptionCenterBatchRetryRequestDto
{
    public List<ExceptionCenterItemRefDto> Items { get; set; } = new();
}

public sealed class ExceptionCenterBatchRetryResultDto
{
    public int RequestedCount { get; set; }
    public int RetriedCount { get; set; }
    public int FailedCount { get; set; }

    /// <summary>First few failures, so the operator sees why without reading the log.</summary>
    public List<string> Failures { get; set; } = new();
}

public sealed class ExceptionCenterSourceDto
{
    public string Source { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public int TotalCount { get; set; }
    public int BlockedCount { get; set; }
    public int RetryingCount { get; set; }
    public int StalledCount { get; set; }
    public DateTime? OldestOpenAtUtc { get; set; }
    public DateTime? LastFailureAtUtc { get; set; }
    public bool RetrySupported { get; set; }
}

/// <summary>
/// A quantity held up by the failures. Money uses <see cref="Currency"/>; volume
/// measures (transfer lines, order lines) leave it null and set <see cref="Unit"/>.
/// </summary>
public sealed class ExceptionCenterExposureDto
{
    public string Label { get; set; } = string.Empty;
    public string? Currency { get; set; }
    public string? Unit { get; set; }
    public decimal Amount { get; set; }
    public int ItemCount { get; set; }
}

public sealed class ExceptionCenterTrendPointDto
{
    public DateTime HourUtc { get; set; }
    public int Count { get; set; }
}

public sealed class ExceptionCenterItemDto
{
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// The item's int primary key, for the sources that have one; zero for Guid-keyed sources.
    /// Route on <see cref="ItemKey"/> instead.
    /// </summary>
    public int ItemId { get; set; }

    /// <summary>
    /// Identifies the item within its source regardless of how that source is keyed. For
    /// int-keyed sources this is the decimal id, so it is what the item was always addressed by.
    /// </summary>
    public string ItemKey { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Reference { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? SourceSystem { get; set; }
    public string? Provider { get; set; }
    public string? LastError { get; set; }
    public int RetryCount { get; set; }
    public int MaxRetries { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? OccurredAtUtc { get; set; }
    public DateTime? NextRetryAtUtc { get; set; }
    public DateTime? ProcessingStartedAtUtc { get; set; }
    public bool CanRetry { get; set; }
    public bool IsAcknowledged { get; set; }
    public DateTime? AcknowledgedAtUtc { get; set; }
    public string? AcknowledgedByUsername { get; set; }
    public string? AssignedToUsername { get; set; }
    public DateTime? AssignedAtUtc { get; set; }

    /// <summary>Triage bucket: Blocked, Retrying, Stalled.</summary>
    public string Triage { get; set; } = "Blocked";

    /// <summary>Signature of the cluster this item belongs to.</summary>
    public string? ClusterSignature { get; set; }

    /// <summary>Plain-language root cause for this item.</summary>
    public string? RootCause { get; set; }

    /// <summary>Failure family, matching <see cref="ExceptionCenterClusterDto.Family"/>.</summary>
    public string? Family { get; set; }

    /// <summary>Money on the document, when the source carries a value.</summary>
    public decimal? Amount { get; set; }
    public string? Currency { get; set; }

    /// <summary>Business context worth seeing without opening SAP: customer, route, warehouse.</summary>
    public string? Counterparty { get; set; }
    public string? Location { get; set; }
    public int? LineCount { get; set; }
    public string? CreatedBy { get; set; }

    /// <summary>True once the scheduled retry time has passed by a wide margin.</summary>
    public bool IsRetryOverdue { get; set; }
}
