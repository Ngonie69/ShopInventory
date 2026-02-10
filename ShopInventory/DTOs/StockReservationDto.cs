using System.ComponentModel.DataAnnotations;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.DTOs;

/// <summary>
/// Request to create a stock reservation from a desktop app or external system
/// </summary>
public class CreateStockReservationRequest
{
    /// <summary>
    /// Unique reference ID from the source system (e.g., desktop app invoice number)
    /// Must be unique across all reservations
    /// </summary>
    [Required(ErrorMessage = "External reference ID is required")]
    [MaxLength(100)]
    public string ExternalReferenceId { get; set; } = string.Empty;

    /// <summary>
    /// Alternative property for external reference (same as ExternalReferenceId)
    /// </summary>
    [MaxLength(100)]
    public string? ExternalReference { get; set; }

    /// <summary>
    /// Source system identifier (e.g., "DESKTOP_APP", "POS_TERMINAL_1")
    /// </summary>
    [Required(ErrorMessage = "Source system is required")]
    [MaxLength(50)]
    public string SourceSystem { get; set; } = string.Empty;

    /// <summary>
    /// Type of document: Invoice, SalesOrder, Quotation
    /// </summary>
    [MaxLength(30)]
    public string DocumentType { get; set; } = ReservationDocumentType.Invoice;

    /// <summary>
    /// Customer card code
    /// </summary>
    [Required(ErrorMessage = "Customer code is required")]
    [MaxLength(50)]
    public string CardCode { get; set; } = string.Empty;

    /// <summary>
    /// Customer name
    /// </summary>
    [MaxLength(200)]
    public string? CardName { get; set; }

    /// <summary>
    /// Currency code
    /// </summary>
    [MaxLength(10)]
    public string? Currency { get; set; }

    /// <summary>
    /// How long to hold the reservation (in minutes). Default: 60 minutes
    /// Maximum: 1440 minutes (24 hours)
    /// </summary>
    [Range(1, 1440, ErrorMessage = "Reservation duration must be between 1 and 1440 minutes")]
    public int ReservationDurationMinutes { get; set; } = 60;

    /// <summary>
    /// Whether fiscalization is required for this invoice
    /// </summary>
    public bool RequiresFiscalization { get; set; } = false;

    /// <summary>
    /// Processing priority (higher = more urgent)
    /// </summary>
    public int? Priority { get; set; }

    /// <summary>
    /// Additional notes or comments
    /// </summary>
    [MaxLength(500)]
    public string? Notes { get; set; }

    /// <summary>
    /// Document due date
    /// </summary>
    public DateTime? DocDueDate { get; set; }

    /// <summary>
    /// Sales person code
    /// </summary>
    public int? SalesPersonCode { get; set; }

    /// <summary>
    /// Line items to reserve
    /// </summary>
    [Required(ErrorMessage = "At least one line item is required")]
    [MinLength(1, ErrorMessage = "At least one line item is required")]
    public List<CreateStockReservationLineRequest> Lines { get; set; } = new();

    /// <summary>
    /// Additional metadata (stored as JSON)
    /// </summary>
    public Dictionary<string, object>? Metadata { get; set; }

    /// <summary>
    /// Gets the effective external reference
    /// </summary>
    public string GetExternalReference() =>
        !string.IsNullOrEmpty(ExternalReference) ? ExternalReference : ExternalReferenceId;
}

/// <summary>
/// Line item for stock reservation request
/// </summary>
public class CreateStockReservationLineRequest
{
    /// <summary>
    /// Line number in the source document
    /// </summary>
    public int LineNum { get; set; }

    /// <summary>
    /// Item code to reserve
    /// </summary>
    [Required(ErrorMessage = "Item code is required")]
    [MaxLength(50)]
    public string ItemCode { get; set; } = string.Empty;

    /// <summary>
    /// Item description
    /// </summary>
    [MaxLength(200)]
    public string? ItemDescription { get; set; }

    /// <summary>
    /// Quantity to reserve (in the specified UoM)
    /// </summary>
    [Required(ErrorMessage = "Quantity is required")]
    [Range(0.000001, double.MaxValue, ErrorMessage = "Quantity must be greater than zero")]
    public decimal Quantity { get; set; }

    /// <summary>
    /// Unit of Measure code (e.g., "KG", "PC", "BOX")
    /// </summary>
    [MaxLength(20)]
    public string? UoMCode { get; set; }

    /// <summary>
    /// Warehouse code where stock should be reserved
    /// </summary>
    [Required(ErrorMessage = "Warehouse code is required")]
    [MaxLength(20)]
    public string WarehouseCode { get; set; } = string.Empty;

    /// <summary>
    /// Unit price
    /// </summary>
    [Range(0, double.MaxValue, ErrorMessage = "Unit price cannot be negative")]
    public decimal UnitPrice { get; set; }

    /// <summary>
    /// Tax code
    /// </summary>
    [MaxLength(20)]
    public string? TaxCode { get; set; }

