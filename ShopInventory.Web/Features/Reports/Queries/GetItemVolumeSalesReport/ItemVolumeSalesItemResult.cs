namespace ShopInventory.Web.Features.Reports.Queries.GetItemVolumeSalesReport;

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

    // Money is USD, every document included. SAP's line total is in the company's local currency,
    // USD, so a ZiG document's lines arrive already converted at that document's own rate — what
    // SAP's DocTotal carries. The FromZig figures are the part that came off ZiG documents: already
    // inside the USD figure beside them, and never to be added to it.
    public decimal InvoicedSalesUsd { get; set; }
    public decimal InvoicedSalesFromZig { get; set; }
    public decimal CreditedSalesUsd { get; set; }
    public decimal CreditedSalesFromZig { get; set; }
    public decimal NetRevenueUsd { get; set; }
    public decimal NetRevenueFromZig { get; set; }

    /// <summary>
    /// SAP's booked gross profit on these lines in USD, net of credit notes: each invoice line's value
    /// before VAT less the stock cost SAP relieved, less the same on the credit notes.
    /// </summary>
    public decimal NetGrossProfitUsd { get; set; }

    /// <summary><see cref="NetGrossProfitUsd"/> over <see cref="NetRevenueUsd"/>, to one place; null with no net revenue.</summary>
    public decimal? NetMarginPercent => NetRevenueUsd == 0
        ? null
        : Math.Round(NetGrossProfitUsd / NetRevenueUsd * 100m, 1, MidpointRounding.AwayFromZero);
}
