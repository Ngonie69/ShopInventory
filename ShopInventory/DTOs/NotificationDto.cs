namespace ShopInventory.DTOs;

#region Notification DTOs

/// <summary>
/// Notification DTO
/// </summary>
public class NotificationDto
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty; // Info, Warning, Error, Success, Alert
    public string Category { get; set; } = string.Empty; // LowStock, Payment, Invoice, System
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public string? ActionUrl { get; set; }
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ReadAt { get; set; }
    public string? CreatedBy { get; set; }
}

/// <summary>
/// Notification list response
/// </summary>
public class NotificationListResponseDto
{
    public int TotalCount { get; set; }
    public int UnreadCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public List<NotificationDto> Notifications { get; set; } = new();
}

/// <summary>
/// Create notification request
/// </summary>
public class CreateNotificationRequest
{
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Type { get; set; } = "Info";
    public string Category { get; set; } = "System";
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public string? ActionUrl { get; set; }
    public string? TargetUsername { get; set; } // null = broadcast to all
    public string? TargetRole { get; set; } // null = all roles
}

/// <summary>
/// Mark notifications as read request
/// </summary>
public class MarkNotificationsReadRequest
{
    public List<int>? NotificationIds { get; set; } // null = mark all as read
}

/// <summary>
/// Notification settings for a user
/// </summary>
public class NotificationSettingsDto
{
    public bool EmailNotificationsEnabled { get; set; } = true;
    public bool LowStockAlerts { get; set; } = true;
    public bool PaymentAlerts { get; set; } = true;
    public bool InvoiceAlerts { get; set; } = true;
    public bool SystemAlerts { get; set; } = true;
    public int LowStockThreshold { get; set; } = 10;
    public List<string> EmailRecipients { get; set; } = new();
}

#endregion

#region Sync Status DTOs

/// <summary>
/// SAP connection status
/// </summary>
public class SapConnectionStatusDto
{
    public bool IsConnected { get; set; }
    public string Status { get; set; } = string.Empty; // Connected, Disconnected, Error, Unknown
    public DateTime? LastConnectedAt { get; set; }
    public DateTime? LastErrorAt { get; set; }
    public string? LastError { get; set; }
    public int ConsecutiveFailures { get; set; }
    public double? ResponseTimeMs { get; set; }
    public string? SapVersion { get; set; }
    public string? CompanyDb { get; set; }
}

/// <summary>
/// Sync status for all cache types
/// </summary>
public class SyncStatusDashboardDto
{
    public DateTime GeneratedAt { get; set; }
    public SapConnectionStatusDto SapConnection { get; set; } = new();
    public List<CacheSyncStatusDto> CacheStatuses { get; set; } = new();
    public OfflineQueueStatusDto OfflineQueue { get; set; } = new();
    public SyncHealthSummaryDto HealthSummary { get; set; } = new();
}

/// <summary>
/// Individual cache sync status
/// </summary>
public class CacheSyncStatusDto
{
    public string CacheKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public DateTime? LastSyncedAt { get; set; }
    public int ItemCount { get; set; }
    public bool IsStale { get; set; }
    public int StaleMinutes { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastErrorAt { get; set; }
    public string Status { get; set; } = string.Empty; // Synced, Syncing, Stale, Error
}

/// <summary>
/// Offline queue status
/// </summary>
public class OfflineQueueStatusDto
{
    public int PendingCount { get; set; }
    public int FailedCount { get; set; }
    public int ProcessedCount { get; set; }
    public DateTime? OldestPendingAt { get; set; }
    public DateTime? LastProcessedAt { get; set; }
    public List<QueuedTransactionDto> PendingTransactions { get; set; } = new();
}

/// <summary>
/// Queued transaction entry
/// </summary>
public class QueuedTransactionDto
{
    public int Id { get; set; }
    public string TransactionType { get; set; } = string.Empty; // Invoice, Payment, Transfer
    public string Status { get; set; } = string.Empty; // Pending, Processing, Failed, Completed
    public DateTime CreatedAt { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public string? CreatedBy { get; set; }
    public string? Summary { get; set; }
}

/// <summary>
/// Sync health summary
/// </summary>
public class SyncHealthSummaryDto
{
    public string OverallHealth { get; set; } = string.Empty; // Healthy, Warning, Critical
    public int HealthScore { get; set; } // 0-100
    public List<string> Issues { get; set; } = new();
    public List<string> Recommendations { get; set; } = new();
}

/// <summary>
/// Trigger manual sync request
/// </summary>
public class TriggerSyncRequest
{
    public string CacheKey { get; set; } = string.Empty; // Products, Prices, BusinessPartners, Warehouses, GLAccounts, All
    public bool ForceFullSync { get; set; } = false;
}

/// <summary>
/// Sync result response
/// </summary>
public class SyncResultDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string CacheKey { get; set; } = string.Empty;
    public int ItemsSynced { get; set; }
    public int ItemsAdded { get; set; }
    public int ItemsUpdated { get; set; }
    public int ItemsRemoved { get; set; }
    public double DurationMs { get; set; }
    public DateTime CompletedAt { get; set; }
}

/// <summary>
/// Retry queued transaction request
/// </summary>
public class RetryQueuedTransactionRequest
{
    public int TransactionId { get; set; }
}

#endregion

#region Email DTOs

/// <summary>
/// Email configuration
/// </summary>
public class EmailConfigurationDto
{
    public string SmtpServer { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string Username { get; set; } = string.Empty;
    public string FromEmail { get; set; } = string.Empty;
    public string FromName { get; set; } = string.Empty;
}

/// <summary>
/// Send email request
/// </summary>
public class SendEmailRequest
{
    public List<string> To { get; set; } = new();
    public List<string>? Cc { get; set; }
    public List<string>? Bcc { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public bool IsHtml { get; set; } = true;
    public List<EmailAttachmentDto>? Attachments { get; set; }
}

/// <summary>
/// Email attachment
/// </summary>
public class EmailAttachmentDto
{
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public byte[] Content { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// Email sent response
/// </summary>
public class EmailSentResponseDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? MessageId { get; set; }
    public DateTime SentAt { get; set; }
}

/// <summary>
/// Test email request
/// </summary>
public class TestEmailRequest
{
    public string ToEmail { get; set; } = string.Empty;
}

#endregion
