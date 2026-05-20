using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

[Index(nameof(ClientTransactionId), IsUnique = true)]
[Index(nameof(TimestampUtc))]
[Index(nameof(Status), nameof(DocumentType), nameof(TimestampUtc))]
public class DesktopFiscalTransactionEntity
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(100)]
    public string ClientTransactionId { get; set; } = string.Empty;

    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    [Required]
    [MaxLength(40)]
    public string DocumentType { get; set; } = string.Empty;

    public int DocNum { get; set; }

    [Required]
    [MaxLength(40)]
    public string Status { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? Message { get; set; }

    [MaxLength(120)]
    public string? VerificationCode { get; set; }

    [MaxLength(2000)]
    public string? QRCode { get; set; }

    [MaxLength(120)]
    public string? DeviceSerialNumber { get; set; }

    [MaxLength(120)]
    public string? DeviceId { get; set; }

    [MaxLength(40)]
    public string? FiscalDay { get; set; }

    public int? ReceiptGlobalNo { get; set; }

    [MaxLength(50)]
    public string? CardCode { get; set; }

    [MaxLength(255)]
    public string? CardName { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal DocTotal { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal VatSum { get; set; }

    [MaxLength(10)]
    public string? Currency { get; set; }

    [MaxLength(50)]
    public string? OriginalInvoiceNumber { get; set; }

    public string? RawRequest { get; set; }

    public string? RawResponse { get; set; }

    [Required]
    [MaxLength(50)]
    public string SourceSystem { get; set; } = "InvoiceFiscalisation";

    [MaxLength(100)]
    public string? CreatedByUserId { get; set; }

    [MaxLength(100)]
    public string? CreatedByUsername { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime LastSyncedAtUtc { get; set; } = DateTime.UtcNow;
}