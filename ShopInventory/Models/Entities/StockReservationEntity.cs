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
// A confirmed reservation is the only local record of an online van sale, so it is also a reporting
// table: these two carry the per-customer drill-down for that route.
[Index(nameof(RouteCustomerId), nameof(CreatedAt))]
[Index(nameof(RouteCustomerCode))]
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
    /// The route customer a van sold to, when this reservation came from the van sales app.
    ///
    /// An online van sale posts straight to SAP and leaves no <c>DesktopSaleEntity</c> behind, so the
    /// confirmed reservation is the only local record it has. <see cref="CardCode"/> is the van's own
    /// business partner on those rows; the shop is here. Null for every other source system.
    /// </summary>
    public int? RouteCustomerId { get; set; }

    [ForeignKey(nameof(RouteCustomerId))]
    public RouteCustomerEntity? RouteCustomer { get; set; }

    /// <summary>
    /// Code and name as they stood at the sale. Snapshots: route customers are hard-deleted, which nulls
    /// the id above, and a rename must not travel backwards into sales already made.
    /// </summary>
    [MaxLength(50)]
    public string? RouteCustomerCode { get; set; }

    [MaxLength(200)]
    public string? RouteCustomerName { get; set; }

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
    /// How the customer paid, as the brand the handset named — "Cash", "Ecocash", "Innbucks".
    ///
    /// Added because an online van sale posts straight to SAP and leaves no <c>DesktopSaleEntity</c>,
    /// so this row is the only local record of it. Without a tender here, every online sale landed in
    /// the compliance report's unallocated column and the day's cash split could not be reconciled.
    ///
    /// Free text rather than an enum, matching <c>DesktopSaleEntity.PaymentMethod</c>, because it is
    /// a brand and not a fiscal money type: ZIMRA knows only <c>MobileWallet</c>, and telling Ecocash
    /// from Innbucks is exactly what the report exists to do. Null on every non-van reservation.
    /// </summary>
    [MaxLength(50)]
    public string? PaymentMethod { get; set; }

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

    /// <summary>
    /// SAP dimension 1 cost centre code for the line.
    /// </summary>
    [MaxLength(50)]
    public string? CostCentreCode { get; set; }

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
    /// One caller has taken this reservation and is posting it to SAP right now.
    /// </summary>
    /// <remarks>
    /// The state that stops one sale becoming two invoices.
    ///
    /// Without it, two requests carrying the same reference both find the reservation
    /// <see cref="Pending"/>, both ask SAP whether the sale is already there, are both told no — because
    /// neither has posted yet — and both post. That is not a theoretical race: a handset that times out
    /// and retries, or a rep who presses the button twice on a slow connection, produces exactly this
    /// pair of requests, and the second invoice is a real document with its own fiscal receipt that only
    /// a manual credit note can undo.
    ///
    /// The transition into this state is a conditional update — it succeeds for exactly one caller, and
    /// the loser is told the sale is already in flight rather than posting a second one. Confirmation
    /// then ends in <see cref="Confirmed"/> or, if the post fails, back in <see cref="Pending"/> so it
    /// can be retried.
    /// </remarks>
    public const string Confirming = "Confirming";

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
