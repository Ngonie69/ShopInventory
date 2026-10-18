using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// One SAP invoice whose booked cost has been copied here, for one warehouse's lines on it.
/// </summary>
/// <remarks>
/// <para>
/// The sales reports' gross profit is SAP's <c>GrssProfit</c> on the invoices the sales were posted
/// as. Read live on each page load it missed the page's 30-second deadline on every attempt in
/// production on 2026-10-18 — the INV1 read itself took the whole 30 s while SAP stock reads beside it
/// were hitting 60 s. A posted invoice's booked profit does not change (it is fixed at the cost when
/// the goods left), so <see cref="Services.SaleInvoiceCostSyncService"/> copies it once, in the
/// background, and the reports read it from here.
/// </para>
/// <para>
/// A row with no lines is still a read: the invoice was cancelled, or holds nothing from this
/// warehouse. Either way it has no cost to report, and it is not asked for again.
/// </para>
/// </remarks>
[Index(nameof(DocEntry), nameof(WarehouseCode), IsUnique = true)]
public class SaleInvoiceCostEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>The SAP invoice: a posted sale's <c>SapDocEntry</c>, or its consolidation's.</summary>
    public int DocEntry { get; set; }

    /// <summary>The warehouse whose lines were read, as the sale names it.</summary>
    [Required]
    [MaxLength(20)]
    public string WarehouseCode { get; set; } = string.Empty;

    /// <summary>When the lines were read from SAP, UTC.</summary>
    public DateTime ReadAt { get; set; }

    public List<SaleInvoiceCostLineEntity> Lines { get; set; } = [];
}

/// <summary>One invoice line with the cost SAP booked for it, in the invoice's own currency.</summary>
public class SaleInvoiceCostLineEntity
{
    [Key]
    public int Id { get; set; }

    public int SaleInvoiceCostId { get; set; }

    [ForeignKey(nameof(SaleInvoiceCostId))]
    public SaleInvoiceCostEntity? Invoice { get; set; }

    [Required]
    [MaxLength(50)]
    public string ItemCode { get; set; } = string.Empty;

    [Column(TypeName = "decimal(18,6)")]
    public decimal Quantity { get; set; }

    /// <summary>The line before VAT.</summary>
    [Column(TypeName = "decimal(18,6)")]
    public decimal Revenue { get; set; }

    /// <summary>SAP's gross profit on the line: the revenue less the stock value relieved when the goods left.</summary>
    [Column(TypeName = "decimal(18,6)")]
    public decimal GrossProfit { get; set; }
}
