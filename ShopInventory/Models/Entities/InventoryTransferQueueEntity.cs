using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// Represents an inventory transfer request queued for batch posting to SAP
/// </summary>
[Index(nameof(ExternalReference), IsUnique = true)]
[Index(nameof(Status), nameof(Priority), nameof(CreatedAt))]
[Index(nameof(FromWarehouse))]
[Index(nameof(ToWarehouse))]
public class InventoryTransferQueueEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// External reference from the source system (e.g., desktop transfer request number)
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string ExternalReference { get; set; } = string.Empty;

    /// <summary>
    /// Source warehouse code
    /// </summary>
    [Required]
    [MaxLength(20)]
    public string FromWarehouse { get; set; } = string.Empty;

    /// <summary>
    /// Destination warehouse code
    /// </summary>
    [Required]
    [MaxLength(20)]
    public string ToWarehouse { get; set; } = string.Empty;

    /// <summary>
    /// JSON serialized inventory transfer request payload
    /// </summary>
    [Required]
    public string TransferPayload { get; set; } = string.Empty;

    /// <summary>
    /// Current queue status
    /// </summary>
    public InventoryTransferQueueStatus Status { get; set; } = InventoryTransferQueueStatus.Pending;

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
    /// When the transfer was queued
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
    /// Total quantity of items being transferred
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalQuantity { get; set; }

    /// <summary>
    /// Number of line items in the transfer
    /// </summary>
    public int LineCount { get; set; }

    /// <summary>
    /// User who created the queue entry
    /// </summary>
    [MaxLength(100)]
    public string? CreatedBy { get; set; }

    /// <summary>
    /// Additional notes or comments
    /// </summary>
    [MaxLength(500)]
    public string? Comments { get; set; }

    /// <summary>
    /// Journal memo for SAP posting
    /// </summary>
    [MaxLength(500)]
    public string? JournalMemo { get; set; }

    /// <summary>
    /// Due date for the transfer
    /// </summary>
    public DateTime? DueDate { get; set; }

    /// <summary>
    /// Whether this is a transfer request (requires approval) or direct transfer
    /// </summary>
    public bool IsTransferRequest { get; set; } = true;

    /// <summary>
    /// Reference to the stock reservation that holds the stock (if applicable)
    /// </summary>
    [MaxLength(50)]
    public string? ReservationId { get; set; }
}

/// <summary>
/// Inventory transfer queue processing status
/// </summary>
public enum InventoryTransferQueueStatus
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
    Cancelled = 5
}
