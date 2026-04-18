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
/// Fiscalized immediately via Revmax; posted to SAP at end of day as part of a consolidated invoice.
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

    // --- Consolidation ---

    public DesktopSaleConsolidationStatus ConsolidationStatus { get; set; } = DesktopSaleConsolidationStatus.Pending;

    public int? ConsolidationId { get; set; }

    [ForeignKey(nameof(ConsolidationId))]
    public SaleConsolidationEntity? Consolidation { get; set; }

    // --- Warehouse / Payment ---

    [Required]
    [MaxLength(20)]
    public string WarehouseCode { get; set; } = string.Empty;

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
