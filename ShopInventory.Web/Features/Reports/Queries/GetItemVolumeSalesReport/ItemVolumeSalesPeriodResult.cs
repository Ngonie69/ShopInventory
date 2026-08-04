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
    public decimal NetRevenueUsd { get; set; }
    public decimal NetRevenueZig { get; set; }

    public List<ItemVolumeSalesAccountResult> Accounts { get; set; } = new();
}
