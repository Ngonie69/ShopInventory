using System.ComponentModel.DataAnnotations;

namespace ShopInventory.Models.Entities;

/// <summary>
/// A canonical SAP unit of measure resolved for one item plus the UoM a sales order line asked
/// for. Resolving this from SAP costs a create/read/delete round-trip against the SQLQueries
/// endpoint per batch of items, which is the dominant cost of posting an approved order, so the
/// answer is persisted rather than held only in process memory: it has to survive a restart and
/// be shared across API nodes.
/// </summary>
public class SapItemUomMappingEntity
{
    [Key]
    public long Id { get; set; }

    [Required]
    [MaxLength(100)]
    public string ItemCode { get; set; } = string.Empty;

    /// <summary>
    /// The UoM the order line asked for, normalised. Empty when the line named none — resolution
    /// depends on the requested unit, so it is part of the key rather than a detail of the answer.
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string RequestedUomCode { get; set; } = string.Empty;

    [MaxLength(50)]
    public string? UoMCode { get; set; }

    /// <summary>
    /// SAP's UoMEntry. Only resolutions that produced one are stored: caching a miss would keep
    /// posts using the fallback unit for the whole cache lifetime after the item master is fixed.
    /// </summary>
    public int UoMEntry { get; set; }

    public DateTime ResolvedAtUtc { get; set; }
}
