using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// A WhatsApp number a customer gave us to receive their documents on, and the record of their
/// agreeing to it.
/// </summary>
/// <remarks>
/// <para>
/// Kept here rather than on the SAP business partner. SAP's phone fields hold whatever number someone
/// typed years ago, often a landline, and nothing in them says the customer agreed to be sent invoices
/// there. This row is created only with that agreement recorded — who took it, when and how — and an
/// opt-out stays on it, so a number that said stop is never quietly re-used.
/// </para>
/// <para>
/// The owner is either an account customer (<see cref="CardCode"/>) or a van route customer
/// (<see cref="RouteCustomerId"/>), never both; a check constraint holds that. SAP keeps one card per
/// currency for a shop, so a shop on three currencies has a row under each card it wants its invoices
/// for — the web offers to copy a number across a shop's cards rather than guessing which belong
/// together.
/// </para>
/// <para>
/// Rows are never deleted. Removing a number sets <see cref="RemovedAtUtc"/>, so the deliveries made
/// to it still name the consent they were made under.
/// </para>
/// </remarks>
[Index(nameof(PhoneE164))]
public class CustomerWhatsAppContactEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>The account customer's SAP card, or null for a route customer.</summary>
    [MaxLength(50)]
    public string? CardCode { get; set; }

    /// <summary>The van route customer, or null for an account customer.</summary>
    public int? RouteCustomerId { get; set; }

    [ForeignKey(nameof(RouteCustomerId))]
    public RouteCustomerEntity? RouteCustomer { get; set; }

    /// <summary>The customer's name when the number was saved, for lists that should not have to ask SAP.</summary>
    [Required]
    [MaxLength(200)]
    public string OwnerName { get; set; } = null!;

    /// <summary>The number in E.164 (<c>+263771234567</c>), normalised on the way in.</summary>
    [Required]
    [MaxLength(20)]
    public string PhoneE164 { get; set; } = null!;

    /// <summary>Who answers on this number — the buyer, the owner, the accounts clerk.</summary>
    [MaxLength(100)]
    public string? ContactName { get; set; }

    /// <summary>Whether new invoices are sent here without anyone pressing Send.</summary>
    public bool AutoSendInvoices { get; set; } = true;

    public WhatsAppConsentSource ConsentSource { get; set; }

    /// <summary>How the customer agreed — "asked on the phone", "signed the account form" — in the recorder's words.</summary>
    [MaxLength(500)]
    public string? ConsentNote { get; set; }

    public DateTime ConsentRecordedAtUtc { get; set; }

    public Guid? ConsentRecordedByUserId { get; set; }

    [Required]
    [MaxLength(100)]
    public string ConsentRecordedBy { get; set; } = null!;

    /// <summary>When the customer stopped receiving documents here. Nothing is sent to an opted-out number.</summary>
    public DateTime? OptedOutAtUtc { get; set; }

    public WhatsAppOptOutSource? OptedOutSource { get; set; }

    [MaxLength(100)]
    public string? OptedOutBy { get; set; }

    /// <summary>What the last "is this number on WhatsApp" check said; null when it was never asked.</summary>
    public bool? WhatsAppExists { get; set; }

    public DateTime? WhatsAppCheckedAtUtc { get; set; }

    /// <summary>When the number was taken off the customer. The row stays for the deliveries that name it.</summary>
    public DateTime? RemovedAtUtc { get; set; }

    [MaxLength(100)]
    public string? RemovedBy { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    [MaxLength(100)]
    public string? UpdatedBy { get; set; }
}
