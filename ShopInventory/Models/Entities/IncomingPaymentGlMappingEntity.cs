using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ShopInventory.Models.Entities;

/// <summary>
/// Which daily run pays a business partner's invoices.
/// </summary>
public enum DailyPaymentRun
{
    /// <summary>The 17:00 run: shop tills, vending and the older desktop app.</summary>
    Shops,

    /// <summary>
    /// The evening run. Van invoices post at 18:00 with a 19:30 mop-up, so a 17:00 cut-off would always
    /// pay them a day late.
    /// </summary>
    Vans
}

/// <summary>
/// The G/L accounts a business partner's daily incoming payment posts to, and who is told about it.
/// </summary>
/// <remarks>
/// <para>
/// SAP uses its own default account for any means of payment that has no account set, and in this company
/// database that default is 700300, the factory's cash on hand. Before this mapping existed every daily
/// payment went there, including Cortina's and Machipisa's, whose money never passes through the factory.
/// A partner with no active mapping is therefore held rather than posted.
/// </para>
/// <para>
/// Cash goes to <see cref="CashAccount"/>. Everything else (Ecocash, Innbucks, swipe) goes to
/// <see cref="ElectronicAccount"/> as transfer money. SAP carries one transfer account per payment, and
/// one per partner is what makes that enough.
/// </para>
/// </remarks>
[Table("IncomingPaymentGlMappings")]
public class IncomingPaymentGlMappingEntity
{
    [Key]
    [MaxLength(50)]
    public string CardCode { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? CardName { get; set; }

    [Required]
    [MaxLength(20)]
    public string CashAccount { get; set; } = string.Empty;

    [Required]
    [MaxLength(20)]
    public string ElectronicAccount { get; set; } = string.Empty;

    public DailyPaymentRun Run { get; set; } = DailyPaymentRun.Shops;

    /// <summary>
    /// The people who receive the cash on the ground, as a list separated by commas. They are emailed once
    /// the day's payment has posted.
    /// </summary>
    [MaxLength(1000)]
    public string? NotifyEmails { get; set; }

    /// <summary>
    /// Set on a seeded row whose electronic account the pre-September history did not confirm, until
    /// someone saves the row.
    /// </summary>
    public bool NeedsReview { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    [MaxLength(100)]
    public string? UpdatedBy { get; set; }
}
