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
}
