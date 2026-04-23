using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// Durable queue for mobile order post-save processing such as SAP price enrichment and notifications.
/// </summary>
[Index(nameof(SalesOrderId), IsUnique = true)]
[Index(nameof(Status), nameof(Priority), nameof(CreatedAt))]
[Index(nameof(OrderNumber))]
public class MobileOrderPostProcessingQueueEntity
{
    [Key]
    public int Id { get; set; }

    public int SalesOrderId { get; set; }

    [Required]
    [MaxLength(50)]
    public string OrderNumber { get; set; } = string.Empty;

    public MobileOrderPostProcessingQueueStatus Status { get; set; } = MobileOrderPostProcessingQueueStatus.Pending;

    public int RetryCount { get; set; }

    public int MaxRetries { get; set; } = 5;

    [MaxLength(2000)]
    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? ProcessingStartedAt { get; set; }

    public DateTime? ProcessedAt { get; set; }

    public DateTime? NextRetryAt { get; set; }

    public int Priority { get; set; }

    public int LineCount { get; set; }

    public DateTime? PricesResolvedAt { get; set; }

    public DateTime? NotificationSentAt { get; set; }
}

public enum MobileOrderPostProcessingQueueStatus
{
    Pending = 0,
    Processing = 1,
    Completed = 2,
    Failed = 3,
    RequiresReview = 4,
    Cancelled = 5
}