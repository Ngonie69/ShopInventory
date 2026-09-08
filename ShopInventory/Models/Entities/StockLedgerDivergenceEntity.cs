using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// One recorded disagreement between the stock ledger and what SAP actually holds.
/// </summary>
/// <remarks>
/// The ledger is the morning snapshot less everything this system has promised since, so it can only
/// be right about stock this system moved. Anything done directly in SAP B1 — a goods issue, a
/// manual adjustment, an invoice raised in the client — is invisible to it, and the gap grows
/// silently through the day until the next morning's fetch papers over it.
///
/// <para>
/// Recorded rather than corrected, deliberately. Quietly writing SAP's figure into the ledger would
/// hide the thing worth knowing: that something is moving stock the system cannot see. Every row
/// here is a question for a person, and the count over time is the measure of whether the ledger can
/// be trusted at all.
/// </para>
/// </remarks>
[Index(nameof(CheckedAt))]
[Index(nameof(WarehouseCode), nameof(ItemCode))]
public class StockLedgerDivergenceEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>The ledger day the comparison was made against.</summary>
    [Column(TypeName = "date")]
    public DateTime LedgerDay { get; set; }

    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;

    [Required]
    [MaxLength(20)]
    public string WarehouseCode { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    public string ItemCode { get; set; } = string.Empty;

    /// <summary>What the ledger said was left to promise.</summary>
    [Column(TypeName = "decimal(18,6)")]
    public decimal LedgerQuantity { get; set; }

    /// <summary>What SAP could actually issue: on hand, less what is already committed.</summary>
    [Column(TypeName = "decimal(18,6)")]
    public decimal SapIssuableQuantity { get; set; }

    /// <summary>
    /// Ledger less SAP. Positive means the ledger is promising stock SAP does not have, which is the
    /// direction that oversells; negative means it is refusing sales that could be made.
    /// </summary>
    [Column(TypeName = "decimal(18,6)")]
    public decimal Difference { get; set; }
}
