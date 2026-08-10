using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

public enum ConsolidationStatus
{
    Pending,
    Posted,
    PartiallyCompleted,
    Failed
}

/// <summary>
/// One sale that was fiscalised before SAP and then folded into a consolidated invoice.
/// </summary>
/// <param name="Reference">The sale's external reference.</param>
/// <param name="FiscalInvoiceNumber">
/// The invoice number the receipt was submitted under. Half of the fiscalisation platform's
/// idempotency key — a consolidated invoice fiscalised under its SAP DocNum would not collide with
/// any of these, which is exactly why the platform cannot catch the duplicate itself.
/// </param>
/// <param name="ReceiptGlobalNo">The receipt the platform returned, where one came back.</param>
/// <param name="Amount">The sale total, so the parts can be reconciled against the whole.</param>
public sealed record ConsolidatedFiscalReceipt(
    string? Reference,
    string? FiscalInvoiceNumber,
    string? ReceiptGlobalNo,
    decimal Amount);

/// <summary>
/// End-of-day consolidated SAP invoice for a business partner.
/// Groups all DesktopSaleEntity records for a BP into one SAP invoice.
/// </summary>
[Index(nameof(CardCode), nameof(ConsolidationDate))]
[Index(nameof(ConsolidationDate))]
[Index(nameof(Status))]
[Index(nameof(SapDocNum))]
public class SaleConsolidationEntity
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(50)]
    public string CardCode { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? CardName { get; set; }

    [Column(TypeName = "date")]
    public DateTime ConsolidationDate { get; set; }

    [MaxLength(20)]
    public string? WarehouseCode { get; set; }

    // --- SAP Invoice ---

    public int? SapDocEntry { get; set; }
    public int? SapDocNum { get; set; }
    public DateTime? PostedAt { get; set; }

    public ConsolidationStatus Status { get; set; } = ConsolidationStatus.Pending;

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalAmount { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalVat { get; set; }

    /// <summary>
    /// Number of individual desktop sales in this consolidation.
    /// </summary>
    public int SaleCount { get; set; }

    /// <summary>
    /// The receipts the constituent sales were fiscalised under, before SAP, as a JSON array of
    /// <see cref="ConsolidatedFiscalReceipt"/>.
    /// </summary>
    /// <remarks>
    /// The consolidated invoice's own DocNum is not the number any of these sales were fiscalised
    /// under — each till sale went to FDMS on its own, under its own reference, and only then was
    /// merged into one SAP document. Nothing else in the system records that link once the queue
    /// entries are marked Completed, so without this column a reconciliation starting from the SAP
    /// invoice has no way back to the receipts that make it up.
    ///
    /// Deliberately unbounded: a busy till day is far more entries than a MaxLength that looked
    /// generous when it was chosen, and silently truncating this to fit would defeat the point.
    /// </remarks>
    public string? ConstituentFiscalReceipts { get; set; }

    // --- SAP Incoming Payment ---

    public int? PaymentSapDocEntry { get; set; }
    public int? PaymentSapDocNum { get; set; }
    public DateTime? PaymentPostedAt { get; set; }

    [MaxLength(50)]
    public string? PaymentStatus { get; set; }

    // --- Error tracking ---

    [MaxLength(2000)]
    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<DesktopSaleEntity> Sales { get; set; } = new();
}
