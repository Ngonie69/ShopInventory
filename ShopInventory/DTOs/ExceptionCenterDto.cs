namespace ShopInventory.DTOs;

public sealed class ExceptionCenterDashboardDto
{
    public int OpenCount { get; set; }
    public int RequiresReviewCount { get; set; }
    public int RetryScheduledCount { get; set; }
    public int SapIssueCount { get; set; }
    public int RevmaxIssueCount { get; set; }
    public int SyncIssueCount { get; set; }
    public int PaymentIssueCount { get; set; }
    public List<ExceptionCenterItemDto> Items { get; set; } = new();
}

public sealed class ExceptionCenterItemDto
{
    public string Source { get; set; } = string.Empty;
    public int ItemId { get; set; }
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