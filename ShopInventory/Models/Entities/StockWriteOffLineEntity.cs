using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// One thing counted on a <see cref="StockWriteOffEntity"/>: an item, the batch or serial number it
/// came out of where the item is managed that way, and how much of it left.
/// </summary>
/// <remarks>
/// The batch is on the line rather than in a collection beneath it because that is the shape of the
/// count: a store person writing off five of one batch and three of another has counted two things,
/// and SAP records them as two lines too. A non-batch item leaves <see cref="BatchNumber"/> null.
/// </remarks>
[Index(nameof(WriteOffId))]
public sealed class StockWriteOffLineEntity
{
    [Key]
    public int Id { get; set; }

    public int WriteOffId { get; set; }

    [ForeignKey(nameof(WriteOffId))]
    public StockWriteOffEntity WriteOff { get; set; } = null!;

    public int LineNum { get; set; }

    [Required]
    [MaxLength(50)]
    public string ItemCode { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? ItemDescription { get; set; }

    /// <summary>How much left, in the unit named by <see cref="UoMCode"/>.</summary>
    [Column(TypeName = "decimal(18,6)")]
    public decimal Quantity { get; set; }

    [MaxLength(20)]
    public string? UoMCode { get; set; }

    /// <summary>
    /// The batch the units came out of, for a batch-managed item. Null for an item SAP does not
    /// manage by batch — not "unknown": SAP refuses a batch-managed line that names none, so a
    /// posted line with a null batch is a line whose item has no batches.
    /// </summary>
    [MaxLength(50)]
    public string? BatchNumber { get; set; }

    /// <summary>
    /// The unit's serial number, for a serial-managed item. SAP counts a serial number as one unit,
    /// so such a line is always a quantity of one.
    /// </summary>
    [MaxLength(50)]
    public string? SerialNumber { get; set; }
}
