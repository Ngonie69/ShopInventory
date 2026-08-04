using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ShopInventory.Models.Entities;

/// <summary>
/// How much volume one sold unit of an item represents, so quantities sold across mixed pack sizes
/// can be added up as litres instead of as a meaningless count of units.
/// </summary>
/// <remarks>
/// The initial factors come from the business' catalogue and are loaded by
/// <see cref="Data.ItemVolumeConversionSeedData"/>. A new product gets its factor through the
/// maintenance screen; an item with no row here is reported as unconverted rather than counted as
/// zero, because silently dropping it would understate the total with nothing on screen to say so.
/// </remarks>
[Table("ItemVolumeConversions")]
public class ItemVolumeConversionEntity
{
    /// <summary>The SAP item code. Stored upper-cased so lookups can be ordinal.</summary>
    [Key]
    [MaxLength(50)]
    public string ItemCode { get; set; } = string.Empty;

    /// <summary>Item name, denormalised at the time the factor was captured.</summary>
    [MaxLength(200)]
    public string? ItemName { get; set; }

    /// <summary>Litres represented by one sold unit.</summary>
    [Column(TypeName = "decimal(18,6)")]
    public decimal VolumeFactor { get; set; }

    /// <summary>Free-text note, e.g. how the factor was derived.</summary>
    [MaxLength(500)]
    public string? Notes { get; set; }

    /// <summary>
    /// Cleared when a factor should stop being applied. An inactive row still reports the item, but
    /// as unconverted rather than at a stale factor.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }

    [MaxLength(100)]
    public string? UpdatedBy { get; set; }
}
