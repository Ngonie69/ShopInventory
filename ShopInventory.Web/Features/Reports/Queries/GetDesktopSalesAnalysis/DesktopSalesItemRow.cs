namespace ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesAnalysis;

/// <summary>What one item sold for across the period, before VAT.</summary>
public sealed class DesktopSalesItemRow
{
    public string ItemCode { get; set; } = string.Empty;

    public string? ItemDescription { get; set; }

    public decimal Quantity { get; set; }

    public decimal NetAmount { get; set; }

    /// <summary>How many sales included it, not how many lines.</summary>
    public int SalesCount { get; set; }

    public decimal ShareOfNetPercent { get; set; }
}
