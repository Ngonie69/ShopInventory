using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// Incoming payment entity for PostgreSQL storage
/// </summary>
[Table("IncomingPayments")]
public class IncomingPaymentEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// SAP DocEntry reference
    /// </summary>
    public int? SAPDocEntry { get; set; }

    /// <summary>
    /// SAP DocNum reference
    /// </summary>
    public int? SAPDocNum { get; set; }

    public DateTime DocDate { get; set; }

    public DateTime? DocDueDate { get; set; }

    [Required]
    [MaxLength(50)]
    public string CardCode { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? CardName { get; set; }

    [MaxLength(10)]
    public string? DocCurrency { get; set; }

    /// <summary>
    /// Cash sum - must be non-negative
    /// </summary>
    [Precision(18, 2)]
    [Range(0, double.MaxValue, ErrorMessage = "Cash sum cannot be negative")]
    public decimal CashSum { get; set; }

    [Precision(18, 2)]
    public decimal CashSumFC { get; set; }

    /// <summary>
    /// Check sum - must be non-negative
    /// </summary>
    [Precision(18, 2)]
    [Range(0, double.MaxValue, ErrorMessage = "Check sum cannot be negative")]
    public decimal CheckSum { get; set; }

    /// <summary>
    /// Transfer sum - must be non-negative
    /// </summary>
    [Precision(18, 2)]
    [Range(0, double.MaxValue, ErrorMessage = "Transfer sum cannot be negative")]
    public decimal TransferSum { get; set; }

    [Precision(18, 2)]
    public decimal TransferSumFC { get; set; }

    /// <summary>
    /// Credit sum - must be non-negative
    /// </summary>
    [Precision(18, 2)]
    [Range(0, double.MaxValue, ErrorMessage = "Credit sum cannot be negative")]
    public decimal CreditSum { get; set; }

    /// <summary>
    /// Document total - must be non-negative
    /// </summary>
    [Precision(18, 2)]
    [Range(0, double.MaxValue, ErrorMessage = "Document total cannot be negative")]
    public decimal DocTotal { get; set; }

    [Precision(18, 2)]
    public decimal DocTotalFc { get; set; }

    [MaxLength(500)]
    public string? Remarks { get; set; }

    [MaxLength(200)]
    public string? JournalRemarks { get; set; }

    [MaxLength(100)]
    public string? TransferReference { get; set; }

    public DateTime? TransferDate { get; set; }

    [MaxLength(50)]
    public string? TransferAccount { get; set; }

    [MaxLength(50)]
    public string? CashAccount { get; set; }

    [MaxLength(50)]
    public string Status { get; set; } = "Draft";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }

    public bool SyncedToSAP { get; set; }

    public DateTime? SyncedAt { get; set; }

    [MaxLength(500)]
    public string? SyncError { get; set; }

    // Navigation properties
    public ICollection<IncomingPaymentInvoiceEntity> PaymentInvoices { get; set; } = new List<IncomingPaymentInvoiceEntity>();
    public ICollection<IncomingPaymentCheckEntity> PaymentChecks { get; set; } = new List<IncomingPaymentCheckEntity>();
    public ICollection<IncomingPaymentCreditCardEntity> PaymentCreditCards { get; set; } = new List<IncomingPaymentCreditCardEntity>();
}

/// <summary>
/// Payment invoice allocation entity
/// </summary>
[Table("IncomingPaymentInvoices")]
public class IncomingPaymentInvoiceEntity
{
    [Key]
    public int Id { get; set; }

    public int IncomingPaymentId { get; set; }

    public int LineNum { get; set; }

    /// <summary>
    /// Reference to local Invoice (if exists)
    /// </summary>
    public int? InvoiceId { get; set; }

    /// <summary>
    /// SAP DocEntry of the invoice being paid
    /// </summary>
    public int? SAPDocEntry { get; set; }

    [Precision(18, 2)]
    public decimal SumApplied { get; set; }

    [Precision(18, 2)]
    public decimal SumAppliedFC { get; set; }

    [MaxLength(20)]
    public string? InvoiceType { get; set; }

    // Navigation properties
    [ForeignKey(nameof(IncomingPaymentId))]
    public IncomingPaymentEntity IncomingPayment { get; set; } = null!;

    [ForeignKey(nameof(InvoiceId))]
    public InvoiceEntity? Invoice { get; set; }
}

/// <summary>
/// Payment check entity
/// </summary>
[Table("IncomingPaymentChecks")]
public class IncomingPaymentCheckEntity
{
    [Key]
    public int Id { get; set; }

    public int IncomingPaymentId { get; set; }

    public int LineNum { get; set; }

    public DateTime? DueDate { get; set; }

    public int CheckNumber { get; set; }

    [MaxLength(50)]
    public string? BankCode { get; set; }

    [MaxLength(50)]
    public string? Branch { get; set; }

    [MaxLength(50)]
    public string? AccountNum { get; set; }

    [Precision(18, 2)]
    public decimal CheckSum { get; set; }

    [MaxLength(10)]
    public string? Currency { get; set; }

    // Navigation property
    [ForeignKey(nameof(IncomingPaymentId))]
    public IncomingPaymentEntity IncomingPayment { get; set; } = null!;
}

/// <summary>
/// Payment credit card entity
/// </summary>
[Table("IncomingPaymentCreditCards")]
public class IncomingPaymentCreditCardEntity
{
    [Key]
    public int Id { get; set; }

    public int IncomingPaymentId { get; set; }

    public int LineNum { get; set; }

    public int CreditCard { get; set; }

    [MaxLength(50)]
    public string? CreditCardNumber { get; set; }

    [MaxLength(10)]
    public string? CardValidUntil { get; set; }

    [MaxLength(50)]
    public string? VoucherNum { get; set; }

    [Precision(18, 2)]
    public decimal CreditSum { get; set; }

    [MaxLength(10)]
    public string? CreditCur { get; set; }

    // Navigation property
    [ForeignKey(nameof(IncomingPaymentId))]
    public IncomingPaymentEntity IncomingPayment { get; set; } = null!;
}
