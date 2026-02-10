using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// Entity for tracking stock reservations from external systems (desktop app, etc.)
/// Reservations hold stock for committed transactions that haven't been posted to SAP yet.
/// This prevents negative quantities by accounting for pending sales.
/// </summary>
[Table("StockReservations")]
[Index(nameof(ReservationId), IsUnique = true)]
[Index(nameof(ExternalReferenceId), IsUnique = true)]
[Index(nameof(ExpiresAt))]
[Index(nameof(Status))]
[Index(nameof(CardCode))]
[Index(nameof(SourceSystem))]
public class StockReservationEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Unique identifier for this reservation (GUID)
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string ReservationId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// External reference ID from the source system (e.g., desktop app invoice number)
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string ExternalReferenceId { get; set; } = string.Empty;

    /// <summary>
    /// Source system identifier (e.g., "DESKTOP_APP", "POS_TERMINAL_1")
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string SourceSystem { get; set; } = string.Empty;

    /// <summary>
    /// Type of document creating the reservation
    /// </summary>
    [Required]
    [MaxLength(30)]
    public string DocumentType { get; set; } = "Invoice";

    /// <summary>
    /// Customer card code for the reservation
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string CardCode { get; set; } = string.Empty;

    /// <summary>
    /// Customer name
    /// </summary>
    [MaxLength(200)]
    public string? CardName { get; set; }

    /// <summary>
    /// Total reserved value
    /// </summary>
    [Precision(18, 2)]
    public decimal TotalValue { get; set; }

    /// <summary>
    /// Currency code
    /// </summary>
    [MaxLength(10)]
    public string? Currency { get; set; }

    /// <summary>
    /// Current status of the reservation
    /// </summary>
    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = ReservationStatus.Pending;

    /// <summary>
    /// When the reservation was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the reservation expires (auto-release after this time)
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// When the reservation was confirmed/posted to SAP
    /// </summary>
    public DateTime? ConfirmedAt { get; set; }

    /// <summary>
    /// SAP DocEntry after posting
    /// </summary>
    public int? SAPDocEntry { get; set; }

    /// <summary>
    /// SAP DocNum after posting
    /// </summary>
    public int? SAPDocNum { get; set; }

    /// <summary>
    /// When the reservation was cancelled
    /// </summary>
    public DateTime? CancelledAt { get; set; }

    /// <summary>
    /// Reason for cancellation
    /// </summary>
    [MaxLength(500)]
    public string? CancellationReason { get; set; }

    /// <summary>
    /// Last time this reservation was renewed
    /// </summary>
    public DateTime? LastRenewedAt { get; set; }

    /// <summary>
    /// Number of times this reservation has been renewed
    /// </summary>
    public int RenewalCount { get; set; }

    /// <summary>
    /// User/API key that created the reservation
    /// </summary>
    [MaxLength(100)]
    public string? CreatedBy { get; set; }

    /// <summary>
    /// Additional metadata (JSON)
    /// </summary>
    public string? Metadata { get; set; }

    // Navigation properties
    public ICollection<StockReservationLineEntity> Lines { get; set; } = new List<StockReservationLineEntity>();
}

/// <summary>
/// Entity for individual line items in a stock reservation
/// </summary>
[Table("StockReservationLines")]
[Index(nameof(ItemCode), nameof(WarehouseCode))]
public class StockReservationLineEntity
{
    [Key]
    public int Id { get; set; }

    public int ReservationId { get; set; }

    /// <summary>
    /// Line number in the source document
    /// </summary>
    public int LineNum { get; set; }

    /// <summary>
    /// Item code being reserved
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string ItemCode { get; set; } = string.Empty;

    /// <summary>
    /// Item description
    /// </summary>
    [MaxLength(200)]
    public string? ItemDescription { get; set; }

    /// <summary>
    /// Reserved quantity (in inventory UoM) - must be positive
    /// </summary>
    [Precision(18, 6)]
    [Range(0.000001, double.MaxValue, ErrorMessage = "Reserved quantity must be greater than zero")]
    public decimal ReservedQuantity { get; set; }

    /// <summary>
    /// Original requested quantity (before UoM conversion)
    /// </summary>
    [Precision(18, 6)]
    public decimal OriginalQuantity { get; set; }

    /// <summary>
    /// UoM code used in the request
    /// </summary>
    [MaxLength(20)]
    public string? UoMCode { get; set; }

    /// <summary>
    /// Warehouse where stock is reserved
    /// </summary>
    [Required]
    [MaxLength(20)]
    public string WarehouseCode { get; set; } = string.Empty;

    /// <summary>
    /// Unit price
    /// </summary>
    [Precision(18, 6)]
    public decimal UnitPrice { get; set; }

    /// <summary>
    /// Line total
    /// </summary>
    [Precision(18, 2)]
    public decimal LineTotal { get; set; }

    /// <summary>
    /// Tax code
    /// </summary>
    [MaxLength(20)]
    public string? TaxCode { get; set; }

    /// <summary>
    /// Discount percentage
    /// </summary>
    [Precision(5, 2)]
    public decimal DiscountPercent { get; set; }

    // Navigation property
    [ForeignKey(nameof(ReservationId))]
    public StockReservationEntity Reservation { get; set; } = null!;

    // Batch allocations for this line
    public ICollection<StockReservationBatchEntity> BatchAllocations { get; set; } = new List<StockReservationBatchEntity>();
}

/// <summary>
/// Entity for batch allocations within a reservation line
/// </summary>
[Table("StockReservationBatches")]
[Index(nameof(ItemCode), nameof(WarehouseCode), nameof(BatchNumber))]
public class StockReservationBatchEntity
{
    [Key]
    public int Id { get; set; }

    public int ReservationLineId { get; set; }

    /// <summary>
    /// Item code
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string ItemCode { get; set; } = string.Empty;

    /// <summary>
    /// Batch number
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string BatchNumber { get; set; } = string.Empty;

    /// <summary>
    /// Warehouse code
    /// </summary>
    [Required]
    [MaxLength(20)]
    public string WarehouseCode { get; set; } = string.Empty;

    /// <summary>
    /// Reserved quantity from this batch
    /// </summary>
    [Precision(18, 6)]
    [Range(0.000001, double.MaxValue, ErrorMessage = "Batch quantity must be greater than zero")]
    public decimal ReservedQuantity { get; set; }

    /// <summary>
    /// Expiry date of the batch
    /// </summary>
    public DateTime? ExpiryDate { get; set; }

    // Navigation property
    [ForeignKey(nameof(ReservationLineId))]
    public StockReservationLineEntity ReservationLine { get; set; } = null!;
}

/// <summary>
/// Static class containing reservation status constants
/// </summary>
public static class ReservationStatus
{
    /// <summary>
    /// Reservation is active and holding stock
    /// </summary>
    public const string Pending = "Pending";

    /// <summary>
    /// Reservation has been confirmed/posted to SAP
    /// </summary>
    public const string Confirmed = "Confirmed";

    /// <summary>
    /// Reservation was cancelled
    /// </summary>
    public const string Cancelled = "Cancelled";

    /// <summary>
    /// Reservation expired and was auto-released
    /// </summary>
    public const string Expired = "Expired";

    /// <summary>
    /// Reservation failed during confirmation
    /// </summary>
    public const string Failed = "Failed";
}

/// <summary>
/// Document types that can create reservations
/// </summary>
public static class ReservationDocumentType
{
    public const string Invoice = "Invoice";
    public const string SalesOrder = "SalesOrder";
    public const string Quotation = "Quotation";
}
