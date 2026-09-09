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

    /// <summary>
    /// Whether this snapshot is missing the half of the warehouse SAP does not batch-manage.
    /// </summary>
    /// <remarks>
    /// A snapshot whose unbatched read failed is still marked <see cref="StockSnapshotStatus.Complete"/>
    /// on purpose — the batch rows it did get are worth having on a till, and a snapshot marked
    /// <see cref="StockSnapshotStatus.Failed"/> puts nothing there at all. But "complete" then covers two
    /// different things: a whole warehouse, and one with every non-batch item silently absent.
    ///
    /// <para>
    /// The fetch skips a snapshot that is already Complete, so without this flag the second kind could
    /// never be repaired: re-running the fetch found a Complete row and returned, and the shop went the
    /// rest of the day unable to sell anything unbatched. That is what happened to KEFSHOP on
    /// 2026-09-09, when the SAP stock reads timed out under a full price-catalog sync.
    /// </para>
    ///
    /// <para>
    /// A flag rather than reading it back off <see cref="LastError"/>, because that field carries a
    /// sentence on the fallback's <i>success</i> too, and because a transfer arriving during the day
    /// writes an unbatched row of its own — so neither the prose nor the presence of batchless rows
    /// answers the question this asks.
    /// </para>
    /// </remarks>
    public bool UnbatchedStockMissing { get; set; }

    public List<DailyStockSnapshotItemEntity> Items { get; set; } = new();
}
