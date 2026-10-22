namespace ShopInventory.Web.Features.Reports.Queries.GetItemVolumeSalesReport;

/// <summary>
/// One day, week, or month of the window, with the accounts that moved inside it.
/// </summary>
public sealed class ItemVolumeSalesPeriodResult
{
    public string Label { get; set; } = string.Empty;
    public DateTime PeriodStartUtc { get; set; }
    public DateTime PeriodEndUtc { get; set; }

    public int InvoiceCount { get; set; }
    public int CreditNoteCount { get; set; }

    public decimal NetQuantity { get; set; }
    public decimal NetVolume { get; set; }
    // Money is USD, every document included. SAP's line total is in the company's local currency,
    // USD, so a ZiG document's lines arrive already converted at that document's own rate — what
    // SAP's DocTotal carries. The FromZig figures are the part that came off ZiG documents: already
    // inside the USD figure beside them, and never to be added to it.
    public decimal NetRevenueUsd { get; set; }
    public decimal NetRevenueFromZig { get; set; }

    public List<ItemVolumeSalesAccountResult> Accounts { get; set; } = new();

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
