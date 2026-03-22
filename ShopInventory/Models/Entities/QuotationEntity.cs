using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ShopInventory.Models.Entities;

public enum QuotationStatus
{
    Draft = 0,
    Pending = 1,
    Approved = 2,
    Sent = 3,
    Accepted = 4,
    Rejected = 5,
    Expired = 6,
    Converted = 7,
    Cancelled = 8
}

public class QuotationEntity
{
    [Key]
    public int Id { get; set; }

    public int? SAPDocEntry { get; set; }

    public int? SAPDocNum { get; set; }

    [Required]
    [MaxLength(50)]
    public string QuotationNumber { get; set; } = null!;

    public DateTime QuotationDate { get; set; } = DateTime.UtcNow;

    public DateTime? ValidUntil { get; set; }

    [Required]
    [MaxLength(50)]
    public string CardCode { get; set; } = null!;

    [MaxLength(200)]
    public string? CardName { get; set; }

    [MaxLength(100)]
    public string? CustomerRefNo { get; set; }

    [MaxLength(200)]
    public string? ContactPerson { get; set; }

    public QuotationStatus Status { get; set; } = QuotationStatus.Draft;

    [MaxLength(2000)]
    public string? Comments { get; set; }

    [MaxLength(2000)]
    public string? TermsAndConditions { get; set; }

    public int? SalesPersonCode { get; set; }

    [MaxLength(100)]
    public string? SalesPersonName { get; set; }

    [MaxLength(10)]
    public string? Currency { get; set; } = "USD";

    [Column(TypeName = "decimal(18,6)")]
    public decimal ExchangeRate { get; set; } = 1;

    [Column(TypeName = "decimal(18,2)")]
    public decimal SubTotal { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TaxAmount { get; set; }

    [Column(TypeName = "decimal(5,2)")]
    public decimal DiscountPercent { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal DiscountAmount { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal DocTotal { get; set; }

    [MaxLength(500)]
    public string? ShipToAddress { get; set; }

    [MaxLength(500)]
    public string? BillToAddress { get; set; }

    [MaxLength(50)]
    public string? WarehouseCode { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public Guid? ApprovedByUserId { get; set; }

    public DateTime? ApprovedDate { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public int? SalesOrderId { get; set; }

    public bool IsSynced { get; set; } = false;

    [MaxLength(500)]
    public string? SyncError { get; set; }

    // Navigation properties
    public virtual ICollection<QuotationLineEntity> Lines { get; set; } = new List<QuotationLineEntity>();
    public virtual SalesOrderEntity? SalesOrder { get; set; }
    public virtual User? CreatedByUser { get; set; }
    public virtual User? ApprovedByUser { get; set; }
}

public class QuotationLineEntity
{
    [Key]
    public int Id { get; set; }

    public int QuotationId { get; set; }

    public int LineNum { get; set; }

    [Required]
    [MaxLength(100)]
    public string ItemCode { get; set; } = null!;

    [MaxLength(200)]
    public string? ItemDescription { get; set; }

    [Column(TypeName = "decimal(18,4)")]
    public decimal Quantity { get; set; }

    [Column(TypeName = "decimal(18,4)")]
    public decimal UnitPrice { get; set; }

    [Column(TypeName = "decimal(5,2)")]
    public decimal DiscountPercent { get; set; }

    [Column(TypeName = "decimal(5,2)")]
    public decimal TaxPercent { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal LineTotal { get; set; }

    [MaxLength(50)]
    public string? WarehouseCode { get; set; }

    [MaxLength(20)]
    public string? UoMCode { get; set; }

    public int? ProductId { get; set; }

    // Navigation properties
    public virtual QuotationEntity Quotation { get; set; } = null!;
    public virtual ProductEntity? Product { get; set; }
}
