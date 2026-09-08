using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// One item and warehouse SAP was holding below zero, on the day it was counted.
/// </summary>
/// <remarks>
/// <para>The outcome measure. Every guard in this area stops a document that <i>would</i> take stock
/// under; this counts the ones that already did, and it is the only number that says whether any of
/// the guards worked. A claim that negative stock is fixed is worth nothing without a figure that
/// was falling while it was being fixed.</para>
///
/// <para><b>Kept as history, not as a current state.</b> One row per item, warehouse and observation,
/// so the question "is this getting better" can be answered by comparing two days rather than by
/// remembering what yesterday looked like. That makes the table grow, slowly and boundedly: it holds
/// only what is actually negative, which on a healthy company is nothing at all.</para>
/// </remarks>
[Index(nameof(ObservedOn))]
[Index(nameof(WarehouseCode), nameof(ItemCode))]
public class NegativeStockObservationEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>The day this count belongs to.</summary>
    [Column(TypeName = "date")]
    public DateTime ObservedOn { get; set; }

    public DateTime ObservedAt { get; set; } = DateTime.UtcNow;

    [Required]
    [MaxLength(20)]
    public string WarehouseCode { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    public string ItemCode { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? ItemName { get; set; }

    /// <summary>How far below zero SAP is holding it. Always negative, or the row would not exist.</summary>
    [Column(TypeName = "decimal(18,6)")]
    public decimal OnHand { get; set; }

    /// <summary>What SAP had already committed against it — context for how it got here.</summary>
    [Column(TypeName = "decimal(18,6)")]
    public decimal Committed { get; set; }
}
