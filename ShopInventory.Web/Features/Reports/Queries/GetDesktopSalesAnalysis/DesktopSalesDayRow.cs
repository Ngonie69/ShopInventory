namespace ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesAnalysis;

/// <summary>One business day's takings, split by payment method.</summary>
public sealed class DesktopSalesDayRow
{
    public DateTime Date { get; set; }

    public int SalesCount { get; set; }

    public decimal TotalAmount { get; set; }

    public decimal VatAmount { get; set; }

    public List<DesktopSalesPaymentAmount> ByPaymentMethod { get; set; } = [];
}
