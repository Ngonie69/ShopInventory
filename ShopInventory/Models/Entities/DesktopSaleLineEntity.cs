using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// Line item on a desktop sale.
/// </summary>
[Index(nameof(SaleId))]
[Index(nameof(ItemCode), nameof(WarehouseCode))]
public class DesktopSaleLineEntity
{
    [Key]
    public int Id { get; set; }

    public int SaleId { get; set; }

    [ForeignKey(nameof(SaleId))]
    public DesktopSaleEntity Sale { get; set; } = null!;

    public int LineNum { get; set; }

    [Required]
    [MaxLength(50)]
    public string ItemCode { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? ItemDescription { get; set; }

    [Column(TypeName = "decimal(18,6)")]
    public decimal Quantity { get; set; }

    [Column(TypeName = "decimal(18,6)")]
    public decimal UnitPrice { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal LineTotal { get; set; }

    [Required]
    [MaxLength(20)]
    public string WarehouseCode { get; set; } = string.Empty;

    [MaxLength(20)]
    public string? TaxCode { get; set; }

    [Column(TypeName = "decimal(5,2)")]
    public decimal DiscountPercent { get; set; }

    [MaxLength(20)]
    public string? UoMCode { get; set; }

    // --- Fiscal detail, on a van sale line that was signed offline ---
    //
    // The fiscalisation platform rebuilds the signed receipt from these lines, so the values a handset
    // signed under have to survive here. They are unused by the desktop route and by SAP posting.

    /// <summary>The FDMS tax id the line was signed under.</summary>
    public int? TaxId { get; set; }

    /// <summary>
    /// The rate in force at signing. Null and zero are not the same: null is untaxed and contributes
    /// nothing to the signed payload, zero is a zero rate and contributes "0.00".
    /// </summary>
    [Column(TypeName = "decimal(5,2)")]
    public decimal? TaxPercent { get; set; }

    [MaxLength(20)]
    public string? HsCode { get; set; }

    [NotMapped]
    public string? CostCentreCode { get; set; }
}
