using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// Inventory transfer entity for PostgreSQL storage
/// </summary>
[Table("InventoryTransfers")]
public class InventoryTransferEntity
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

    public DateTime? DueDate { get; set; }

    [MaxLength(20)]
    public string? FromWarehouse { get; set; }

    [MaxLength(20)]
    public string? ToWarehouse { get; set; }

    [MaxLength(500)]
    public string? Comments { get; set; }

    [MaxLength(200)]
    public string? JournalMemo { get; set; }

    [MaxLength(50)]
    public string Status { get; set; } = "Draft";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }

    public bool SyncedToSAP { get; set; }

    public DateTime? SyncedAt { get; set; }

    [MaxLength(500)]
    public string? SyncError { get; set; }

    // Navigation properties
    public ICollection<InventoryTransferLineEntity> StockTransferLines { get; set; } = new List<InventoryTransferLineEntity>();
}

/// <summary>
/// Inventory transfer line entity for PostgreSQL storage
/// </summary>
[Table("InventoryTransferLines")]
public class InventoryTransferLineEntity
{
    [Key]
    public int Id { get; set; }

    public int InventoryTransferId { get; set; }

    public int LineNum { get; set; }

    public int? ProductId { get; set; }

    [Required]
    [MaxLength(50)]
    public string ItemCode { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? ItemDescription { get; set; }

    /// <summary>
    /// Transfer quantity - must be positive (greater than zero)
    /// </summary>
    [Precision(18, 6)]
    [Range(0.000001, double.MaxValue, ErrorMessage = "Transfer quantity must be greater than zero")]
    public decimal Quantity { get; set; }

    [MaxLength(20)]
    public string? FromWarehouseCode { get; set; }

    [MaxLength(20)]
    public string? WarehouseCode { get; set; }

    [MaxLength(20)]
    public string? UoMCode { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    [ForeignKey(nameof(InventoryTransferId))]
    public InventoryTransferEntity InventoryTransfer { get; set; } = null!;

    [ForeignKey(nameof(ProductId))]
    public ProductEntity? Product { get; set; }

    public ICollection<InventoryTransferLineBatchEntity> BatchNumbers { get; set; } = new List<InventoryTransferLineBatchEntity>();
}

/// <summary>
/// Inventory transfer line batch allocation entity
/// </summary>
[Table("InventoryTransferLineBatches")]
public class InventoryTransferLineBatchEntity
{
    [Key]
    public int Id { get; set; }

    public int InventoryTransferLineId { get; set; }

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
    [ForeignKey(nameof(InventoryTransferLineId))]
    public InventoryTransferLineEntity InventoryTransferLine { get; set; } = null!;
}
