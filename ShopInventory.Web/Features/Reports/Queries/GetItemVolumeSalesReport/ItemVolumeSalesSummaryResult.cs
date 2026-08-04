namespace ShopInventory.Web.Features.Reports.Queries.GetItemVolumeSalesReport;

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

    public decimal InvoicedSalesUsd { get; set; }
    public decimal InvoicedSalesZig { get; set; }
    public decimal CreditedSalesUsd { get; set; }
    public decimal CreditedSalesZig { get; set; }
    public decimal NetRevenueUsd { get; set; }
    public decimal NetRevenueZig { get; set; }
}
