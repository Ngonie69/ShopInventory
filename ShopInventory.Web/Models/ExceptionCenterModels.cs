namespace ShopInventory.Web.Models;

public sealed class ExceptionCenterDashboardModel
{
    public int OpenCount { get; set; }
    public int RequiresReviewCount { get; set; }
    public int RetryScheduledCount { get; set; }
    public int SapIssueCount { get; set; }
    public int RevmaxIssueCount { get; set; }
    public int SyncIssueCount { get; set; }
    public int PaymentIssueCount { get; set; }
    public List<ExceptionCenterItemModel> Items { get; set; } = new();
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
    public bool CanRetry { get; set; }
    public bool IsAcknowledged { get; set; }
    public DateTime? AcknowledgedAtUtc { get; set; }
    public string? AcknowledgedByUsername { get; set; }
    public string? AssignedToUsername { get; set; }
    public DateTime? AssignedAtUtc { get; set; }
}