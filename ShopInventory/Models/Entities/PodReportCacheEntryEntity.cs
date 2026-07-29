using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ShopInventory.Models.Entities;

/// <summary>
/// A persisted, already-authorized POD report snapshot. The invoice, credit-note and crate
/// classification data comes from SAP and is expensive to rebuild; POD upload state is deliberately
/// refreshed from the local attachment tables whenever the snapshot is read.
/// </summary>
[Table("PodReportCacheEntries")]
public sealed class PodReportCacheEntryEntity
{
    [Key]
    public long Id { get; set; }

    [Required]
    [MaxLength(100)]
    public string CacheKey { get; set; } = string.Empty;

    public DateTime FromDate { get; set; }

    public DateTime ToDate { get; set; }

    [Required]
    [MaxLength(80)]
    public string ScopeKey { get; set; } = string.Empty;

    [Required]
    public string PayloadJson { get; set; } = string.Empty;

    public DateTime RefreshedAtUtc { get; set; }

    public DateTime ExpiresAtUtc { get; set; }
}
