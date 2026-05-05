using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

[Index(nameof(ExternalReference), IsUnique = true)]
[Index(nameof(Status), nameof(Priority), nameof(CreatedAt))]
[Index(nameof(CustomerCode))]
public class IncomingPaymentQueueEntity
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(100)]
    public string ExternalReference { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    public string CustomerCode { get; set; } = string.Empty;

    [Required]
    public string PaymentPayload { get; set; } = string.Empty;

    public IncomingPaymentQueueStatus Status { get; set; } = IncomingPaymentQueueStatus.Pending;

    public int RetryCount { get; set; }

    public int MaxRetries { get; set; } = 3;

    [MaxLength(2000)]
    public string? LastError { get; set; }

    [MaxLength(50)]
    public string? SapDocEntry { get; set; }

    public int? SapDocNum { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? ProcessingStartedAt { get; set; }

    public DateTime? ProcessedAt { get; set; }

    public DateTime? NextRetryAt { get; set; }

    [MaxLength(50)]
    public string SourceSystem { get; set; } = "API";

    public int Priority { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalAmount { get; set; }

    [MaxLength(10)]
    public string Currency { get; set; } = "USD";

    [MaxLength(100)]
    public string? CreatedBy { get; set; }

    [MaxLength(500)]
    public string? Remarks { get; set; }
}

public enum IncomingPaymentQueueStatus
{
    Pending = 0,
    Processing = 1,
    Completed = 2,
    Failed = 3,
    RequiresReview = 4,
    Cancelled = 5
}