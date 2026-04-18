using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

public enum StockSnapshotStatus
{
    Pending,
    Complete,
    Failed
}

/// <summary>
/// Morning stock snapshot header — one per warehouse per day.
/// </summary>
[Index(nameof(SnapshotDate), nameof(WarehouseCode), IsUnique = true)]
[Index(nameof(Status))]
public class DailyStockSnapshotEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// The date of the snapshot (date only, no time).
    /// </summary>
    [Column(TypeName = "date")]
    public DateTime SnapshotDate { get; set; }

    /// <summary>
    /// The warehouse this snapshot is for.
    /// </summary>
    [Required]
    [MaxLength(20)]
    public string WarehouseCode { get; set; } = string.Empty;

    public StockSnapshotStatus Status { get; set; } = StockSnapshotStatus.Pending;

    /// <summary>
    /// Number of distinct item/batch rows captured.
    /// </summary>
    public int ItemCount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedAt { get; set; }

    [MaxLength(2000)]
    public string? LastError { get; set; }

    public List<DailyStockSnapshotItemEntity> Items { get; set; } = new();
}
