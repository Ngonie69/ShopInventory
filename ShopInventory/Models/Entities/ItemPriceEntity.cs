using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// Item price entity for PostgreSQL storage
/// </summary>
[Table("ItemPrices")]
public class ItemPriceEntity
{
    [Key]
    public int Id { get; set; }

    public int? ProductId { get; set; }

    [Required]
    [MaxLength(50)]
    public string ItemCode { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? ItemName { get; set; }

    /// <summary>
    /// SAP Price List number
    /// </summary>
    public int PriceList { get; set; }

    [MaxLength(100)]
    public string? PriceListName { get; set; }

    /// <summary>
    /// Price - must be non-negative
    /// </summary>
    [Precision(18, 6)]
    [Range(0, double.MaxValue, ErrorMessage = "Price cannot be negative")]
    public decimal Price { get; set; }

    [MaxLength(10)]
    public string? Currency { get; set; }

    [MaxLength(20)]
    public string? UoMCode { get; set; }

    /// <summary>
    /// Base price list for price derivation
    /// </summary>
    public int? BasePriceList { get; set; }

    /// <summary>
    /// Factor/multiplier for derived prices
    /// </summary>
    [Precision(10, 6)]
    public decimal? Factor { get; set; }

    public DateTime? ValidFrom { get; set; }

    public DateTime? ValidTo { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }

    public bool IsActive { get; set; } = true;

    public bool SyncedFromSAP { get; set; }

    public DateTime? LastSyncedAt { get; set; }

    // Navigation property
    [ForeignKey(nameof(ProductId))]
    public ProductEntity? Product { get; set; }
}