    /// <summary>
    /// Discount percentage (0-100)
    /// </summary>
    [Range(0, 100, ErrorMessage = "Discount percent must be between 0 and 100")]
    public decimal DiscountPercent { get; set; }

    /// <summary>
    /// Specific batch allocations (optional - if not provided, FIFO/FEFO will be used)
    /// </summary>
    public List<ReservationBatchRequest>? BatchNumbers { get; set; }

    /// <summary>
    /// Whether to auto-allocate batches using FIFO/FEFO if BatchNumbers is not specified
    /// </summary>
    public bool AutoAllocateBatches { get; set; } = true;
}

/// <summary>
/// Batch allocation for reservation
/// </summary>
public class ReservationBatchRequest
{
    /// <summary>
    /// Batch number
    /// </summary>
    [Required(ErrorMessage = "Batch number is required")]
    [MaxLength(50)]
    public string BatchNumber { get; set; } = string.Empty;

    /// <summary>
    /// Quantity from this batch
    /// </summary>
    [Required(ErrorMessage = "Quantity is required")]
    [Range(0.000001, double.MaxValue, ErrorMessage = "Quantity must be greater than zero")]
    public decimal Quantity { get; set; }
}

/// <summary>
/// Response after creating a stock reservation
/// </summary>
public class StockReservationResponseDto
{
    /// <summary>
    /// Whether the reservation was successful
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Response message
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// The created reservation details
    /// </summary>
    public StockReservationDto? Reservation { get; set; }

    /// <summary>
    /// Validation errors if the reservation failed
    /// </summary>
    public List<StockReservationErrorDto> Errors { get; set; } = new();

    /// <summary>
    /// Warnings about the reservation
    /// </summary>
    public List<string> Warnings { get; set; } = new();
}

