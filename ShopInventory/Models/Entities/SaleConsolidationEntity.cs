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
/// End-of-day consolidated SAP invoice for a business partner.
/// Groups all DesktopSaleEntity records for a BP into one SAP invoice.
/// </summary>
[Index(nameof(CardCode), nameof(ConsolidationDate))]
[Index(nameof(ConsolidationDate))]
[Index(nameof(Status))]
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
