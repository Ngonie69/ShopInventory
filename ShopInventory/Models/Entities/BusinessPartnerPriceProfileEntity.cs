using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

[Table("BusinessPartnerPriceProfiles")]
[Index(nameof(CardCode), IsUnique = true)]
public class BusinessPartnerPriceProfileEntity
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(50)]
    public string CardCode { get; set; } = string.Empty;

    [MaxLength(255)]
    public string? CardName { get; set; }

    [MaxLength(20)]
    public string? Currency { get; set; }

    public int PriceListNum { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }

    public bool SyncedFromSAP { get; set; }

    public DateTime? LastSyncedAt { get; set; }
}