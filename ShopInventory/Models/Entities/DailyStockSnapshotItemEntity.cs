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
    /// <remarks>
    /// Read freely; moved only through <see cref="Move"/>, and opened only through
    /// <see cref="Opening"/>. The setter is closed because this is the figure the invoicing guard reads
    /// and, for most of this system's life, anything could change it without saying so. The journal
    /// that now explains it is worth exactly as much as it is complete, and completeness is not a
    /// property you can ask a reviewer to maintain across a codebase this size — the compiler has to
    /// be the one asking.
    /// </remarks>
    [Column(TypeName = "decimal(18,6)")]
    public decimal AvailableQuantity { get; private set; }

    /// <summary>
    /// The position this row opens at, before anything has moved it.
    /// </summary>
    /// <remarks>
    /// For the morning fetch, where opening and working quantity are the same figure by definition.
    /// A row that arrives mid-day opens at zero and reaches its quantity through <see cref="Move"/>,
    /// so that the movement is journalled like any other — see <paramref name="opening"/> being
    /// separate from what the row ends up holding.
    /// </remarks>
    /// <param name="opening">What the warehouse held when the day started.</param>
    /// <returns>The row, so an opening can be stated inside a projection.</returns>
    public DailyStockSnapshotItemEntity Opening(decimal opening)
    {
        OriginalQuantity = opening;
        AvailableQuantity = opening;
        return this;
    }

    /// <summary>
    /// Moves the working quantity. Negative takes units off the row.
    /// </summary>
    /// <remarks>
    /// Internal, so only this assembly can move a balance at all, and called from a deliberately
    /// short list of places that each journal what they moved. The list is not something a reviewer
    /// can be asked to hold in their head, so it is written down and asserted: see
    /// <c>StockMovementWriterTests</c>, which fails when a new caller appears. That is the real
    /// guarantee here — not that a bypass is impossible, but that one cannot arrive unnoticed.
    /// </remarks>
    internal void Move(decimal delta) => AvailableQuantity += delta;

    public DateTime? ExpiryDate { get; set; }

    /// <summary>
    /// Row version for optimistic concurrency on AvailableQuantity.
    /// PostgreSQL uses xmin system column via [Timestamp] on uint.
    /// </summary>
    [Timestamp]
    public uint Version { get; set; }
}
