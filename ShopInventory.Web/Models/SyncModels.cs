using System.Text.Json.Serialization;

namespace ShopInventory.Web.Models;

/// <summary>
/// Sync status and health monitoring models for the web application
/// </summary>
public class SapConnectionStatusModel
{
    public bool IsConnected { get; set; }
    public string? Status { get; set; }
    public DateTime? LastConnectedAt { get; set; }
    public DateTime? LastErrorAt { get; set; }
    public string? LastError { get; set; }
    public int ConsecutiveFailures { get; set; }
    public double? ResponseTimeMs { get; set; }
    public string? CompanyDb { get; set; }
}

public class SyncDashboardModel
{
    public SapConnectionStatusModel SapConnection { get; set; } = new();
    public List<CacheSyncStatusModel> CacheStatuses { get; set; } = new();
    public OfflineQueueStatusModel OfflineQueue { get; set; } = new();
    public SyncHealthSummaryModel HealthSummary { get; set; } = new();
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    // Convenience properties derived from HealthSummary
    [JsonIgnore]
    public string OverallHealthStatus => HealthSummary?.OverallHealth ?? "Unknown";
    [JsonIgnore]
    public int ActiveIssuesCount => HealthSummary?.Issues?.Count ?? 0;
}

public class SyncHealthSummaryModel
{
    public string OverallHealth { get; set; } = "Unknown";
    public int HealthScore { get; set; }
    public List<string> Issues { get; set; } = new();
    public List<string> Recommendations { get; set; } = new();
}

public class CacheSyncStatusModel
{
    public string CacheKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public DateTime? LastSyncedAt { get; set; }
    public int ItemCount { get; set; }
    public bool IsStale { get; set; }
    public int StaleMinutes { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastErrorAt { get; set; }
    public string Status { get; set; } = "Unknown";

    [JsonIgnore]
    public string TimeAgo => LastSyncedAt.HasValue
        ? GetTimeAgo(DateTime.UtcNow - LastSyncedAt.Value)
        : "Never";

    [JsonIgnore]
    public string StatusBadgeClass => Status switch
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
    public int PendingCount { get; set; }
    public int FailedCount { get; set; }
    public int ProcessedCount { get; set; }
    public DateTime? OldestPendingAt { get; set; }
    public DateTime? LastProcessedAt { get; set; }
    public List<QueuedTransactionModel> PendingTransactions { get; set; } = new();
}

public class QueuedTransactionModel
{
    public int Id { get; set; }
    public string TransactionType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public string? CreatedBy { get; set; }
    public string? Summary { get; set; }

    [JsonIgnore]
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

    [JsonIgnore]
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
    public DateTime CheckedAt { get; set; }
    public bool IsSuccess { get; set; }
    public string? Endpoint { get; set; }
    public string? ErrorMessage { get; set; }
    public double? ResponseTimeMs { get; set; }
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
