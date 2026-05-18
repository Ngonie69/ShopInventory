using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

[Table("BusinessPartnerSpecialPrices")]
[Index(nameof(CardCode), nameof(ItemCode), IsUnique = true)]
public class BusinessPartnerSpecialPriceEntity
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(50)]
    public string CardCode { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    public string ItemCode { get; set; } = string.Empty;

    [Precision(18, 6)]
    [Range(0, double.MaxValue, ErrorMessage = "Price cannot be negative")]
    public decimal Price { get; set; }

    public DateTime? ValidFrom { get; set; }

    public DateTime? ValidTo { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }

    public bool SyncedFromSAP { get; set; }

    public DateTime? LastSyncedAt { get; set; }
}