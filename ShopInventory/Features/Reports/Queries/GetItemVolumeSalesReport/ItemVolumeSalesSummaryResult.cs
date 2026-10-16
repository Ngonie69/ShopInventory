namespace ShopInventory.Features.Reports.Queries.GetItemVolumeSalesReport;

public sealed class ItemVolumeSalesSummaryResult
{
    public int RequestedAccountCount { get; set; }
    public int ActiveAccountCount { get; set; }

    /// <summary>Zero when the caller asked for every item rather than a chosen list.</summary>
    public int RequestedItemCount { get; set; }

    public int ItemCount { get; set; }
    public int TotalPeriods { get; set; }

    public int InvoiceCount { get; set; }
    public int CreditNoteCount { get; set; }

    public decimal InvoicedQuantity { get; set; }
    public decimal CreditedQuantity { get; set; }
    public decimal NetQuantity { get; set; }

    public decimal InvoicedVolume { get; set; }
    public decimal CreditedVolume { get; set; }
    public decimal NetVolume { get; set; }

    /// <summary>
    /// Items that moved but could not be converted. The volume totals exclude them, so a non-zero
    /// count here means the volume is a floor rather than the whole picture.
    /// </summary>
    public int ItemsWithoutFactorCount { get; set; }

    public decimal QuantityWithoutFactor { get; set; }

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
}
