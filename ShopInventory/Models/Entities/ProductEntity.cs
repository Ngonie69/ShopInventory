using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// Product entity for PostgreSQL storage
/// </summary>
[Table("Products")]
public class ProductEntity
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(50)]
    public string ItemCode { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? ItemName { get; set; }

    [MaxLength(50)]
    public string? ItemType { get; set; }

    public int? ItemsGroupCode { get; set; }

    [MaxLength(50)]
    public string? BarCode { get; set; }

    public bool ManageBatchNumbers { get; set; }

    public bool ManageSerialNumbers { get; set; }

    /// <summary>
    /// Quantity on stock - must be non-negative
    /// </summary>
    [Precision(18, 6)]
    [Range(0, double.MaxValue, ErrorMessage = "Quantity on stock cannot be negative")]
    public decimal QuantityOnStock { get; set; }

    /// <summary>
    /// Quantity ordered from vendors - must be non-negative
    /// </summary>
    [Precision(18, 6)]
    [Range(0, double.MaxValue, ErrorMessage = "Quantity ordered from vendors cannot be negative")]
    public decimal QuantityOrderedFromVendors { get; set; }

    /// <summary>
    /// Quantity ordered by customers - must be non-negative
    /// </summary>
    [Precision(18, 6)]
    [Range(0, double.MaxValue, ErrorMessage = "Quantity ordered by customers cannot be negative")]
    public decimal QuantityOrderedByCustomers { get; set; }

    [MaxLength(20)]
    public string? InventoryUOM { get; set; }

    [MaxLength(20)]
    public string? SalesUnit { get; set; }

    [MaxLength(20)]
    public string? PurchaseUnit { get; set; }

    [MaxLength(20)]
    public string? DefaultWarehouse { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Synced from SAP
    /// </summary>
    public bool SyncedFromSAP { get; set; }

    public DateTime? LastSyncedAt { get; set; }

    // Navigation properties
    public ICollection<ProductBatchEntity> Batches { get; set; } = new List<ProductBatchEntity>();
    public ICollection<InvoiceLineEntity> InvoiceLines { get; set; } = new List<InvoiceLineEntity>();
    public ICollection<InventoryTransferLineEntity> TransferLines { get; set; } = new List<InventoryTransferLineEntity>();
    public ICollection<ItemPriceEntity> Prices { get; set; } = new List<ItemPriceEntity>();
}

/// <summary>
/// Product batch entity for PostgreSQL storage
/// </summary>
[Table("ProductBatches")]
public class ProductBatchEntity
{
    [Key]
    public int Id { get; set; }

    public int ProductId { get; set; }

    [Required]
    [MaxLength(50)]
    public string BatchNumber { get; set; } = string.Empty;

    /// <summary>
    /// Batch quantity - must be non-negative
    /// </summary>
    [Precision(18, 6)]
    [Range(0, double.MaxValue, ErrorMessage = "Batch quantity cannot be negative")]
    public decimal Quantity { get; set; }

    [MaxLength(20)]
    public string? WarehouseCode { get; set; }

    [MaxLength(50)]
    public string? Status { get; set; }

    [MaxLength(100)]
    public string? ManufacturerSerialNumber { get; set; }

    [MaxLength(100)]
    public string? InternalSerialNumber { get; set; }

    public DateTime? ExpiryDate { get; set; }

    public DateTime? ManufacturingDate { get; set; }

    public DateTime? AdmissionDate { get; set; }

    [MaxLength(100)]
    public string? Location { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }

    public bool IsActive { get; set; } = true;

    // Navigation property
    [ForeignKey(nameof(ProductId))]
    public ProductEntity Product { get; set; } = null!;
}
