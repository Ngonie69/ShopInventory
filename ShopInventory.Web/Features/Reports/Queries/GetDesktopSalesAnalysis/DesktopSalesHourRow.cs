namespace ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesAnalysis;

/// <summary>The takings rung up in one hour of the counter's day (CAT), summed across the period.</summary>
public sealed class DesktopSalesHourRow
{
    public int Hour { get; set; }

    public int SalesCount { get; set; }

    public decimal TotalAmount { get; set; }
}
