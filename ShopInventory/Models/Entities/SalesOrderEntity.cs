using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ShopInventory.Models.Entities;

/// <summary>
/// Sales Order status enum
/// </summary>
public enum SalesOrderStatus
{
    Draft = 0,
    Pending = 1,
    Approved = 2,
    PartiallyFulfilled = 3,
    Fulfilled = 4,
    Cancelled = 5,
    OnHold = 6
}

/// <summary>
/// Entity for Sales Orders - pre-invoice orders that can be tracked and converted to invoices
/// </summary>
public class SalesOrderEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// SAP Document Entry (if synced)
    /// </summary>
    public int? SAPDocEntry { get; set; }

    /// <summary>
    /// SAP Document Number (if synced)
    /// </summary>
    public int? SAPDocNum { get; set; }

    /// <summary>
    /// Internal order number
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string OrderNumber { get; set; } = null!;

    /// <summary>
    /// Order date
    /// </summary>
    public DateTime OrderDate { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Expected delivery date
    /// </summary>
    public DateTime? DeliveryDate { get; set; }

    /// <summary>
    /// Customer code
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string CardCode { get; set; } = null!;

    /// <summary>
    /// Customer name
    /// </summary>
    [MaxLength(200)]
    public string? CardName { get; set; }

    /// <summary>
    /// Customer reference number
    /// </summary>
    [MaxLength(100)]
    public string? CustomerRefNo { get; set; }

    /// <summary>
    /// Order status
    /// </summary>
    public SalesOrderStatus Status { get; set; } = SalesOrderStatus.Draft;

    /// <summary>
    /// Comments/Notes
    /// </summary>
    [MaxLength(1000)]
    public string? Comments { get; set; }

    /// <summary>
    /// Salesperson code
    /// </summary>
    public int? SalesPersonCode { get; set; }

    /// <summary>
    /// Salesperson name
    /// </summary>
    [MaxLength(100)]
    public string? SalesPersonName { get; set; }

    /// <summary>
    /// Currency code (e.g., USD, ZIG)
    /// </summary>
    [MaxLength(10)]
    public string? Currency { get; set; } = "USD";

    /// <summary>
    /// Exchange rate at order creation
    /// </summary>
    [Column(TypeName = "decimal(18,6)")]
    public decimal ExchangeRate { get; set; } = 1;

    /// <summary>
    /// Subtotal before tax
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal SubTotal { get; set; }

    /// <summary>
    /// Tax amount
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal TaxAmount { get; set; }

    /// <summary>
    /// Discount percentage
    /// </summary>
    [Column(TypeName = "decimal(5,2)")]
    public decimal DiscountPercent { get; set; }

    /// <summary>
    /// Discount amount
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal DiscountAmount { get; set; }

    /// <summary>
    /// Total order amount
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal DocTotal { get; set; }

    /// <summary>
    /// Shipping address
    /// </summary>
    [MaxLength(500)]
    public string? ShipToAddress { get; set; }

    /// <summary>
    /// Billing address
    /// </summary>
    [MaxLength(500)]
    public string? BillToAddress { get; set; }

    /// <summary>
    /// Warehouse code for default fulfillment
    /// </summary>
    [MaxLength(50)]
    public string? WarehouseCode { get; set; }

    /// <summary>
    /// User who created the order
    /// </summary>
    public Guid? CreatedByUserId { get; set; }

    /// <summary>
    /// User who approved the order
    /// </summary>
    public Guid? ApprovedByUserId { get; set; }

    /// <summary>
    /// Approval date
    /// </summary>
    public DateTime? ApprovedDate { get; set; }

    /// <summary>
    /// Created timestamp
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Last updated timestamp
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Invoice ID if order was converted
    /// </summary>
    public int? InvoiceId { get; set; }

    /// <summary>
    /// Whether the order is synced to SAP
    /// </summary>
    public bool IsSynced { get; set; } = false;

    /// <summary>
    /// Last sync error message
    /// </summary>
    [MaxLength(500)]
    public string? SyncError { get; set; }

    // Navigation properties
    public virtual ICollection<SalesOrderLineEntity> Lines { get; set; } = new List<SalesOrderLineEntity>();
    public virtual InvoiceEntity? Invoice { get; set; }
    public virtual User? CreatedByUser { get; set; }
    public virtual User? ApprovedByUser { get; set; }
}

/// <summary>
/// Entity for Sales Order Line Items
/// </summary>
public class SalesOrderLineEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Parent Sales Order ID
    /// </summary>
    public int SalesOrderId { get; set; }

    /// <summary>
    /// Line number (0-based)
    /// </summary>
    public int LineNum { get; set; }

    /// <summary>
    /// Item code
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string ItemCode { get; set; } = null!;

    /// <summary>
    /// Item description
    /// </summary>
    [MaxLength(200)]
    public string? ItemDescription { get; set; }

    /// <summary>
    /// Quantity ordered
    /// </summary>
    [Column(TypeName = "decimal(18,4)")]
    public decimal Quantity { get; set; }

    /// <summary>
    /// Quantity fulfilled/delivered
    /// </summary>
    [Column(TypeName = "decimal(18,4)")]
    public decimal QuantityFulfilled { get; set; }

    /// <summary>
    /// Unit price
    /// </summary>
    [Column(TypeName = "decimal(18,4)")]
    public decimal UnitPrice { get; set; }

    /// <summary>
    /// Line discount percentage
    /// </summary>
    [Column(TypeName = "decimal(5,2)")]
    public decimal DiscountPercent { get; set; }

    /// <summary>
    /// Tax percentage
    /// </summary>
    [Column(TypeName = "decimal(5,2)")]
    public decimal TaxPercent { get; set; }

    /// <summary>
    /// Line total before tax
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal LineTotal { get; set; }

    /// <summary>
    /// Warehouse code
    /// </summary>
    [MaxLength(50)]
    public string? WarehouseCode { get; set; }

    /// <summary>
    /// Unit of Measure code
    /// </summary>
    [MaxLength(20)]
    public string? UoMCode { get; set; }

    /// <summary>
    /// Batch/Serial number (if applicable)
    /// </summary>
    [MaxLength(100)]
    public string? BatchNumber { get; set; }

    /// <summary>
    /// Product ID reference
    /// </summary>
    public int? ProductId { get; set; }

    // Navigation properties
    public virtual SalesOrderEntity SalesOrder { get; set; } = null!;
    public virtual ProductEntity? Product { get; set; }
}
