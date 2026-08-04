namespace ShopInventory.Features.Reports.Queries.GetItemVolumeSalesReport;

/// <summary>
/// One item's movement, either across every selected account or within a single one.
/// </summary>
public sealed class ItemVolumeSalesItemResult
{
    public string ItemCode { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;

    public int InvoiceCount { get; set; }
    public int CreditNoteCount { get; set; }

    public decimal InvoicedQuantity { get; set; }
    public decimal CreditedQuantity { get; set; }

    /// <summary>Invoiced less credited. Negative when the range holds more credits than sales.</summary>
    public decimal NetQuantity { get; set; }

    /// <summary>
    /// Litres per sold unit, or null when the item has no active conversion factor. Null is not the
    /// same as zero: the quantity is real, only the volume is unknown.
    /// </summary>
    public decimal? VolumeFactor { get; set; }

    public bool HasVolumeFactor => VolumeFactor.HasValue;

    /// <summary><see cref="NetQuantity"/> at <see cref="VolumeFactor"/>; zero when there is no factor.</summary>
    public decimal NetVolume { get; set; }

    public decimal InvoicedVolume { get; set; }
    public decimal CreditedVolume { get; set; }

    public decimal InvoicedSalesUsd { get; set; }
    public decimal InvoicedSalesZig { get; set; }
    public decimal CreditedSalesUsd { get; set; }
    public decimal CreditedSalesZig { get; set; }
    public decimal NetRevenueUsd { get; set; }
    public decimal NetRevenueZig { get; set; }
}
