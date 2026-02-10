using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// Represents an invoice queued for batch posting to SAP
/// </summary>
[Index(nameof(ExternalReference), IsUnique = true)]
[Index(nameof(Status), nameof(Priority), nameof(CreatedAt))]
[Index(nameof(ReservationId))]
[Index(nameof(CustomerCode))]
public class InvoiceQueueEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Reference to the stock reservation that holds the stock
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string ReservationId { get; set; } = string.Empty;

    /// <summary>
    /// External reference from the source system (e.g., desktop invoice number)
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string ExternalReference { get; set; } = string.Empty;

    /// <summary>
    /// SAP Business Partner code
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string CustomerCode { get; set; } = string.Empty;

    /// <summary>
    /// JSON serialized invoice request payload
    /// </summary>
    [Required]
    public string InvoicePayload { get; set; } = string.Empty;

    /// <summary>
    /// Current queue status
    /// </summary>
    public InvoiceQueueStatus Status { get; set; } = InvoiceQueueStatus.Pending;

    /// <summary>
    /// Number of processing attempts
    /// </summary>
    public int RetryCount { get; set; } = 0;

    /// <summary>
    /// Maximum allowed retry attempts
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Last error message if failed
    /// </summary>
    [MaxLength(2000)]
    public string? LastError { get; set; }

    /// <summary>
    /// SAP Document Entry after successful posting
    /// </summary>
    [MaxLength(50)]
    public string? SapDocEntry { get; set; }

    /// <summary>
    /// SAP Document Number after successful posting
    /// </summary>
    public int? SapDocNum { get; set; }

    /// <summary>
    /// Fiscal device number from fiscalization
    /// </summary>
    [MaxLength(100)]
    public string? FiscalDeviceNumber { get; set; }

    /// <summary>
    /// Fiscal receipt number from fiscalization
    /// </summary>
    [MaxLength(100)]
    public string? FiscalReceiptNumber { get; set; }

    /// <summary>
    /// When the invoice was queued
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When processing started
    /// </summary>
    public DateTime? ProcessingStartedAt { get; set; }

    /// <summary>
    /// When processing completed (success or final failure)
    /// </summary>
    public DateTime? ProcessedAt { get; set; }

    /// <summary>
    /// When the next retry should be attempted
    /// </summary>
    public DateTime? NextRetryAt { get; set; }

    /// <summary>
    /// Source system identifier
    /// </summary>
    [MaxLength(50)]
    public string SourceSystem { get; set; } = "Desktop";

    /// <summary>
    /// Processing priority (higher = more urgent)
    /// </summary>
    public int Priority { get; set; } = 0;

    /// <summary>
    /// Warehouse code for the invoice
    /// </summary>
    [MaxLength(20)]
    public string? WarehouseCode { get; set; }

    /// <summary>
    /// Total invoice amount
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalAmount { get; set; }

    /// <summary>
    /// Currency code
    /// </summary>
    [MaxLength(10)]
    public string Currency { get; set; } = "USD";

    /// <summary>
    /// Whether fiscalization is required
    /// </summary>
    public bool RequiresFiscalization { get; set; } = false;

    /// <summary>
    /// Whether fiscalization was successful
    /// </summary>
    public bool? FiscalizationSuccess { get; set; }

    /// <summary>
    /// User who created the queue entry
    /// </summary>
    [MaxLength(100)]
    public string? CreatedBy { get; set; }

    /// <summary>
    /// Additional notes or comments
    /// </summary>
    [MaxLength(500)]
    public string? Notes { get; set; }

    // Note: Navigation to StockReservationEntity is not used because ReservationId
    // links to the StockReservationEntity.ReservationId (business key), not the PK.
    // Use the ReservationId string field to look up reservations.
}

/// <summary>
/// Invoice queue processing status
/// </summary>
public enum InvoiceQueueStatus
{
    /// <summary>Waiting to be processed</summary>
    Pending = 0,

    /// <summary>Currently being processed</summary>
    Processing = 1,

    /// <summary>Successfully posted to SAP</summary>
    Completed = 2,

    /// <summary>Failed but will retry</summary>
    Failed = 3,

    /// <summary>Failed permanently, needs manual review</summary>
    RequiresReview = 4,

    /// <summary>Cancelled by user</summary>
    Cancelled = 5,

    /// <summary>Partially completed (posted but fiscalization pending)</summary>
    PartiallyCompleted = 6
}
