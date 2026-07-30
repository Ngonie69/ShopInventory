namespace ShopInventory.Web.Models;

/// <summary>
/// Mirror of ShopInventory.DTOs.ExceptionCenterDashboardDto. See that file for
/// what each group of numbers means.
/// </summary>
public sealed class ExceptionCenterDashboardModel
{
    public DateTime GeneratedAtUtc { get; set; }
    public ExceptionCenterTriageModel Triage { get; set; } = new();
    public List<ExceptionCenterClusterModel> Clusters { get; set; } = new();
    public List<ExceptionCenterSourceModel> Sources { get; set; } = new();
    public List<ExceptionCenterExposureModel> Exposure { get; set; } = new();
    public List<ExceptionCenterTrendPointModel> Trend { get; set; } = new();
    public List<ExceptionCenterItemModel> Items { get; set; } = new();
    public int TotalItemCount { get; set; }
    public bool ItemsTruncated { get; set; }
    public bool AnalysisTruncated { get; set; }
}

public sealed class ExceptionCenterTriageModel
{
    public int BlockedCount { get; set; }
    public int BlockedUnassignedCount { get; set; }
    public int BlockedUnacknowledgedCount { get; set; }
    public int RetryingCount { get; set; }
    public int StalledCount { get; set; }
    public int RetryOverdueCount { get; set; }
    public int AcknowledgedCount { get; set; }
    public int AssignedToMeCount { get; set; }
    public int NewLastHourCount { get; set; }
    public int NewLast24hCount { get; set; }
    public int PreviousDayCount { get; set; }
    public DateTime? OldestOpenAtUtc { get; set; }
    public DateTime? NewestOpenAtUtc { get; set; }
    public DateTime? NextRetryAtUtc { get; set; }
    public int AgeUnder1hCount { get; set; }
    public int Age1To24hCount { get; set; }
    public int Age1To7dCount { get; set; }
    public int AgeOver7dCount { get; set; }
}

public sealed class ExceptionCenterClusterModel
{
    public string Signature { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Guidance { get; set; } = string.Empty;
    public string Family { get; set; } = "Unknown";
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
    public List<ExceptionCenterExposureModel> Exposure { get; set; } = new();
    public List<ExceptionCenterItemRefModel> RetryableItems { get; set; } = new();
}

public sealed class ExceptionCenterItemRefModel
{
    public string Source { get; set; } = string.Empty;

    /// <summary>Routing key, so a batch can mix int-keyed and Guid-keyed sources.</summary>
    public string ItemKey { get; set; } = string.Empty;
}

public sealed class ExceptionCenterBatchRetryRequestModel
{
    public List<ExceptionCenterItemRefModel> Items { get; set; } = new();
}

public sealed class ExceptionCenterBatchRetryResultModel
{
    public int RequestedCount { get; set; }
    public int RetriedCount { get; set; }
    public int FailedCount { get; set; }
    public List<string> Failures { get; set; } = new();
}

public sealed class ExceptionCenterSourceModel
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

public sealed class ExceptionCenterExposureModel
{
    public string Label { get; set; } = string.Empty;
    public string? Currency { get; set; }
    public string? Unit { get; set; }
    public decimal Amount { get; set; }
    public int ItemCount { get; set; }
}

public sealed class ExceptionCenterTrendPointModel
{
    public DateTime HourUtc { get; set; }
    public int Count { get; set; }
}

public sealed class ExceptionCenterItemModel
{
    public string Source { get; set; } = string.Empty;

    /// <summary>The item's int primary key where it has one; zero for Guid-keyed sources.</summary>
    public int ItemId { get; set; }

    /// <summary>Identifies the item within its source, whatever the shape of its primary key.</summary>
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
    public string Triage { get; set; } = "Blocked";
    public string? ClusterSignature { get; set; }
    public string? RootCause { get; set; }
    public string? Family { get; set; }
    public decimal? Amount { get; set; }
    public string? Currency { get; set; }
    public string? Counterparty { get; set; }
    public string? Location { get; set; }
    public int? LineCount { get; set; }
    public string? CreatedBy { get; set; }
    public bool IsRetryOverdue { get; set; }
}
