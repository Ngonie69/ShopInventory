namespace ShopInventory.Web.Features.Reports.Queries.GetItemVolumeSalesReport;

/// <summary>
/// One business partner's movement over the window, with the per-item breakdown behind it.
/// </summary>
public sealed class ItemVolumeSalesAccountResult
{
    public string CardCode { get; set; } = string.Empty;
    public string CardName { get; set; } = string.Empty;

    /// <summary>
    /// The trade channel the partner sells through, from SAP's <c>OCRD.U_Channel</c>. Empty for a partner
    /// with none, and for every partner when the report's <c>ChannelsRead</c> is false.
    /// </summary>
    public string Channel { get; set; } = string.Empty;

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

    public List<ItemVolumeSalesItemResult> Items { get; set; } = new();
}
