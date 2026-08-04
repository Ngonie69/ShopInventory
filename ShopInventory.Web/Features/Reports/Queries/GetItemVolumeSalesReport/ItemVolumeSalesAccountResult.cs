namespace ShopInventory.Web.Features.Reports.Queries.GetItemVolumeSalesReport;

/// <summary>
/// One business partner's movement over the window, with the per-item breakdown behind it.
/// </summary>
public sealed class ItemVolumeSalesAccountResult
{
    public string CardCode { get; set; } = string.Empty;
    public string CardName { get; set; } = string.Empty;

    public int InvoiceCount { get; set; }
    public int CreditNoteCount { get; set; }

    public decimal InvoicedQuantity { get; set; }
    public decimal CreditedQuantity { get; set; }
    public decimal NetQuantity { get; set; }

    public decimal NetVolume { get; set; }
    public decimal InvoicedVolume { get; set; }
    public decimal CreditedVolume { get; set; }

    /// <summary>Items this account traded that carry no conversion factor.</summary>
    public int ItemsWithoutFactorCount { get; set; }

    public decimal InvoicedSalesUsd { get; set; }
    public decimal InvoicedSalesZig { get; set; }
    public decimal CreditedSalesUsd { get; set; }
    public decimal CreditedSalesZig { get; set; }
    public decimal NetRevenueUsd { get; set; }
    public decimal NetRevenueZig { get; set; }

    public List<ItemVolumeSalesItemResult> Items { get; set; } = new();
}
