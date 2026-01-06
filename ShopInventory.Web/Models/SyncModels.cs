namespace ShopInventory.Web.Models;

/// <summary>
/// Sync status and health monitoring models for the web application
/// </summary>
/// 
public class SapConnectionStatusModel
{
    public bool IsConnected { get; set; }
    public string? SessionId { get; set; }
    public DateTime? LastCheckedAt { get; set; }
    public DateTime? LastSuccessfulConnectionAt { get; set; }
    public string? ErrorMessage { get; set; }
    public int ConsecutiveFailures { get; set; }
}

public class SyncDashboardModel
{
    public SapConnectionStatusModel SapConnection { get; set; } = new();
    public List<CacheSyncStatusModel> CacheStatuses { get; set; } = new();
    public OfflineQueueStatusModel OfflineQueue { get; set; } = new();
    public string OverallHealthStatus { get; set; } = "Unknown";
    public int ActiveIssuesCount { get; set; }
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
}

public class CacheSyncStatusModel
{
    public string EntityType { get; set; } = string.Empty;
    public int CachedCount { get; set; }
    public DateTime? LastSyncAt { get; set; }
    public string SyncStatus { get; set; } = "Unknown";
    public string SyncFrequency { get; set; } = "Unknown";
    public string? LastError { get; set; }

    public string TimeAgo => LastSyncAt.HasValue
        ? GetTimeAgo(DateTime.UtcNow - LastSyncAt.Value)
        : "Never";

    public string StatusBadgeClass => SyncStatus switch
    {
        "Synced" => "bg-success",
        "Syncing" => "bg-info",
        "Stale" => "bg-warning",
        "Error" => "bg-danger",
        _ => "bg-secondary"
    };

    private static string GetTimeAgo(TimeSpan span)
    {
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays}d ago";
        return $"{(int)(span.TotalDays / 7)}w ago";
    }
}

public class OfflineQueueStatusModel
{
    public int TotalCount { get; set; }
    public int PendingCount { get; set; }
    public int ProcessingCount { get; set; }
    public int FailedCount { get; set; }
    public int CompletedCount { get; set; }
    public DateTime? OldestPendingAt { get; set; }
    public DateTime? LastProcessedAt { get; set; }
}

public class QueuedTransactionModel
{
    public int Id { get; set; }
    public string TransactionType { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string? EntityId { get; set; }
    public string Status { get; set; } = string.Empty;
    public int RetryCount { get; set; }
    public int MaxRetries { get; set; } = 3;
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public string? CreatedBy { get; set; }

    public string TimeAgo
    {
        get
        {
            var span = DateTime.UtcNow - CreatedAt;
            if (span.TotalMinutes < 1) return "just now";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
            if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
            return $"{(int)span.TotalDays}d ago";
        }
    }

    public string StatusBadgeClass => Status switch
    {
        "Pending" => "bg-warning",
        "Processing" => "bg-info",
        "Completed" => "bg-success",
        "Failed" => "bg-danger",
        "Cancelled" => "bg-secondary",
        _ => "bg-secondary"
    };
}

public class ConnectionLogModel
{
    public int Id { get; set; }
    public DateTime Timestamp { get; set; }
    public string Action { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Message { get; set; }
    public int? DurationMs { get; set; }
}

public class SystemHealthModel
{
    public string Status { get; set; } = "Unknown";
    public bool SapConnected { get; set; }
    public bool DatabaseConnected { get; set; }
    public int PendingQueueItems { get; set; }
    public int FailedQueueItems { get; set; }
    public int StaleCacheEntities { get; set; }
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;

    public string StatusBadgeClass => Status switch
    {
        "Healthy" => "bg-success",
        "Degraded" => "bg-warning",
        "Unhealthy" => "bg-danger",
        _ => "bg-secondary"
    };
}

// Note: NotificationModel and NotificationListResponse are defined in ReportModels.cs
