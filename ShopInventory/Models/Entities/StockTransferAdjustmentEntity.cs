using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// Transfer-based stock adjustment received from TransferEventListener.
/// Adjusts DailyStockSnapshotItem quantities during the day.
/// </summary>
[Index(nameof(SnapshotDate), nameof(ItemCode), nameof(WarehouseCode), nameof(TransferDocEntry), nameof(Direction), IsUnique = true)]
[Index(nameof(SnapshotDate))]
public class StockTransferAdjustmentEntity
{
    [Key]
    public int Id { get; set; }

    [Column(TypeName = "date")]
    public DateTime SnapshotDate { get; set; }

    [Required]
    [MaxLength(50)]
    public string ItemCode { get; set; } = string.Empty;

    [Required]
    [MaxLength(20)]
    public string WarehouseCode { get; set; } = string.Empty;

    /// <summary>
    /// Positive for IN, negative for OUT.
    /// </summary>
    [Column(TypeName = "decimal(18,6)")]
    public decimal AdjustmentQuantity { get; set; }

    [Required]
    [MaxLength(5)]
    public string Direction { get; set; } = string.Empty;

    public int? TransferDocEntry { get; set; }

    public int? TransferDocNum { get; set; }

    [MaxLength(50)]
    public string? SourceWarehouse { get; set; }

    [MaxLength(50)]
    public string? DestinationWarehouse { get; set; }

    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
}
