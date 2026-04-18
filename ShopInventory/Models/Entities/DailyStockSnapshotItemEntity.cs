using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// Per-item batch stock row within a daily snapshot.
/// AvailableQuantity is the working quantity — decremented by sales, adjusted by transfers.
/// </summary>
[Index(nameof(SnapshotId), nameof(ItemCode), nameof(BatchNumber))]
[Index(nameof(ItemCode), nameof(WarehouseCode))]
public class DailyStockSnapshotItemEntity
{
    [Key]
    public int Id { get; set; }

    public int SnapshotId { get; set; }

    [ForeignKey(nameof(SnapshotId))]
    public DailyStockSnapshotEntity Snapshot { get; set; } = null!;

    [Required]
    [MaxLength(50)]
    public string ItemCode { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? ItemDescription { get; set; }

    [Required]
    [MaxLength(20)]
    public string WarehouseCode { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? BatchNumber { get; set; }

    /// <summary>
    /// Quantity at morning fetch — never changes.
    /// </summary>
    [Column(TypeName = "decimal(18,6)")]
    public decimal OriginalQuantity { get; set; }

    /// <summary>
    /// Working quantity — decremented by sales, adjusted by transfers.
    /// </summary>
    [Column(TypeName = "decimal(18,6)")]
    public decimal AvailableQuantity { get; set; }

    public DateTime? ExpiryDate { get; set; }

    /// <summary>
    /// Row version for optimistic concurrency on AvailableQuantity.
    /// </summary>
    [Timestamp]
    public byte[] RowVersion { get; set; } = null!;
}
