using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// Where one business partner's daily incoming payment got to.
/// </summary>
public enum DailyIncomingPaymentStatus
{
    /// <summary>Its invoices are claimed and it has not been sent, or a send was refused and may be retried.</summary>
    Pending,

    /// <summary>Sent, and SAP's answer never arrived. Nothing is sent again until SAP has been asked.</summary>
    Unresolved,

    /// <summary>SAP holds the payment.</summary>
    Posted,

    /// <summary>Nothing was left to pay once SAP's balances were read, so nothing was sent.</summary>
    NothingToPay,

    /// <summary>
    /// Never reached SAP by the end of its day. Its invoices were handed back and folded into a later
    /// day's payment.
    /// </summary>
    Released
}

/// <summary>
/// The one incoming payment a business partner gets for a day's till and vending invoices, and for the
/// older desktop app's consolidated invoice.
/// </summary>
/// <remarks>
/// <para>
/// One row per customer per day, and the unique index is what holds that: a second pass on the same day
/// finds the row and leaves the customer alone, so an invoice posted after the day's payment went out
/// waits for the next day's rather than getting a payment of its own.
/// </para>
/// <para>
/// The invoices it settles are claimed before anything is sent, as <see cref="Lines"/>, so the same
/// invoice can never be put on two payments. A line is only handed back once SAP has shown the payment
/// does not exist.
/// </para>
/// </remarks>
[Table("DailyIncomingPayments")]
[Index(nameof(CardCode), nameof(PaymentDate), IsUnique = true)]
// Every pass asks which rows are not finished.
[Index(nameof(Status))]
public class DailyIncomingPaymentEntity
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(50)]
    public string CardCode { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? CardName { get; set; }

    /// <summary>The trading day the payment closes, in CAT. It is also the payment's SAP DocDate.</summary>
    [Column(TypeName = "date")]
    public DateTime PaymentDate { get; set; }

    /// <summary>
    /// The payment's business key, written at the start of its SAP Remarks.
    /// </summary>
    /// <remarks>
    /// SAP has no idempotency for a payment: ClientRequestId is not forwarded. So a lost reply is resolved
    /// by listing the customer's payments for the day and looking for this at the start of the Remarks.
    /// </remarks>
    [Required]
    [MaxLength(80)]
    public string Reference { get; set; } = string.Empty;

    public DailyIncomingPaymentStatus Status { get; set; } = DailyIncomingPaymentStatus.Pending;

    [Column(TypeName = "decimal(18,2)")]
    public decimal CashSum { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TransferSum { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal CreditSum { get; set; }

    /// <summary>Written before the request goes out and committed on its own, so a lost reply is known about.</summary>
    public DateTime? PostIssuedAtUtc { get; set; }

    public int? SapDocEntry { get; set; }

    public int? SapDocNum { get; set; }

    public DateTime? PostedAtUtc { get; set; }

    /// <summary>Refusals so far. Transient failures do not count.</summary>
    public int Attempts { get; set; }

    [MaxLength(2000)]
    public string? LastError { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<DailyIncomingPaymentLineEntity> Lines { get; set; } = new();
}

/// <summary>
/// One invoice on a daily incoming payment, and what is applied to it by tender.
/// </summary>
/// <remarks>
/// Exactly one of <see cref="DesktopSaleId"/> and <see cref="SaleConsolidationId"/> is set: a till or
/// vending sale's own invoice, or the older desktop app's consolidated invoice. The amounts are what the
/// till recorded as paid, reduced to the invoice's open balance when SAP is read just before posting.
/// </remarks>
[Table("DailyIncomingPaymentLines")]
[Index(nameof(InvoiceDocEntry))]
[Index(nameof(DesktopSaleId))]
[Index(nameof(SaleConsolidationId))]
public class DailyIncomingPaymentLineEntity
{
    [Key]
    public int Id { get; set; }

    public int DailyIncomingPaymentId { get; set; }

    public DailyIncomingPaymentEntity? DailyIncomingPayment { get; set; }

    public int? DesktopSaleId { get; set; }

    public int? SaleConsolidationId { get; set; }

    public int InvoiceDocEntry { get; set; }

    public int? InvoiceDocNum { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal CashAmount { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TransferAmount { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal CreditAmount { get; set; }

    [NotMapped]
    public decimal SumApplied => CashAmount + TransferAmount + CreditAmount;
}