/// <summary>
/// DTO for stock reservation details
/// </summary>
public class StockReservationDto
{
    public int Id { get; set; }
    public string ReservationId { get; set; } = string.Empty;
    public string ExternalReferenceId { get; set; } = string.Empty;
    public string SourceSystem { get; set; } = string.Empty;
    public string DocumentType { get; set; } = string.Empty;
    public string CardCode { get; set; } = string.Empty;
    public string? CardName { get; set; }
    public decimal TotalValue { get; set; }
    public string? Currency { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConfirmedAt { get; set; }
    public int? SAPDocEntry { get; set; }
    public int? SAPDocNum { get; set; }
    public DateTime? CancelledAt { get; set; }
    public string? CancellationReason { get; set; }
    public int RenewalCount { get; set; }
    public string? CreatedBy { get; set; }
    public List<StockReservationLineDto> Lines { get; set; } = new();
}

/// <summary>
/// DTO for reservation line details
/// </summary>
public class StockReservationLineDto
{
    public int Id { get; set; }
    public int LineNum { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemDescription { get; set; }
    public decimal ReservedQuantity { get; set; }
    public decimal OriginalQuantity { get; set; }
    public string? UoMCode { get; set; }
    public string WarehouseCode { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public decimal LineTotal { get; set; }
    public string? TaxCode { get; set; }
    public decimal DiscountPercent { get; set; }
    public List<StockReservationBatchDto> BatchAllocations { get; set; } = new();
}

/// <summary>
/// DTO for reservation batch allocation
/// </summary>
public class StockReservationBatchDto
{
    public int Id { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string BatchNumber { get; set; } = string.Empty;
    public string WarehouseCode { get; set; } = string.Empty;
    public decimal ReservedQuantity { get; set; }
    public DateTime? ExpiryDate { get; set; }
}

/// <summary>
/// Error details for stock reservation failures
/// </summary>
public class StockReservationErrorDto
{
    public string ErrorCode { get; set; } = string.Empty;
    public int? LineNumber { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string? WarehouseCode { get; set; }
    public string? BatchNumber { get; set; }
    public decimal RequestedQuantity { get; set; }
    public decimal AvailableQuantity { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? SuggestedAction { get; set; }
}

/// <summary>
/// Request to confirm/post a reservation to SAP
/// </summary>
public class ConfirmReservationRequest
{
    /// <summary>
    /// The reservation ID to confirm
    /// </summary>
    [Required(ErrorMessage = "Reservation ID is required")]
    public string ReservationId { get; set; } = string.Empty;

    /// <summary>
    /// Optional: Override document date
    /// </summary>
    public string? DocDate { get; set; }

    /// <summary>
    /// Optional: Override document due date
    /// </summary>
    public string? DocDueDate { get; set; }

    /// <summary>
    /// Optional: Customer reference number for the SAP document
    /// </summary>
    public string? NumAtCard { get; set; }

    /// <summary>
    /// Optional: Comments for the SAP document
    /// </summary>
    public string? Comments { get; set; }

    /// <summary>
    /// Optional: Sales person code
    /// </summary>
    public int? SalesPersonCode { get; set; }

    /// <summary>
    /// Whether to fiscalize the invoice after posting
    /// </summary>
    public bool Fiscalize { get; set; } = true;
}

/// <summary>
/// Response after confirming a reservation
/// </summary>
public class ConfirmReservationResponseDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string ReservationId { get; set; } = string.Empty;
    public int? SAPDocEntry { get; set; }
    public int? SAPDocNum { get; set; }
    public InvoiceDto? Invoice { get; set; }
    public FiscalizationResult? Fiscalization { get; set; }
    public List<string> Errors { get; set; } = new();
}

/// <summary>
/// Request to cancel a reservation
/// </summary>
public class CancelReservationRequest
{
    /// <summary>
    /// The reservation ID to cancel
    /// </summary>
    [Required(ErrorMessage = "Reservation ID is required")]
    public string ReservationId { get; set; } = string.Empty;

    /// <summary>
    /// Reason for cancellation
    /// </summary>
    [MaxLength(500)]
    public string? Reason { get; set; }
}

/// <summary>
/// Request to renew/extend a reservation
/// </summary>
public class RenewReservationRequest
{
    /// <summary>
    /// The reservation ID to renew
    /// </summary>
    [Required(ErrorMessage = "Reservation ID is required")]
    public string ReservationId { get; set; } = string.Empty;

    /// <summary>
    /// Additional time to add (in minutes). Default: 60 minutes
    /// </summary>
    [Range(1, 1440, ErrorMessage = "Extension duration must be between 1 and 1440 minutes")]
    public int ExtensionMinutes { get; set; } = 60;
}

/// <summary>
/// Query parameters for listing reservations
/// </summary>
public class ReservationQueryParams
{
    /// <summary>
    /// Filter by source system
    /// </summary>
    public string? SourceSystem { get; set; }

    /// <summary>
    /// Filter by status (Pending, Confirmed, Cancelled, Expired)
    /// </summary>
    public string? Status { get; set; }

    /// <summary>
    /// Filter by customer code
    /// </summary>
    public string? CardCode { get; set; }

    /// <summary>
    /// Filter by external reference ID (partial match)
    /// </summary>
    public string? ExternalReferenceId { get; set; }

    /// <summary>
    /// Include only active (non-expired) reservations
    /// </summary>
    public bool ActiveOnly { get; set; } = true;

    /// <summary>
    /// Page number (1-based)
    /// </summary>
    [Range(1, int.MaxValue)]
    public int Page { get; set; } = 1;

    /// <summary>
    /// Page size
    /// </summary>
    [Range(1, 100)]
    public int PageSize { get; set; } = 20;
}

/// <summary>
/// Paginated list of reservations
/// </summary>
public class ReservationListResponseDto
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages { get; set; }
    public bool HasMore { get; set; }
    public List<StockReservationDto> Reservations { get; set; } = new();
}

/// <summary>
/// Summary of reserved stock for an item/warehouse
/// </summary>
public class ReservedStockSummaryDto
{
    public string ItemCode { get; set; } = string.Empty;
    public string WarehouseCode { get; set; } = string.Empty;
    public decimal TotalReservedQuantity { get; set; }
    public decimal AvailableQuantity { get; set; }
    public decimal PhysicalQuantity { get; set; }
    public int ActiveReservationCount { get; set; }
    public List<ReservedStockBatchSummaryDto> BatchReservations { get; set; } = new();
}

/// <summary>
/// Summary of reserved stock by batch
/// </summary>
public class ReservedStockBatchSummaryDto
{
    public string BatchNumber { get; set; } = string.Empty;
    public decimal TotalReservedQuantity { get; set; }
    public decimal AvailableQuantity { get; set; }
    public decimal PhysicalQuantity { get; set; }
    public DateTime? ExpiryDate { get; set; }
}

/// <summary>
/// Error codes for stock reservation operations
/// </summary>
public static class ReservationErrorCode
{
    public const string InsufficientStock = "INSUFFICIENT_STOCK";
    public const string InsufficientBatchStock = "INSUFFICIENT_BATCH_STOCK";
    public const string ItemNotFound = "ITEM_NOT_FOUND";
    public const string WarehouseNotFound = "WAREHOUSE_NOT_FOUND";
    public const string BatchNotFound = "BATCH_NOT_FOUND";
    public const string DuplicateReference = "DUPLICATE_REFERENCE";
    public const string ReservationNotFound = "RESERVATION_NOT_FOUND";
    public const string ReservationExpired = "RESERVATION_EXPIRED";
    public const string ReservationAlreadyConfirmed = "RESERVATION_ALREADY_CONFIRMED";
    public const string ReservationAlreadyCancelled = "RESERVATION_ALREADY_CANCELLED";
    public const string InvalidQuantity = "INVALID_QUANTITY";
    public const string LockConflict = "LOCK_CONFLICT";
    public const string SAPPostingFailed = "SAP_POSTING_FAILED";
    public const string ValidationFailed = "VALIDATION_FAILED";
}
