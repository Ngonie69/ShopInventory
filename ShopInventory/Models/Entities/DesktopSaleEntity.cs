using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

public enum DesktopSaleFiscalizationStatus
{
    Pending,
    Success,
    Failed,
    Skipped
}

public enum DesktopSaleConsolidationStatus
{
    Pending,
    Consolidated,
    Failed,
    Excluded
}

/// <summary>
/// A local invoice created by the desktop app during the day.
/// Fiscalised immediately; posted to SAP at end of day as part of a consolidated invoice.
/// </summary>
[Index(nameof(ExternalReferenceId), IsUnique = true)]
[Index(nameof(CardCode))]
[Index(nameof(ConsolidationStatus))]
[Index(nameof(DocDate))]
[Index(nameof(WarehouseCode))]
public class DesktopSaleEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Unique reference from the desktop app — idempotency key.
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string ExternalReferenceId { get; set; } = string.Empty;

    [MaxLength(50)]
    public string? SourceSystem { get; set; }

    [Required]
    [MaxLength(50)]
    public string CardCode { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? CardName { get; set; }

    [Column(TypeName = "date")]
    public DateTime DocDate { get; set; }

    public int? SalesPersonCode { get; set; }

    [MaxLength(100)]
    public string? NumAtCard { get; set; }

    [MaxLength(500)]
    public string? Comments { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalAmount { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal VatAmount { get; set; }

    [MaxLength(10)]
    public string Currency { get; set; } = "ZWG";

    // --- Fiscalization ---

    public DesktopSaleFiscalizationStatus FiscalizationStatus { get; set; } = DesktopSaleFiscalizationStatus.Pending;

    [MaxLength(100)]
    public string? FiscalReceiptNumber { get; set; }

    [MaxLength(100)]
    public string? FiscalDeviceNumber { get; set; }

    [MaxLength(500)]
    public string? FiscalQRCode { get; set; }

    [MaxLength(200)]
    public string? FiscalVerificationCode { get; set; }

    [MaxLength(500)]
    public string? FiscalVerificationLink { get; set; }

    [MaxLength(50)]
    public string? FiscalDayNo { get; set; }

    [MaxLength(2000)]
    public string? FiscalError { get; set; }

    /// <summary>
    /// The ZIMRA receipt's global number, and the counter within its fiscal day.
    ///
    /// Only van sales carry these, and they are what make a posted SAP invoice traceable back to the
    /// receipt the customer holds. Reconciliation between SAP and FDMS has nothing else to join on: the
    /// receipt records the SAP DocNum as its InvoiceNo, but that is assigned hours later, at posting.
    /// </summary>
    public int? ReceiptGlobalNo { get; set; }

    public int? ReceiptCounter { get; set; }

    // --- Consolidation / posting ---

    /// <summary>
    /// Where the sale is in its journey to SAP. <see cref="DesktopSaleConsolidationStatus.Consolidated"/>
    /// means "SAP has it" for both routes: the desktop app's sales reach SAP inside a consolidated
    /// invoice, while van sales post one-to-one (see <c>VanSalesEndOfDayPostingService</c>) so that each
    /// SAP invoice still maps to exactly one ZIMRA receipt.
    /// </summary>
    public DesktopSaleConsolidationStatus ConsolidationStatus { get; set; } = DesktopSaleConsolidationStatus.Pending;

    public int? ConsolidationId { get; set; }

    [ForeignKey(nameof(ConsolidationId))]
    public SaleConsolidationEntity? Consolidation { get; set; }

    /// <summary>
    /// The SAP invoice this sale posted as, for the one-to-one route. Null for a consolidated sale,
    /// which is traced through <see cref="Consolidation"/> instead.
    /// </summary>
    public int? SapDocEntry { get; set; }

    public int? SapDocNum { get; set; }

    public DateTime? PostedAt { get; set; }

    /// <summary>
    /// Posting attempts so far. The 18:00 run and the 19:30 mop-up both increment this, so a sale that
    /// fails every night is visible rather than silently retried forever.
    /// </summary>
    public int PostingAttempts { get; set; }

    [MaxLength(2000)]
    public string? LastPostingError { get; set; }

    // --- Warehouse / Payment ---

    [Required]
    [MaxLength(20)]
    public string WarehouseCode { get; set; } = string.Empty;

    /// <summary>
    /// The van's cost centre. Mandatory for a van sale — <c>CreateVanSalesDirectInvoiceHandler</c>
    /// refuses to build an invoice without one — and unused by the desktop route.
    /// </summary>
    [MaxLength(50)]
    public string? CostCentreCode { get; set; }

    [MaxLength(50)]
    public string? PaymentMethod { get; set; }

    [MaxLength(100)]
    public string? PaymentReference { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal AmountPaid { get; set; }

    // --- Audit ---

    [MaxLength(100)]
    public string? CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<DesktopSaleLineEntity> Lines { get; set; } = new();
}
