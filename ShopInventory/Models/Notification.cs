using System.ComponentModel.DataAnnotations;

namespace ShopInventory.Models;

/// <summary>
/// Notification entity for storing user notifications
/// </summary>
public class Notification
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Target user ID (null for broadcast notifications)
    /// </summary>
    public Guid? UserId { get; set; }

    /// <summary>
    /// Target username (null for broadcast)
    /// </summary>
    [MaxLength(50)]
    public string? TargetUsername { get; set; }

    /// <summary>
    /// Target role for role-based notifications
    /// </summary>
    [MaxLength(50)]
    public string? TargetRole { get; set; }

    /// <summary>
    /// Notification title
    /// </summary>
    [Required]
    [MaxLength(200)]
    public required string Title { get; set; }

    /// <summary>
    /// Notification message body
    /// </summary>
    [Required]
    [MaxLength(1000)]
    public required string Message { get; set; }

    /// <summary>
    /// Notification type (Info, Warning, Error, Success, Alert)
    /// </summary>
    [Required]
    [MaxLength(20)]
    public required string Type { get; set; }

    /// <summary>
    /// Notification category (LowStock, Payment, Invoice, System, Security)
    /// </summary>
    [Required]
    [MaxLength(50)]
    public required string Category { get; set; }

    /// <summary>
    /// Related entity type (e.g., Invoice, Product, Payment)
    /// </summary>
    [MaxLength(50)]
    public string? EntityType { get; set; }

    /// <summary>
    /// Related entity ID
    /// </summary>
    [MaxLength(100)]
    public string? EntityId { get; set; }

    /// <summary>
    /// URL to navigate to when notification is clicked
    /// </summary>
    [MaxLength(500)]
    public string? ActionUrl { get; set; }

    /// <summary>
    /// Whether the notification has been read
    /// </summary>
    public bool IsRead { get; set; } = false;

    /// <summary>
    /// When the notification was read
    /// </summary>
    public DateTime? ReadAt { get; set; }

    /// <summary>
    /// When the notification was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Who created the notification (system or username)
    /// </summary>
    [MaxLength(100)]
    public string? CreatedBy { get; set; }

    /// <summary>
    /// Expiration date for auto-cleanup
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// Navigation property to user
    /// </summary>
    public User? User { get; set; }
}

/// <summary>
/// Offline queue entity for storing transactions when SAP is unavailable
/// </summary>
public class OfflineQueueItem
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Type of transaction (Invoice, Payment, InventoryTransfer)
    /// </summary>
    [Required]
    [MaxLength(50)]
    public required string TransactionType { get; set; }

    /// <summary>
    /// Current status (Pending, Processing, Completed, Failed, Cancelled)
    /// </summary>
    [Required]
    [MaxLength(20)]
    public required string Status { get; set; }

    /// <summary>
    /// JSON serialized transaction data
    /// </summary>
    [Required]
    public required string TransactionData { get; set; }

    /// <summary>
    /// Brief summary of the transaction
    /// </summary>
    [MaxLength(500)]
    public string? Summary { get; set; }

    /// <summary>
    /// Number of processing attempts
    /// </summary>
    public int AttemptCount { get; set; } = 0;

    /// <summary>
    /// Maximum retry attempts before marking as failed
    /// </summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>
    /// Last error message if failed
    /// </summary>
    [MaxLength(1000)]
    public string? LastError { get; set; }

    /// <summary>
    /// When the last attempt was made
    /// </summary>
    public DateTime? LastAttemptAt { get; set; }

    /// <summary>
    /// When to retry next (for exponential backoff)
    /// </summary>
    public DateTime? NextRetryAt { get; set; }

    /// <summary>
    /// When the item was queued
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Who created the transaction
    /// </summary>
    [MaxLength(100)]
    public string? CreatedBy { get; set; }

    /// <summary>
    /// When the transaction was completed
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// SAP document entry if successfully created
    /// </summary>
    public int? SapDocEntry { get; set; }

    /// <summary>
    /// SAP document number if successfully created
    /// </summary>
    public int? SapDocNum { get; set; }

    /// <summary>
    /// Priority (1 = highest, 10 = lowest)
    /// </summary>
    public int Priority { get; set; } = 5;
}

/// <summary>
/// Email queue for outgoing emails
/// </summary>
public class EmailQueueItem
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Recipient email addresses (JSON array)
    /// </summary>
    [Required]
    [MaxLength(1000)]
    public required string ToAddresses { get; set; }

    /// <summary>
    /// CC addresses (JSON array)
    /// </summary>
    [MaxLength(1000)]
    public string? CcAddresses { get; set; }

    /// <summary>
    /// BCC addresses (JSON array)
    /// </summary>
    [MaxLength(1000)]
    public string? BccAddresses { get; set; }

    /// <summary>
    /// Email subject
    /// </summary>
    [Required]
    [MaxLength(500)]
    public required string Subject { get; set; }

    /// <summary>
    /// Email body
    /// </summary>
    [Required]
    public required string Body { get; set; }

    /// <summary>
    /// Whether body is HTML
    /// </summary>
    public bool IsHtml { get; set; } = true;

    /// <summary>
    /// Status (Pending, Sending, Sent, Failed)
    /// </summary>
    [Required]
    [MaxLength(20)]
    public required string Status { get; set; }

    /// <summary>
    /// Attempt count
    /// </summary>
    public int AttemptCount { get; set; } = 0;

    /// <summary>
    /// Last error
    /// </summary>
    [MaxLength(1000)]
    public string? LastError { get; set; }

    /// <summary>
    /// When queued
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When sent successfully
    /// </summary>
    public DateTime? SentAt { get; set; }

    /// <summary>
    /// Email category for filtering
    /// </summary>
    [MaxLength(50)]
    public string? Category { get; set; }
}

/// <summary>
/// SAP connection health tracking
/// </summary>
public class SapConnectionLog
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Whether the connection was successful
    /// </summary>
    public bool IsSuccess { get; set; }

    /// <summary>
    /// Response time in milliseconds
    /// </summary>
    public double? ResponseTimeMs { get; set; }

    /// <summary>
    /// Error message if failed
    /// </summary>
    [MaxLength(1000)]
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Endpoint tested
    /// </summary>
    [MaxLength(200)]
    public string? Endpoint { get; set; }

    /// <summary>
    /// When the check was performed
    /// </summary>
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// User notification settings
/// </summary>
public class UserNotificationSettings
{
    [Key]
    public Guid UserId { get; set; }

    /// <summary>
    /// Email notifications enabled
    /// </summary>
    public bool EmailNotificationsEnabled { get; set; } = true;

    /// <summary>
    /// Low stock alerts enabled
    /// </summary>
    public bool LowStockAlerts { get; set; } = true;

    /// <summary>
    /// Payment alerts enabled
    /// </summary>
    public bool PaymentAlerts { get; set; } = true;

    /// <summary>
    /// Invoice alerts enabled
    /// </summary>
    public bool InvoiceAlerts { get; set; } = true;

    /// <summary>
    /// System alerts enabled
    /// </summary>
    public bool SystemAlerts { get; set; } = true;

    /// <summary>
    /// Low stock threshold for alerts
    /// </summary>
    public int LowStockThreshold { get; set; } = 10;

    /// <summary>
    /// Navigation property
    /// </summary>
    public User? User { get; set; }
}
