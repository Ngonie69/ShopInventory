using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// Price List entity for caching SAP price lists in PostgreSQL
/// </summary>
[Table("PriceLists")]
[Index(nameof(ListNum), IsUnique = true)]
public class PriceListEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// SAP Price List Number (OPLN.ListNum)
    /// </summary>
    public int ListNum { get; set; }

    /// <summary>
    /// Price List Name (OPLN.ListName)
    /// </summary>
    [MaxLength(200)]
    public string? ListName { get; set; }

    /// <summary>
    /// Base Price List number for derived prices
    /// </summary>
    public int? BasePriceList { get; set; }

    /// <summary>
    /// Currency code for this price list
    /// </summary>
    [MaxLength(10)]
    public string? Currency { get; set; }

    /// <summary>
    /// Factor/multiplier for derived prices
    /// </summary>
    [Precision(10, 6)]
    public decimal? Factor { get; set; }

    /// <summary>
    /// Rounding method used
    /// </summary>
    [MaxLength(50)]
    public string? RoundingMethod { get; set; }

    /// <summary>
    /// Whether the price list is active/valid
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Number of items with prices in this list (cached count)
    /// </summary>
    public int ItemCount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }

    public DateTime? LastSyncedAt { get; set; }
}
