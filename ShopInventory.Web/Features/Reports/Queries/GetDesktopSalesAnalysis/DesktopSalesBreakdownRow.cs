namespace ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesAnalysis;

/// <summary>The takings of one shop, one source or one operator, split by payment method.</summary>
public sealed class DesktopSalesBreakdownRow
{
    public string Key { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public int SalesCount { get; set; }

    public decimal TotalAmount { get; set; }

    public decimal VatAmount { get; set; }

    public decimal ShareOfValuePercent { get; set; }

    public List<DesktopSalesPaymentAmount> ByPaymentMethod { get; set; } = [];
}
