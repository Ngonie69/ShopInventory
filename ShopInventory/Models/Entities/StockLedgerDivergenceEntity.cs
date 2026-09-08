using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// How the ledger and reality were found to disagree.
/// </summary>
public static class StockLedgerDivergenceSources
{
    /// <summary>The hourly comparison found the ledger and SAP holding different figures.</summary>
    public const string SapComparison = "SapComparison";

    /// <summary>
    /// A document that had already happened took more than the ledger held. For a van that is what
    /// an over-sale looks like: the goods left the van hours ago and the invoice cannot be refused,
    /// so the only thing left to do is say so.
    /// </summary>
    public const string SettledDocument = "SettledDocument";
}

/// <summary>
/// One recorded disagreement between the stock ledger and reality.
/// </summary>
/// <remarks>
/// The ledger is the morning snapshot less everything this system has promised since, so it can only
/// be right about stock this system moved. Anything done directly in SAP B1 — a goods issue, a
/// manual adjustment, an invoice raised in the client — is invisible to it, and the gap grows
/// silently through the day until the next morning's fetch papers over it.
///
/// <para>
/// Two ways of noticing, both recorded here so there is one list to read rather than two. The hourly
/// comparison asks SAP. A settled document reports itself: a van sale posts an invoice for stock
/// that left the van before anyone here knew about it, and if the van did not have it, that is
/// discovered at the moment the sale is recorded rather than by asking anybody.
/// </para>
///
/// <para>
/// Recorded rather than corrected, deliberately. Quietly writing SAP's figure into the ledger would
/// hide the thing worth knowing: that something is moving stock the system cannot see. Every row
/// here is a question for a person, and the count over time is the measure of whether the ledger can
/// be trusted at all.
/// </para>
/// </remarks>
[Index(nameof(CheckedAt))]
[Index(nameof(Source))]
[Index(nameof(WarehouseCode), nameof(ItemCode))]
public class StockLedgerDivergenceEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>The ledger day the comparison was made against.</summary>
    [Column(TypeName = "date")]
    public DateTime LedgerDay { get; set; }

    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// How this was found — see <see cref="StockLedgerDivergenceSources"/>.
    /// </summary>
    [Required]
    [MaxLength(40)]
    public string Source { get; set; } = StockLedgerDivergenceSources.SapComparison;

    /// <summary>
    /// The document that reported it, when one did. Null for the hourly comparison, which is asking
    /// about a warehouse rather than about a document.
    /// </summary>
    [MaxLength(100)]
    public string? Reference { get; set; }

    [Required]
    [MaxLength(20)]
    public string WarehouseCode { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    public string ItemCode { get; set; } = string.Empty;

    /// <summary>What the ledger said was left to promise.</summary>
    [Column(TypeName = "decimal(18,6)")]
    public decimal LedgerQuantity { get; set; }

    /// <summary>
    /// What reality held. For the hourly comparison that is what SAP could issue — on hand less
    /// committed. For a settled document it is what the document actually took.
    /// </summary>
    [Column(TypeName = "decimal(18,6)")]
    public decimal SapIssuableQuantity { get; set; }

    /// <summary>
    /// Ledger less reality. Positive means the ledger is promising stock that is not there, which is
    /// the direction that oversells; negative means it is refusing sales that could be made.
    /// </summary>
    [Column(TypeName = "decimal(18,6)")]
    public decimal Difference { get; set; }
}
