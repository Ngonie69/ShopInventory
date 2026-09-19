using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// One product on a <see cref="MarketBreakageEntity"/>.
/// </summary>
[Index(nameof(BreakageId))]
public sealed class MarketBreakageLineEntity
{
    [Key]
    public int Id { get; set; }

    public int BreakageId { get; set; }

    [ForeignKey(nameof(BreakageId))]
    public MarketBreakageEntity Breakage { get; set; } = null!;

    public int LineNum { get; set; }

    [Required]
    [MaxLength(50)]
    public string ItemCode { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? ItemDescription { get; set; }

    /// <summary>Why it came back — broken, expired, damaged, leaking…</summary>
    [MaxLength(100)]
    public string? Reason { get; set; }

    /// <summary>What the rep said they collected, in the item's inventory unit. Never changed.</summary>
    [Column(TypeName = "decimal(18,6)")]
    public decimal ReportedQuantity { get; set; }

    /// <summary>
    /// What the office counted off the van. Null until the report is confirmed; zero means the line
    /// is dropped from the transfer.
    /// </summary>
    [Column(TypeName = "decimal(18,6)")]
    public decimal? ConfirmedQuantity { get; set; }
}
