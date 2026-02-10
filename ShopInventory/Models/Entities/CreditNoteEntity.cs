using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ShopInventory.Models.Entities;

/// <summary>
/// Credit Note status enum
/// </summary>
public enum CreditNoteStatus
{
    Draft = 0,
    Pending = 1,
    Approved = 2,
    Applied = 3,
    Cancelled = 4
}

/// <summary>
/// Credit Note type enum
/// </summary>
public enum CreditNoteType
{
    Return = 0,           // Product return
    PriceAdjustment = 1,  // Price correction
    Discount = 2,         // Post-sale discount
    Damaged = 3,          // Damaged goods
    Other = 4             // Other reasons
}

/// <summary>
/// Entity for Credit Notes - handles returns and refunds
/// </summary>
public class CreditNoteEntity
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
    /// Credit note number
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string CreditNoteNumber { get; set; } = null!;

    /// <summary>
    /// Credit note date
    /// </summary>
    public DateTime CreditNoteDate { get; set; } = DateTime.UtcNow;

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
    /// Credit note type
    /// </summary>
    public CreditNoteType Type { get; set; } = CreditNoteType.Return;

    /// <summary>
    /// Credit note status
    /// </summary>
    public CreditNoteStatus Status { get; set; } = CreditNoteStatus.Draft;

    /// <summary>
    /// Original Invoice ID (if based on an invoice)
    /// </summary>
    public int? OriginalInvoiceId { get; set; }

    /// <summary>
    /// Original Invoice DocEntry
    /// </summary>
    public int? OriginalInvoiceDocEntry { get; set; }

    /// <summary>
    /// Reason for credit note
    /// </summary>
    [MaxLength(500)]
    public string? Reason { get; set; }

    /// <summary>
    /// Comments/Notes
    /// </summary>
    [MaxLength(1000)]
    public string? Comments { get; set; }

    /// <summary>
    /// Currency code
    /// </summary>
    [MaxLength(10)]
    public string? Currency { get; set; } = "USD";

    /// <summary>
    /// Exchange rate
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
    /// Total credit amount
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal DocTotal { get; set; }

    /// <summary>
    /// Amount applied to invoices
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal AppliedAmount { get; set; }

    /// <summary>
    /// Remaining balance
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal Balance { get; set; }

    /// <summary>
    /// Whether to restock items
    /// </summary>
    public bool RestockItems { get; set; } = true;

    /// <summary>
    /// Warehouse for restocking
    /// </summary>
    [MaxLength(50)]
    public string? RestockWarehouseCode { get; set; }

    /// <summary>
    /// User who created the credit note
    /// </summary>
    public Guid? CreatedByUserId { get; set; }

    /// <summary>
    /// User who approved the credit note
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
    /// Whether synced to SAP
    /// </summary>
    public bool IsSynced { get; set; } = false;

    /// <summary>
    /// Sync error message
    /// </summary>
    [MaxLength(500)]
    public string? SyncError { get; set; }

    // Navigation properties
    public virtual ICollection<CreditNoteLineEntity> Lines { get; set; } = new List<CreditNoteLineEntity>();
    public virtual InvoiceEntity? OriginalInvoice { get; set; }
    public virtual User? CreatedByUser { get; set; }
    public virtual User? ApprovedByUser { get; set; }
}

/// <summary>
/// Entity for Credit Note Line Items
/// </summary>
public class CreditNoteLineEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Parent Credit Note ID
    /// </summary>
    public int CreditNoteId { get; set; }

    /// <summary>
    /// Line number
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
    /// Quantity being returned/credited
    /// </summary>
    [Column(TypeName = "decimal(18,4)")]
    public decimal Quantity { get; set; }

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
    /// Line total
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal LineTotal { get; set; }

    /// <summary>
    /// Warehouse code for return
    /// </summary>
    [MaxLength(50)]
    public string? WarehouseCode { get; set; }

    /// <summary>
    /// Return reason for this line
    /// </summary>
    [MaxLength(200)]
    public string? ReturnReason { get; set; }

    /// <summary>
    /// Original invoice line reference
    /// </summary>
    public int? OriginalInvoiceLineId { get; set; }

    /// <summary>
    /// Batch number (if applicable)
    /// </summary>
    [MaxLength(100)]
    public string? BatchNumber { get; set; }

    /// <summary>
    /// Whether item was restocked
    /// </summary>
    public bool IsRestocked { get; set; } = false;

    /// <summary>
    /// Product ID reference
    /// </summary>
    public int? ProductId { get; set; }

    // Navigation properties
    public virtual CreditNoteEntity CreditNote { get; set; } = null!;
    public virtual ProductEntity? Product { get; set; }
}
