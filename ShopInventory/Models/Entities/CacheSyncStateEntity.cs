using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ShopInventory.Models.Entities;

[Table("CacheSyncStates")]
public class CacheSyncStateEntity
{
    [Key]
    [MaxLength(100)]
    public string CacheKey { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string DisplayName { get; set; } = string.Empty;

    public DateTime? LastSyncedAt { get; set; }

    public int ItemCount { get; set; }

    [MaxLength(1000)]
    public string? LastError { get; set; }

    public DateTime? LastErrorAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}