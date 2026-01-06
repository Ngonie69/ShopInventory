using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// Invoice entity for PostgreSQL storage
/// </summary>
[Table("Invoices")]
public class InvoiceEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// SAP DocEntry reference
    /// </summary>
    public int? SAPDocEntry { get; set; }

    /// <summary>
    /// SAP DocNum reference
    /// </summary>
    public int? SAPDocNum { get; set; }

    public DateTime DocDate { get; set; }

    public DateTime? DocDueDate { get; set; }

    [Required]
    [MaxLength(50)]
    public string CardCode { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? CardName { get; set; }

    [MaxLength(100)]
    public string? NumAtCard { get; set; }

    [MaxLength(500)]
    public string? Comments { get; set; }

    /// <summary>
    /// Document total - must be non-negative
    /// </summary>
    [Precision(18, 2)]
    [Range(0, double.MaxValue, ErrorMessage = "Document total cannot be negative")]
    public decimal DocTotal { get; set; }

    [Precision(18, 2)]
    public decimal DocTotalFc { get; set; }

    /// <summary>
    /// VAT sum - must be non-negative
    /// </summary>
    [Precision(18, 2)]
    [Range(0, double.MaxValue, ErrorMessage = "VAT sum cannot be negative")]
    public decimal VatSum { get; set; }

    [MaxLength(10)]
    public string? DocCurrency { get; set; }

    public int? SalesPersonCode { get; set; }

    [MaxLength(50)]
    public string Status { get; set; } = "Draft";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }

    public bool SyncedToSAP { get; set; }

    public DateTime? SyncedAt { get; set; }

    [MaxLength(500)]
    public string? SyncError { get; set; }

    // Navigation properties
    public ICollection<InvoiceLineEntity> DocumentLines { get; set; } = new List<InvoiceLineEntity>();
    public ICollection<IncomingPaymentInvoiceEntity> PaymentInvoices { get; set; } = new List<IncomingPaymentInvoiceEntity>();
}

/// <summary>
/// Invoice line entity for PostgreSQL storage
/// </summary>
[Table("InvoiceLines")]
public class InvoiceLineEntity
{
    [Key]
    public int Id { get; set; }

    public int InvoiceId { get; set; }

    public int LineNum { get; set; }

    public int? ProductId { get; set; }

    [Required]
    [MaxLength(50)]
    public string ItemCode { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? ItemDescription { get; set; }

    /// <summary>
    /// Quantity - must be positive (greater than zero)
    /// </summary>
    [Precision(18, 6)]
    [Range(0.000001, double.MaxValue, ErrorMessage = "Quantity must be greater than zero")]
    public decimal Quantity { get; set; }

    /// <summary>
    /// Unit price - must be non-negative
    /// </summary>
    [Precision(18, 6)]
    [Range(0, double.MaxValue, ErrorMessage = "Unit price cannot be negative")]
    public decimal UnitPrice { get; set; }

    [Precision(18, 6)]
    public decimal Price { get; set; }

    /// <summary>
    /// Line total - must be non-negative
    /// </summary>
    [Precision(18, 2)]
    [Range(0, double.MaxValue, ErrorMessage = "Line total cannot be negative")]
    public decimal LineTotal { get; set; }

    [MaxLength(20)]
    public string? WarehouseCode { get; set; }

    [MaxLength(20)]
    public string? TaxCode { get; set; }

    [Precision(5, 2)]
    public decimal DiscountPercent { get; set; }

    [MaxLength(20)]
    public string? UoMCode { get; set; }

    public int? UoMEntry { get; set; }

    [MaxLength(50)]
    public string? AccountCode { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    [ForeignKey(nameof(InvoiceId))]
    public InvoiceEntity Invoice { get; set; } = null!;

    [ForeignKey(nameof(ProductId))]
    public ProductEntity? Product { get; set; }

    public ICollection<InvoiceLineBatchEntity> BatchNumbers { get; set; } = new List<InvoiceLineBatchEntity>();
}

/// <summary>
/// Invoice line batch allocation entity
/// </summary>
[Table("InvoiceLineBatches")]
public class InvoiceLineBatchEntity
{
    [Key]
    public int Id { get; set; }

    public int InvoiceLineId { get; set; }

    [Required]
    [MaxLength(50)]
    public string BatchNumber { get; set; } = string.Empty;

    /// <summary>
    /// Batch quantity - must be positive (greater than zero)
    /// </summary>
    [Precision(18, 6)]
    [Range(0.000001, double.MaxValue, ErrorMessage = "Batch quantity must be greater than zero")]
    public decimal Quantity { get; set; }

    // Navigation property
    [ForeignKey(nameof(InvoiceLineId))]
    public InvoiceLineEntity InvoiceLine { get; set; } = null!;
}
